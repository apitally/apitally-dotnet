using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

internal static class Program
{
    private static readonly AsyncLocal<bool> ServingRequest = new();
    private static int checks;

    private static int Main()
    {
        try
        {
            Console.WriteLine(
                $"Runtime: {RuntimeInformation.FrameworkDescription}; {RuntimeInformation.OSArchitecture}"
            );
            Console.WriteLine(
                $"OpenTelemetry: {Version(typeof(Sdk))}; DiagnosticSource: {Version(typeof(Activity))}"
            );
            SnapshotIsolation(userFirst: true);
            SnapshotIsolation(userFirst: false);
            Sampling();
            BatchLifecycle();
            DisposeWithoutShutdown();
            Coordination();
            Console.WriteLine($"PASS: all 6 groups; {checks} assertions");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL: {exception}");
            return 1;
        }
    }

    private static void SnapshotIsolation(bool userFirst)
    {
        var requestThread = Environment.CurrentManagedThreadId;
        string[] userHeader = ["synthetic-secret", "second"];
        long[] userNumbers = [7, 8];
        bool[] eventFlags = [true, false];
        double[] linkNumbers = [1.5, 2.5];
        string[] resourceLabels = ["user-original"];
        byte[] userBytes = [1, 2, 3];
        var resource = ResourceBuilder
            .CreateEmpty()
            .AddAttributes(
                new Dictionary<string, object>
                {
                    ["service.name"] = "synthetic-service",
                    ["service.instance.id"] = "user-instance",
                    ["deployment.environment.name"] = "user-env",
                    ["user.labels"] = resourceLabels,
                }
            )
            .Build();
        using var userEntered = new ManualResetEventSlim(false);
        using var privateFinished = new ManualResetEventSlim(false);
        var privateExporter = new ProbeExporter<SpanSnapshot>(snapshot =>
        {
            try
            {
                Check(userEntered.Wait(5000), "user and private exporters overlap");
                Check(
                    Environment.CurrentManagedThreadId != requestThread && !ServingRequest.Value,
                    "body CPU processing is off request thread and execution context"
                );
                Check(Activity.Current is null, "export worker has no request Activity.Current");
                snapshot.Tags["http.request.header.authorization"] = new[] { "[REDACTED]" };
                snapshot.Tags["url.full"] = "https://synthetic.invalid/items?token=[REDACTED]";
                ((string[])snapshot.Tags["http.request.header.x-user"]!)[0] = "[REDACTED]";
                ((long[])snapshot.Tags["user.numbers"]!)[0] = -1;
                ((byte[])snapshot.Tags["user.bytes"]!)[0] = 0;
                ((bool[])snapshot.Events.Single().Tags["flags"]!)[0] = false;
                ((double[])snapshot.Links.Single().Tags["numbers"]!)[0] = -1;
                ((string[])snapshot.Resource["user.labels"]!)[0] = "private-only";
                snapshot.Resource["service.instance.id"] = "private-instance";
                snapshot.Resource["deployment.environment.name"] = "private-env";

                // This inline block stands in for a body callback, not its public signature.
                Check(
                    !snapshot.Tags.ContainsKey("http.request.body"),
                    "callback sees no body attribute"
                );
                Check(
                    ((string[])snapshot.Tags["http.request.header.authorization"]!).SequenceEqual([
                        "[REDACTED]",
                    ])
                        && (string)snapshot.Tags["url.full"]!
                            == "https://synthetic.invalid/items?token=[REDACTED]"
                        && (string)snapshot.Tags["http.route"]! == "/items/{id}",
                    "callback sees redacted headers/query and late enrichment"
                );
                var rawBody = snapshot.RawBody!;
                Check(rawBody.Length == 50_000, "complete maximum-size raw body");
                _ = SHA256.HashData(rawBody);
                var body = JsonNode.Parse(rawBody)!;
                body["password"] = "[REDACTED]";
                snapshot.Tags["http.request.body"] = body.ToJsonString();
                snapshot.RawBody = null;
                Check(
                    body["padding"]!.GetValue<string>().Length == 49_963,
                    "body padding not truncated"
                );
            }
            finally
            {
                privateFinished.Set();
            }
        });
        var userExporter = new ProbeExporter<Activity>(activity =>
        {
            userEntered.Set();
            Check(privateFinished.Wait(5000), "user exporter reads after private mutation");
            Check(
                !activity.TagObjects.Any(t =>
                    t.Key
                        is "http.request.header.authorization"
                            or "http.request.body"
                            or "http.route"
                            or "apitally.exception.sentry_event_id"
                ),
                "private headers/body/enrichment invisible to user"
            );
            Check(
                (string)activity.GetTagItem("url.full")!
                    == "https://synthetic.invalid/items?token=synthetic",
                "user URL unchanged"
            );
            Check(
                ((string[])activity.GetTagItem("http.request.header.x-user")!).SequenceEqual([
                    "synthetic-secret",
                    "second",
                ])
                    && ((long[])activity.GetTagItem("user.numbers")!).SequenceEqual([7L, 8L])
                    && ((byte[])activity.GetTagItem("user.bytes")!).SequenceEqual(
                        new byte[] { 1, 2, 3 }
                    ),
                "mutable activity arrays unchanged"
            );
            Check(
                (
                    (bool[])activity.Events.Single().Tags.Single(t => t.Key == "flags").Value!
                ).SequenceEqual([true, false])
                    && (
                        (double[])
                            activity.Links.Single().Tags!.Single(t => t.Key == "numbers").Value!
                    ).SequenceEqual([1.5, 2.5]),
                "mutable event and link arrays unchanged"
            );
            Check(
                resourceLabels.SequenceEqual(["user-original"])
                    && (string)resource.Attributes.Single(t => t.Key == "service.instance.id").Value
                        == "user-instance"
                    && (string)
                        resource
                            .Attributes.Single(t => t.Key == "deployment.environment.name")
                            .Value == "user-env",
                "user resource unchanged"
            );
        });

        ServingRequest.Value = true;
        SnapshotBatchProcessor batch;
        BatchActivityExportProcessor userBatch;
        // First-request activation can create workers under a request ExecutionContext.
        using (ExecutionContext.SuppressFlow())
        {
            batch = new SnapshotBatchProcessor(privateExporter, batchSize: 1);
            userBatch = new BatchActivityExportProcessor(userExporter, 32, 1000, 1000, 1);
        }
        using (batch)
        {
            SpanSnapshot? captured = null;
            var bridge = new SnapshotProcessor(snapshot => captured = snapshot);
            var builder = Sdk.CreateTracerProviderBuilder()
                .AddSource("poc.snapshot")
                .SetSampler(new AlwaysOnSampler())
                .SetResourceBuilder(
                    ResourceBuilder.CreateEmpty().AddAttributes(resource.Attributes)
                );
            if (userFirst)
            {
                builder.AddProcessor(userBatch).AddProcessor(bridge);
            }
            else
            {
                builder.AddProcessor(bridge).AddProcessor(userBatch);
            }
            using var provider = builder.Build();
            using var source = new ActivitySource("poc.snapshot", "0.99.0");
            var parent = new ActivityContext(
                ActivityTraceId.CreateRandom(),
                ActivitySpanId.CreateRandom(),
                ActivityTraceFlags.Recorded,
                "synthetic=value",
                isRemote: true
            );
            var link = new ActivityContext(
                ActivityTraceId.CreateRandom(),
                ActivitySpanId.CreateRandom(),
                ActivityTraceFlags.Recorded,
                "linked=value",
                isRemote: true
            );
            var start = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
            using var activity = source.StartActivity(
                "server",
                ActivityKind.Server,
                parent,
                tags: new ActivityTagsCollection
                {
                    ["http.request.header.x-user"] = userHeader,
                    ["user.numbers"] = userNumbers,
                    ["user.bytes"] = userBytes,
                    ["url.full"] = "https://synthetic.invalid/items?token=synthetic",
                    ["unsupported"] = new StringBuilder("synthetic"),
                },
                links:
                [
                    new ActivityLink(
                        link,
                        new ActivityTagsCollection { ["numbers"] = linkNumbers }
                    ),
                ],
                startTime: start
            )!;
            activity.DisplayName = "GET /items/{id}";
            activity.AddEvent(
                new ActivityEvent(
                    "synthetic-event",
                    start.AddMilliseconds(10),
                    new ActivityTagsCollection { ["flags"] = eventFlags }
                )
            );
            activity.SetStatus(ActivityStatusCode.Error, "synthetic-error");
            activity.SetEndTime(start.AddMilliseconds(125).UtcDateTime);
            activity.Stop();
            Check(captured is not null, "OnEnd created snapshot");
            var snapshot = captured!;
            Check(
                snapshot.TraceId == activity.TraceId
                    && snapshot.TraceId == parent.TraceId
                    && snapshot.SpanId == activity.SpanId
                    && snapshot.ParentSpanId == parent.SpanId,
                "trace/span/remote parent identity preserved"
            );
            Check(
                snapshot.SourceName == "poc.snapshot"
                    && snapshot.SourceVersion == "0.99.0"
                    && snapshot.Name == activity.DisplayName
                    && snapshot.Kind == ActivityKind.Server,
                "scope name/version, display name and kind preserved"
            );
            Check(
                snapshot.StartTimeUtc == start.UtcDateTime
                    && snapshot.Duration == TimeSpan.FromMilliseconds(125)
                    && snapshot.TraceState == "synthetic=value"
                    && snapshot.Flags == ActivityTraceFlags.Recorded,
                "start/duration/trace state/flags preserved"
            );
            Check(
                snapshot.Status == ActivityStatusCode.Error
                    && snapshot.StatusDescription == "synthetic-error",
                "status preserved"
            );
            Check(
                snapshot.Events.Single().Name == "synthetic-event"
                    && snapshot.Events.Single().Timestamp == start.AddMilliseconds(10)
                    && ((bool[])snapshot.Events.Single().Tags["flags"]!).SequenceEqual(eventFlags),
                "event fully copied"
            );
            Check(
                snapshot.Links.Single().Context == link
                    && ((double[])snapshot.Links.Single().Tags["numbers"]!).SequenceEqual(
                        linkNumbers
                    ),
                "link fully copied"
            );
            Check(
                (string)snapshot.Resource["service.name"]! == "synthetic-service"
                    && ((string[])snapshot.Resource["user.labels"]!).SequenceEqual(resourceLabels),
                "resource copied"
            );
            Check(!snapshot.Tags.ContainsKey("unsupported"), "unsupported mutable object omitted");
            snapshot.Tags["http.route"] = "/items/{id}";
            snapshot.Tags["apitally.exception.sentry_event_id"] = "synthetic-late-id";
            var json =
                "{\"password\":\"synthetic\",\"padding\":\"" + new string('x', 49_963) + "\"}";
            snapshot.RawBody = Encoding.UTF8.GetBytes(json);
            batch.OnEnd(snapshot);
            Check(batch.ForceFlush(5000) && provider.ForceFlush(5000), "both queues flushed");
            Check(
                privateExporter.Exported.Wait(5000) && userExporter.Exported.Wait(5000),
                "both exports completed"
            );
            Check(
                privateExporter.Errors.IsEmpty && userExporter.Errors.IsEmpty,
                "exporter assertions: "
                    + string.Join("; ", privateExporter.Errors.Concat(userExporter.Errors))
            );
            Check(
                privateExporter.Items.Count == 1 && userExporter.Items.Count == 1,
                $"exact export counts private={privateExporter.Items.Count} user={userExporter.Items.Count}"
            );
            Check(
                snapshot.RawBody is null
                    && JsonNode.Parse((string)snapshot.Tags["http.request.body"]!)![
                        "password"
                    ]!.GetValue<string>() == "[REDACTED]",
                "body redacted and raw payload released"
            );
            Check(batch.Shutdown(5000), "private worker shutdown");
            Console.WriteLine(
                $"PASS snapshot isolation (userFirst={userFirst}): request thread={requestThread}, "
                    + $"private worker={privateExporter.Worker!.ManagedThreadId}, user worker={userExporter.Worker!.ManagedThreadId}, body=50000 bytes"
            );
        }
        ServingRequest.Value = false;
        Check(
            privateExporter.DisposeCalls == 1 && userExporter.DisposeCalls == 1,
            "exporters disposed exactly once"
        );
    }

