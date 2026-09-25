using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace RequestAssociation;

internal static class Program
{
    private static int assertions;

    public static async Task Main()
    {
        using var manual = LocalHost.Manual;
        Console.WriteLine(
            $"Runtime: {RuntimeInformation.FrameworkDescription}; ASP.NET: {typeof(HttpContext).Assembly.Location}"
        );
        Console.WriteLine("registration | sampling | handoff | result");
        foreach (
            var registration in new[] { "fallback", "user-first", "candidate-first", "existing" }
        )
            await Run(
                registration,
                SamplingDecision.RecordAndSample,
                "native",
                full: registration == "candidate-first"
            );
        foreach (var order in new[] { "reverse", "race" })
            await Run("candidate-first", SamplingDecision.RecordAndSample, order);
        foreach (var sampling in new[] { SamplingDecision.Drop, SamplingDecision.RecordOnly })
            await Run("candidate-first", sampling, "native");
        await Run("candidate-first", SamplingDecision.Drop, "native", remoteUnsampled: true);
        Console.WriteLine(
            $"PASS {assertions} assertions; all hosts, providers, late tasks and batch workers disposed"
        );
    }

    public static void Check(bool condition, string message)
    {
        Interlocked.Increment(ref assertions);
        if (!condition)
            throw new InvalidOperationException("ASSERT: " + message);
    }