    private static void Sampling()
    {
        var specializedExporter = new ProbeExporter<Activity>();
        var genericExporter = new ProbeExporter<SpanSnapshot>();
        var filteredExporter = new ProbeExporter<SpanSnapshot>();
        using var generic = new SnapshotBatchProcessor(genericExporter);
        using var filtered = new SnapshotBatchProcessor(filteredExporter);
        var unfilteredBridge = new SnapshotProcessor(generic.OnEnd, requireRecorded: false);
        var filteredBridge = new SnapshotProcessor(filtered.OnEnd);
        using var provider = Sdk.CreateTracerProviderBuilder()
            .AddSource("poc.sampling")
            .SetSampler(new NameSampler())
            .AddProcessor(new BatchActivityExportProcessor(specializedExporter, 32, 1000, 1000, 8))
            .AddProcessor(unfilteredBridge)
            .AddProcessor(filteredBridge)
            .Build();
        using var source = new ActivitySource("poc.sampling");
        foreach (var name in new[] { "recorded", "record-only", "drop" })
        {
            using var activity = source.StartActivity(name, ActivityKind.Server);
            if (name == "record-only")
            {
                Check(
                    activity is { IsAllDataRequested: true, Recorded: false },
                    "RecordOnly Activity exists"
                );
            }
        }
        Check(
            provider.ForceFlush(5000) && generic.ForceFlush(5000) && filtered.ForceFlush(5000),
            "sampling flush"
        );
        Check(generic.Shutdown(5000) && filtered.Shutdown(5000), "sampling workers shutdown");
        Check(specializedExporter.Exported.Wait(5000), "specialized sampling export completed");
        Check(
            unfilteredBridge.EndCount == 2 && unfilteredBridge.RecordOnlyCount == 1,
            "provider OnEnd includes RecordOnly, excludes Drop"
        );
        Check(
            specializedExporter.Items.Select(a => a.DisplayName).SequenceEqual(["recorded"]),
            "specialized filters RecordOnly"
        );
        Check(
            genericExporter.Items.Select(s => s.Name).SequenceEqual(["recorded", "record-only"]),
            "generic exports RecordOnly without explicit guard"
        );
        Check(
            filteredExporter.Items.Select(s => s.Name).SequenceEqual(["recorded"]),
            "bridge Recorded guard restores specialized behavior"
        );
        Check(
            genericExporter.Items.Last().Flags == ActivityTraceFlags.None,
            "RecordOnly flags not changed"
        );
        Console.WriteLine(
            "PASS sampling: OnEnd=2, specialized=1, generic unfiltered=2, generic Recorded guard=1; Drop=0"
        );
    }