    private static async Task Run(
        string registration,
        SamplingDecision sampling,
        string order,
        bool full = false,
        bool remoteUnsampled = false
    )
    {
        var probe = new Probe { HandoffMode = order };
        var fixture = new Fixture(probe)
        {
            Outgoing = sampling == SamplingDecision.RecordAndSample,
            RemoteUnsampled = remoteUnsampled,
        };
        using var appExporter = new AppExporter(probe);
        var appLogs = new AppLogs();
        using var existing =
            registration == "existing" ? LocalHost.BuildExisting(fixture, appExporter) : null;
        LocalHost? host = null;
        try
        {
            host = await LocalHost.Start(
                probe,
                fixture,
                appExporter,
                appLogs,
                registration,
                sampling,
                existing
            );
            await Completed(probe, "first");
            await probe.Drain();
            Check(fixture.EarlyBeforeStarted, "first request before ApplicationStarted");
            var runtime = host.Services.GetRequiredService<CandidateRuntime>();
            Check(runtime.OwnsProvider == (registration == "fallback"), "provider ownership");
            Check(
                probe.BuilderConfigured == (registration is "user-first" or "candidate-first"),
                "builder callback path"
            );
            if (registration != "fallback")
            {
                Check(
                    runtime
                        .Provider!.GetResource()
                        .Attributes.Any(pair =>
                            pair.Key == "app.resource" && Equals(pair.Value, "untouched")
                        ),
                    "application custom resource preserved"
                );
                Check(
                    ReferenceEquals(existing, runtime.Provider) == (registration == "existing"),
                    "existing instance resolved from DI"
                );
            }
            var helpers = host.Services.GetRequiredService<RequestHelpers>();
            var submitted = probe.Batch.Submitted;
            helpers.SetConsumer("outside");
            helpers.SetAttribute("id", "outside");
            helpers.CaptureException(new Exception("outside"));
            Check(
                probe.Batch.Submitted == submitted && probe.Accessor.HttpContext is null,
                "out-of-request helpers are no-ops"
            );

            if (sampling == SamplingDecision.RecordAndSample)
            {
                using (
                    var unrelated = LocalHost.Manual.StartActivity(
                        "unrelated",
                        ActivityKind.Internal,
                        new ActivityContext(
                            probe.Observations["first"].TraceId,
                            ActivitySpanId.CreateFromString("cccccccccccccccc"),
                            ActivityTraceFlags.Recorded
                        )
                    )
                )
                {
                    fixture.Record(unrelated, "outside");
                    fixture.Log(
                        host.Services.GetRequiredService<ILoggerFactory>()
                            .CreateLogger(CaptureAdapter.Category),
                        "outside"
                    );
                    helpers.SetConsumer("outside-child");
                    helpers.CaptureException(new Exception("outside-child"));
                }
                await probe.Drain();
                Check(
                    probe.Batch.Submitted == submitted,
                    "same trace alone cannot associate unrelated out-of-request span/log/helper"
                );
                CheckKept(probe, fixture, "first");
                Check(
                    fixture.BeforeMiddleware.All(value => value),
                    "enrichment child and log precede middleware"
                );
                Check(
                    fixture.ExplicitParentsNull.All(value => value)
                        && !fixture.ExplicitParentsNull.IsEmpty,
                    "explicit ActivityContext child has no Activity.Parent object"
                );
                Check(
                    probe.Observations["first"].StartTagsAbsent,
                    "SERVER tags absent at real OnStart"
                );
                Check(
                    order != "reverse"
                        || probe
                            .Observations["first"]
                            .Processed.SequenceEqual(new[] { "server", "transport" }),
                    "controlled reverse processing order"
                );
            }
            else
            {
                var observation = probe.Observations["first"];
                Check(
                    !observation.Sampled
                        && probe.Exporter.Records.IsEmpty
                        && appExporter.Records.IsEmpty,
                    "user sampling produces no candidate or app exported detail"
                );
                Check(
                    fixture.Started.All(span => !span.Recorded),
                    "no extra listener promotes recording"
                );
                CheckInputs(observation, "first");
                Check(
                    observation.Transport!.Helpers["exception.message"] as string == "first-first",
                    "first exception retained independently of sampling"
                );
            }

            if (full)
                await Integrated(host, probe, fixture);
            CheckApp(probe, fixture, appExporter, appLogs, registration != "fallback");
            Check(
                probe.RetainedPayload == 0,
                "no request payload retained after completed release/drop"
            );
            Console.WriteLine(
                $"{registration} | {(remoteUnsampled ? "parent-unsampled" : sampling)} | {order} | PASS; native first="
                    + string.Join(",", probe.Observations["first"].Native)
                    + "; processed="
                    + string.Join(",", probe.Observations["first"].Processed)
            );

            await host.DisposeAsync();
            host = null;
            Check(!probe.Enabled, "host disposal disables attached capture");
            if (existing is not null)
            {
                Check(!appExporter.Disposed, "host does not dispose existing-instance provider");
                var privateCount = probe.Batch.Submitted;
                var applicationCount = appExporter.Records.Count;
                using (var activity = LocalHost.Manual.StartActivity("after-host-disposal"))
                    Check(
                        activity?.Recorded == true,
                        "external provider remains subscribed and sampled"
                    );
                Check(
                    appExporter.Records.Count == applicationCount + 1,
                    "external app exporter continues after host disposal"
                );
                Check(
                    probe.Batch.Submitted == privateCount,
                    "disabled candidate captures no external work"
                );
            }
            await probe.Drain();
        }
        finally
        {
            foreach (var gate in fixture.Gates.Values)
                gate.Continue.TrySetResult();
            foreach (var late in fixture.Late.Values)
                late.Continue.TrySetResult();
            try
            {
                await Task.WhenAll(fixture.Late.Values.Select(value => value.Task))
                    .WaitAsync(TimeSpan.FromSeconds(7));
            }
            finally
            {
                try
                {
                    if (host is not null)
                        await host.DisposeAsync();
                    await Task.WhenAll(probe.Handoffs.ToArray()).WaitAsync(TimeSpan.FromSeconds(5));
                }
                finally
                {
                    probe.Stop();
                }
            }
        }
    }