    private static void BatchLifecycle()
    {
        var exporter = new ProbeExporter<SpanSnapshot>();
        using var batch = new SnapshotBatchProcessor(exporter, delay: 1000);
        var elapsed = Stopwatch.StartNew();
        batch.OnEnd(EmptySnapshot("delay"));
        Check(!exporter.Exported.Wait(100), "sub-batch not exported immediately");
        Check(
            exporter.Exported.Wait(5000) && exporter.Items.Count == 1,
            "scheduled delay exports without flush"
        );
        var delay = elapsed.ElapsedMilliseconds;
        exporter.Exported.Reset();
        batch.OnEnd(EmptySnapshot("flush"));
        Check(
            batch.ForceFlush(5000) && exporter.Exported.Wait(5000) && exporter.Items.Count == 2,
            "force flush triggers export"
        );
        batch.OnEnd(EmptySnapshot("shutdown"));
        Check(batch.Shutdown(5000) && exporter.Items.Count == 3, "shutdown drains pending item");
        Check(
            exporter.Worker!.Join(1000) && exporter.ShutdownCalls == 1,
            "shutdown joins worker and forwards to exporter"
        );
        Check(!batch.Shutdown(5000), "second shutdown is no-op false");
        batch.OnEnd(EmptySnapshot("after-shutdown"));
        Check(
            !batch.ForceFlush(100) && exporter.Items.Count == 3,
            "generic intake after shutdown strands item"
        );

        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var blockingExporter = new ProbeExporter<SpanSnapshot>(_ =>
        {
            entered.Set();
            Check(release.Wait(5000), "bounded exporter test gate");
        });
        using var blockingBatch = new SnapshotBatchProcessor(
            blockingExporter,
            batchSize: 1,
            queueSize: 2
        );
        try
        {
            blockingBatch.OnEnd(EmptySnapshot("timeout"));
            Check(entered.Wait(5000), "blocking exporter entered");
            Check(
                blockingBatch.ForceFlush(100) && blockingExporter.Items.IsEmpty,
                "ForceFlush can return true while final dequeued item is still inside Export"
            );
            blockingBatch.OnEnd(EmptySnapshot("queued-first"));
            blockingBatch.OnEnd(EmptySnapshot("queued-second"));
            blockingBatch.OnEnd(EmptySnapshot("queue-full"));
            Check(
                !blockingBatch.ForceFlush(100),
                "force flush times out with queued items behind blocked Export"
            );
            Check(
                !blockingExporter.Exported.Wait(1100),
                "1000ms exporterTimeout does not cancel synchronous Export"
            );
        }
        finally
        {
            release.Set();
            Check(blockingBatch.Shutdown(5000), "timeout probe drained and joined");
        }
        Check(
            blockingExporter
                .Items.Select(s => s.Name)
                .SequenceEqual(["timeout", "queued-first", "queued-second"])
                && blockingExporter.Errors.IsEmpty,
            "bounded queue keeps earliest items and drops overflow"
        );
        Console.WriteLine(
            $"PASS batching: scheduled={delay}ms, bounded queue=2, Shutdown drains/joins; "
                + "NEGATIVE ForceFlush can return before Export completes; generic accepts after shutdown; timeout does not cancel Export"
        );
    }

    private static void DisposeWithoutShutdown()
    {
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var exporter = new ProbeExporter<SpanSnapshot>(_ =>
        {
            entered.Set();
            Check(release.Wait(5000), "dispose probe bounded exporter gate");
        });
        var batch = new SnapshotBatchProcessor(exporter, batchSize: 1);
        try
        {
            batch.OnEnd(EmptySnapshot("in-export"));
            Check(entered.Wait(5000), "dispose probe export entered");
            batch.OnEnd(EmptySnapshot("queued"));
            batch.Dispose();
            Check(
                exporter.DisposeCalls == 1 && exporter.ShutdownCalls == 0,
                "Dispose does not invoke Shutdown"
            );
            Check(exporter.Worker!.IsAlive, "Dispose returned with export worker alive");
        }
        finally
        {
            release.Set();
            Check(exporter.Worker!.Join(5000), "dispose-only probe worker joined manually");
            batch.Dispose();
        }
        Check(
            exporter.Items.Count == 1 && exporter.Errors.IsEmpty && exporter.DisposeCalls == 1,
            "Dispose-only loses queued second item; exporter disposed once"
        );
        Console.WriteLine(
            "PASS disposal probe: NEGATIVE Dispose alone does not drain/join/shutdown; pending second item lost"
        );
    }