    private static async Task Integrated(LocalHost host, Probe probe, Fixture fixture)
    {
        foreach (var mode in new[] { "request-drop", "response-drop", "keep", "abstain" })
        {
            await host.Request("/" + mode + "/" + mode);
            await Completed(probe, mode);
            await probe.Drain();
            CheckInputs(probe.Observations[mode], mode);
            if (mode.EndsWith("drop"))
            {
                Check(ForRequest(probe, mode).Length == 0, mode + " releases no private detail");
                Check(probe.Observations[mode].ReleaseCount == 0, mode + " cannot be revived");
                Check(
                    probe.Observations[mode].Decision!.Verdict
                        == (mode == "request-drop" ? "keep" : "drop"),
                    "request-stage drop wins over response keep"
                );
            }
            else
                CheckKept(probe, fixture, mode);
        }
        Console.WriteLine(
            "  PASS request drop/response keep, response drop, keep, abstain; final response inputs"
        );

        await host.Request("/keep/reuse-a");
        await Completed(probe, "reuse-a");
        await host.Request("/keep/reuse-b");
        await Completed(probe, "reuse-b");
        Check(
            probe.Observations["reuse-a"].Transport!.ConnectionId
                == probe.Observations["reuse-b"].Transport!.ConnectionId,
            "sequential HTTP/1.1 requests reuse observed connection ID"
        );
        const string remote = "00-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-bbbbbbbbbbbbbbbb-01";
        await host.Request("/keep/trace-a", remote);
        await Completed(probe, "trace-a");
        await host.Request("/keep/trace-b", remote);
        await Completed(probe, "trace-b");
        Check(
            probe.Observations["trace-a"].TraceId == probe.Observations["trace-b"].TraceId
                && probe.Observations["trace-a"].ServerId != probe.Observations["trace-b"].ServerId,
            "same remote trace has distinct inbound request roots"
        );

        fixture.Gates["overlap-a"] = new WorkGate();
        fixture.Gates["overlap-b"] = new WorkGate();
        using (
            var a = new HttpClient(
                new SocketsHttpHandler { UseProxy = false, ActivityHeadersPropagator = null }
            )
            {
                Timeout = TimeSpan.FromSeconds(7),
            }
        )
        using (
            var b = new HttpClient(
                new SocketsHttpHandler { UseProxy = false, ActivityHeadersPropagator = null }
            )
            {
                Timeout = TimeSpan.FromSeconds(7),
            }
        )
        {
            var requestA = LocalHost.Send(a, host.Address + "/keep/overlap-a", null);
            var requestB = LocalHost.Send(b, host.Address + "/keep/overlap-b", null);
            await Task.WhenAll(
                    fixture.Gates["overlap-a"].Entered.Task,
                    fixture.Gates["overlap-b"].Entered.Task
                )
                .WaitAsync(TimeSpan.FromSeconds(5));
            Check(
                !requestA.IsCompleted && !requestB.IsCompleted,
                "deterministic concurrent overlap"
            );
            fixture.Gates["overlap-a"].Continue.SetResult();
            fixture.Gates["overlap-b"].Continue.SetResult();
            await Task.WhenAll(requestA, requestB).WaitAsync(TimeSpan.FromSeconds(7));
        }
        foreach (
            var id in new[] { "reuse-a", "reuse-b", "trace-a", "trace-b", "overlap-a", "overlap-b" }
        )
            await Completed(probe, id);
        await probe.Drain();
        foreach (
            var id in new[] { "reuse-a", "reuse-b", "trace-a", "trace-b", "overlap-a", "overlap-b" }
        )
            CheckKept(probe, fixture, id);
        Console.WriteLine(
            "  PASS same trace/different roots, deterministic concurrency, HTTP/1.1 same-connection reuse"
        );

        foreach (var mode in new[] { "keep", "response-drop" })
        {
            var id = "late-" + mode;
            var late = fixture.Late[id] = new LateWork();
            await host.Request("/" + mode + "/" + id);
            await Completed(probe, id);
            await Task.WhenAll(
                    probe.Observations[id].NativeServer.Task,
                    probe.Observations[id].NativeTransport.Task
                )
                .WaitAsync(TimeSpan.FromSeconds(5));
            Check(!late.Task.IsCompleted, "real child remains live after both native completions");
            await probe.Drain();
            var before = ForRequest(probe, id).Length;
            late.Continue.SetResult();
            await late.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await probe.Drain();
            if (mode == "keep")
            {
                Check(
                    ForRequest(probe, id).Length == before + 2,
                    "late child and log follow cached keep decision"
                );
                CheckKept(probe, fixture, id);
            }
            else
                Check(
                    before == 0 && ForRequest(probe, id).Length == 0,
                    "late child and log follow cached drop decision"
                );
            Check(
                probe.Observations[id].DecisionCount == 1,
                "late detail does not rerun response decision"
            );
        }

        var expiry = fixture.Late["expiry"] = new LateWork();
        await host.Request("/keep/expiry");
        await Completed(probe, "expiry");
        await probe.Drain();
        var expiryCount = ForRequest(probe, "expiry").Length;
        Check(
            probe.MapCount > 0 && probe.RetainedPayload == 0,
            "only ID metadata retained after release"
        );
        probe.Clock.Advance(TimeSpan.FromSeconds(59));
        probe.Cleanup();
        Check(probe.MapCount > 0, "POC-only completed-root retention before expiry");
        probe.Clock.Advance(TimeSpan.FromSeconds(1));
        probe.Cleanup();
        Check(
            probe.MapCount == 0,
            "POC-only expiry evicts completed roots including a live late child"
        );
        expiry.Continue.SetResult();
        await expiry.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await probe.Drain();
        Check(
            ForRequest(probe, "expiry").Length == expiryCount,
            "expired late child/log are ignored and do not revive request"
        );
        Console.WriteLine(
            "  PASS real late keep/drop and POC-only 60s metadata expiry, with active late-child eviction"
        );

        probe.SpanCap = 5;
        probe.LogCap = 6;
        var cap = fixture.Late["cap"] = new LateWork();
        await host.Request("/keep/cap");
        await Completed(probe, "cap");
        await probe.Drain();
        var capBefore = ForRequest(probe, "cap").Length;
        Check(capBefore == 12, "five descendants plus SERVER and six logs consume cumulative caps");
        cap.Continue.SetResult();
        await cap.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await probe.Drain();
        Check(
            ForRequest(probe, "cap").Length == capBefore,
            "late span/log cannot bypass cumulative caps"
        );
        probe.SpanCap = 16;
        probe.LogCap = 16;

        var cutoffGate = fixture.Gates["cutoff"] = new WorkGate();
        var cutoffRequest = host.Request("/keep/cutoff");
        await cutoffGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(
            !cutoffRequest.IsCompleted && probe.Observations["cutoff"].Decision is null,
            "cutoff while real request unresolved"
        );
        Check(
            ForRequest(probe, "cutoff").Length == 0,
            "before-middleware detail stays buffered while request is live"
        );
        probe.Cutoff();
        cutoffGate.Continue.SetResult();
        await cutoffRequest.WaitAsync(TimeSpan.FromSeconds(7));
        await Completed(probe, "cutoff");
        await probe.Drain();
        Check(
            ForRequest(probe, "cutoff").Length == 0,
            "explicit cutoff drops buffered and later unresolved detail"
        );
        CheckInputs(probe.Observations["cutoff"], "cutoff", decision: false);
        Check(probe.MapCount == 0, "cutoff clears all association IDs");
        Console.WriteLine(
            "  PASS cumulative late caps and explicit cutoff; independent transport/helper inputs preserved"
        );
    }

    private static async Task Completed(Probe probe, string id)
    {
        Check(
            probe.Observations.TryGetValue(id, out var observation),
            "real request observation exists: " + id
        );
        await observation!.Done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        foreach (var sink in probe.Observations.Values.Where(value => value.Id == id + "-sink"))
            await sink.Done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        probe.CheckFailures();
    }

    private static Owned[] ForRequest(Probe probe, string id)
    {
        var serverId = probe.Observations[id].ServerId.ToHexString();
        return probe
            .Exporter.Records.Where(record =>
                record.Attributes[Probe.ServerLink] as string == serverId
            )
            .ToArray();
    }

    private static void CheckInputs(Observation observation, string id, bool decision = true)
    {
        var transport = observation.Transport!;
        Check(
            transport.DurationMilliseconds >= 0
                && transport.RequestBytes is null
                && transport.ResponseBytes == 2,
            "sampling-independent duration and transport size inputs: " + id
        );
        Check(
            transport.Status == 202
                && transport.Route == "/{mode}/{id}"
                && transport.Method == "GET",
            "final transport fields: " + id
        );
        Check(
            transport.Helpers["apitally.consumer"] as string == "consumer-" + id
                && transport.Helpers["apitally.custom.id"] as string == id,
            "request-local helper inputs: " + id
        );
        Check(
            transport.Helpers["exception.type"] as string
                == typeof(InvalidOperationException).FullName
                && transport.Helpers["exception.message"] as string == "first-" + id,
            "first exception input: " + id
        );
        Check(
            observation.EarlyMethod == "GET"
                && observation.EarlyPath!.EndsWith("/" + id)
                && observation.EarlyUserAgent == "request-association-poc/1",
            "private early request fields: " + id
        );
        if (!decision)
            return;
        Check(
            observation.DecisionCount == 1 && observation.Decision!.Transport == transport,
            "one decision sees final transport: " + id
        );
        if (observation.Sampled)
        {
            var server = observation.Decision!.Server!;
            Check(
                server.Attributes["http.request.method"] as string == transport.Method
                    && Convert.ToInt32(server.Attributes["http.response.status_code"])
                        == transport.Status
                    && server.Attributes["http.route"] as string == transport.Route,
                "decision sees final SERVER transport fields: " + id
            );
            Check(
                server.Attributes.TryGetValue("http.response.body.size", out var responseSize)
                    && Equals(responseSize, transport.ResponseBytes),
                "decision SERVER includes final transport response size: " + id
            );
            Check(
                !server.Attributes.ContainsKey("http.request.body.size"),
                "decision SERVER omits unknown request size: " + id
            );
            foreach (var pair in transport.Helpers)
                Check(
                    Equals(server.Attributes[pair.Key], pair.Value),
                    "decision sees final SERVER helper: " + pair.Key
                );
        }
    }