    private static void Coordination()
    {
        foreach (var serverFirst in new[] { true, false })
        {
            foreach (var keep in new[] { true, false })
            {
                var state = new CoordinationModel(spanCap: 2, logCap: 2)
                {
                    RawBody = new byte[50_000],
                };
                state.Descendant("first");
                state.Log("first");
                if (serverFirst)
                {
                    state.CompleteServer();
                }
                else
                {
                    state.CompleteTransport(keep);
                }
                Check(state.Output.Count == 0, "first completion does not release");
                state.Descendant("second");
                state.Descendant("over-cap");
                state.Log("second");
                state.Log("over-cap");
                Check(
                    state.BufferedCount == (keep || serverFirst ? 4 : 0),
                    "earliest-arrival cap or drop"
                );
                if (serverFirst)
                {
                    state.CompleteTransport(keep);
                }
                else
                {
                    state.CompleteServer();
                }
                state.CompleteServer();
                state.CompleteTransport(!keep);
                state.Descendant("late");
                state.Log("late");
                string[] expected = keep
                    ?
                    [
                        "span:first",
                        "span:second",
                        "span:SERVER",
                        "log:first",
                        "log:second",
                        "span:late",
                        "log:late",
                    ]
                    : [];
                Check(
                    state.Output.SequenceEqual(expected),
                    "descendants/SERVER/logs ordering and late keep/drop"
                );
                Check(
                    state.RawBody is null && state.BufferedCount == 0,
                    "raw body and buffers released"
                );
                Check(
                    state.ReleaseCount == (keep ? 1 : 0) && state.ResponseDecisionCount == 1,
                    "no duplicate release or response decision"
                );
            }
        }
        Console.WriteLine(
            "PASS coordination MODEL ONLY: both completion orders x keep/drop, earliest cap=2, late items, one release"
        );
    }

    private static SpanSnapshot EmptySnapshot(string name) =>
        new(
            default,
            default,
            default,
            name,
            ActivityKind.Internal,
            "poc",
            "1",
            DateTime.UtcNow,
            TimeSpan.Zero,
            null,
            ActivityTraceFlags.Recorded,
            ActivityStatusCode.Unset,
            null,
            [],
            [],
            [],
            []
        );

    private static string? Version(Type type) =>
        type
            .Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
        Interlocked.Increment(ref checks);
    }
}