    private static void CheckKept(Probe probe, Fixture fixture, string id)
    {
        CheckInputs(probe.Observations[id], id);
        var observation = probe.Observations[id];
        var records = ForRequest(probe, id);
        var spans = records.Where(record => record.Signal == "span").ToArray();
        var expected = fixture.Started.Where(span => span.Request == id && span.Recorded).ToArray();
        Check(
            spans.Length == expected.Length + 1,
            "exact descendant plus SERVER span count: " + id
        );
        Check(
            spans.Select(span => span.SpanId).Distinct().Count() == spans.Length,
            "no duplicate span IDs: " + id
        );
        foreach (var span in expected)
        {
            var actual = spans.Single(value => value.SpanId == span.SpanId);
            Check(
                actual.TraceId == span.TraceId
                    && actual.ParentSpanId == span.ParentSpanId
                    && actual.Name == span.Name,
                "exact exported span identity/parentage: " + span.Name
            );
        }
        var server = spans.Single(span => span.SpanId == observation.ServerId);
        var transport = observation.Transport!;
        Check(
            server.Attributes["http.request.method"] as string == transport.Method
                && Convert.ToInt32(server.Attributes["http.response.status_code"])
                    == transport.Status
                && server.Attributes["http.route"] as string == transport.Route,
            "actual exported SERVER contains final transport fields: " + id
        );
        Check(
            server.Attributes.TryGetValue("http.response.body.size", out var responseSize)
                && Equals(responseSize, transport.ResponseBytes),
            "actual exported SERVER includes final transport response size: " + id
        );
        Check(
            !server.Attributes.ContainsKey("http.request.body.size"),
            "actual exported SERVER omits unknown request size: " + id
        );
        var serverIndex = Array.IndexOf(records, server);
        var lateSpans = spans
            .Where(span => fixture.Late.ContainsKey(id) && span.Name == "late-" + id)
            .ToArray();
        var initialDescendants = spans
            .Where(span => span != server && !lateSpans.Contains(span))
            .ToArray();
        Check(
            initialDescendants.All(span => Array.IndexOf(records, span) < serverIndex),
            "actual exported initial descendants precede SERVER: " + id
        );
        Check(
            records
                .Where(record => record.Signal == "log")
                .All(log => Array.IndexOf(records, log) > serverIndex),
            "actual exported logs follow SERVER: " + id
        );
        Check(
            initialDescendants
                .Select(span => span.Name)
                .SequenceEqual(
                    new[] { "before-" + id, "nested-" + id, "ids-" + id, "GET", "child-" + id }
                ),
            "actual exported initial descendants preserve end arrival order: " + id
        );
        if (lateSpans.Length > 0)
            Check(
                lateSpans.All(span => Array.IndexOf(records, span) > serverIndex),
                "actual exported genuine late spans follow SERVER: " + id
            );
        var child = spans.Single(span => span.Name == "child-" + id);
        Check(
            child.ParentSpanId == server.SpanId
                && spans.Single(span => span.Name == "before-" + id).ParentSpanId == server.SpanId
                && spans.Single(span => span.Name == "nested-" + id).ParentSpanId == child.SpanId
                && spans.Single(span => span.Name == "ids-" + id).ParentSpanId == server.SpanId,
            "structural manual child parentage: " + id
        );
        Check(
            spans.Single(span => span.Name == "GET").ParentSpanId == child.SpanId,
            "instrumented HttpClient span has real manual parent: " + id
        );
        if (id.StartsWith("trace-"))
            Check(
                server.ParentSpanId.ToHexString() == "bbbbbbbbbbbbbbbb",
                "inbound parent is the supplied remote span ID"
            );
        Check(
            server.Attributes["apitally.consumer"] as string == "consumer-" + id,
            "helper targets SERVER rather than current child"
        );
        Check(
            spans
                .Where(span => span != server)
                .All(span => !span.Attributes.ContainsKey("apitally.consumer")),
            "children not mutated by SERVER helper"
        );
        var logs = records.Where(record => record.Signal == "log").ToArray();
        var expectedLogs = fixture.Logs.Where(log => log.Name.EndsWith("-" + id)).ToArray();
        Check(logs.Length == expectedLogs.Length, "exact private log count: " + id);
        Check(
            logs.Select(log => log.Attributes["event"] as string)
                .SequenceEqual(expectedLogs.Select(log => log.Name)),
            "actual exported logs preserve arrival order: " + id
        );
        foreach (var log in expectedLogs)
        {
            var actual = logs.Single(record => record.Attributes["event"] as string == log.Name);
            Check(
                actual.TraceId == log.TraceId && actual.SpanId == log.SpanId,
                "actual native log trace/span ID: " + log.Name
            );
            Check(
                actual.Name == "masked" && actual.Attributes["secret"] as string == "[redacted]",
                "native callback mutations survive owned copy"
            );
            Check(
                actual.Attributes[Probe.ServerLink] as string == observation.ServerId.ToHexString(),
                "explicit private SERVER log linkage"
            );
        }
        Check(
            observation.ReleaseCount == 1 && observation.DecisionCount == 1,
            "release and decision happen once"
        );
        Check(
            observation.CompletionsAtRelease == 2,
            "no detail release before both completions processed"
        );
        Check(
            observation.Native.Count == 2 && observation.Processed.Count == 2,
            "both real completions observed and processed once"
        );
    }

    private static void CheckApp(
        Probe probe,
        Fixture fixture,
        AppExporter appExporter,
        AppLogs appLogs,
        bool appTracing
    )
    {
        var expectedLogs = fixture.Logs.ToArray();
        var actualLogs = appLogs.Records.ToArray();
        Check(actualLogs.Length == expectedLogs.Length, "independent app log exact count");
        foreach (var expected in expectedLogs)
        {
            var actual = actualLogs.Single(log =>
                log.Attributes["event"] as string == expected.Name
            );
            Check(
                actual.TraceId == expected.TraceId && actual.SpanId == expected.SpanId,
                "independent app log IDs unchanged"
            );
            Check(
                actual.Body.Contains("private-value")
                    && actual.Attributes["secret"] as string == "private-value"
                    && !actual.Attributes.ContainsKey(Probe.ServerLink),
                "private masking/linkage does not change independent app sink"
            );
        }
        if (!appTracing)
            return;
        var expectedSpans = fixture
            .Started.Where(span => span.Recorded)
            .Select(span => span.SpanId)
            .Concat(
                probe
                    .Observations.Values.Where(observation => observation.Sampled)
                    .Select(observation => observation.ServerId)
            )
            .ToArray();
        appExporter.Wait(expectedSpans.Length);
        var actualSpans = appExporter.Records.ToArray();
        Check(
            actualSpans.Length == expectedSpans.Length
                && actualSpans.Select(span => span.SpanId).ToHashSet().SetEquals(expectedSpans),
            "independent app span exact IDs/count including private-drop and post-cutoff requests"
        );
        Check(
            actualSpans.All(span =>
                !span.Attributes.Keys.Any(key => key.StartsWith("apitally."))
                && !span.Attributes.ContainsKey("exception.message")
            ),
            "private helpers never mutate application activities"
        );
        foreach (var root in probe.Observations.Values.Where(observation => observation.Sampled))
        {
            var server = actualSpans.Single(span => span.SpanId == root.ServerId);
            Check(
                server.Attributes["user.enrichment"] as string == "untouched",
                "application enrichment preserved"
            );
            Check(
                !server.Attributes.ContainsKey("http.request.body.size")
                    && !server.Attributes.ContainsKey("http.response.body.size"),
                "transport body-size injection stays private: " + root.Id
            );
        }
    }
}
