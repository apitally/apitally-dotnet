using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NativeLogMasking;
using OpenTelemetry;
using OpenTelemetry.Logs;

Console.WriteLine(
    $"Runtime: {RuntimeInformation.FrameworkDescription}; SDK: 10.0.301; OpenTelemetry: 1.19.0"
);
using var listener = new ActivityListener
{
    ShouldListenTo = source => source.Name == "NativeLogMasking.Poc",
    Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
        ActivitySamplingResult.AllDataAndRecorded,
};
ActivitySource.AddActivityListener(listener);
using var source = new ActivitySource("NativeLogMasking.Poc");
try
{
    Tests.NativeDefaults();
    foreach (var adapterFirst in new[] { true, false })
        Tests.Isolation(source, adapterFirst);
    Tests.DropResults();
    Tests.PoolingAndLateMutation();
    Tests.UnsupportedValues();
    Tests.FormatterOnce();
    Tests.ParallelScopes(source);
    Check.Equal(0, Check.Failures.Count, "no assertions were swallowed by callback drop handling");
    Console.WriteLine(
        $"PASS: {Check.Count} assertions; all providers and batch workers disposed; no network exporters."
    );
}
catch
{
    foreach (var failure in Check.Failures)
        Console.Error.WriteLine(failure);
    throw;
}

internal static class Tests
{
    public static void NativeDefaults()
    {
        var forwarded = 0;
        var first = new Observe(record =>
        {
            Check.Equal(
                "rendered without template",
                record.Body,
                "native Body is rendered without OriginalFormat"
            );
            Check.Equal(
                record.Body,
                record.FormattedMessage,
                "IncludeFormattedMessage=false alone does not clear FormattedMessage"
            );
            return;
        });
        var next = new Observe(_ => forwarded++);
        using var provider = NativeProvider(first, next);
        provider
            .CreateLogger("Native.Defaults")
            .Log(
                LogLevel.Information,
                default,
                new List<KeyValuePair<string, object?>>(),
                null,
                static (_, _) => "rendered without template"
            );
        Check.Equal(1, forwarded, "returning from OnEnd does not stop the next processor");
        Console.WriteLine(
            "PASS negative findings: native FormattedMessage populated despite false option; OnEnd return does not drop downstream"
        );
    }

    public static void Isolation(ActivitySource source, bool adapterFirst)
    {
        using var fixture = new Fixture(adapterFirst);
        using var server = source.StartActivity("request", ActivityKind.Server)!;
        using var child = source.StartActivity("child", ActivityKind.Internal)!;
        fixture.Capture.Associations[(child.TraceId, child.SpanId)] = server.SpanId.ToHexString();
        var thread = Environment.CurrentManagedThreadId;
        var numbers = new[] { 1, 2 };
        var names = new[] { "original-name" };
        var scopeNumbers = new List<int> { 3, 4 };
        var outer = new Dictionary<string, object?>
        {
            ["event-wins"] = "outer",
            ["scope-wins"] = "outer",
            ["outer-only"] = "outer-value",
            ["scope-numbers"] = scopeNumbers,
        };
        var inner = new Dictionary<string, object?>
        {
            ["event-wins"] = "inner",
            ["scope-wins"] = "inner",
            ["scope-secret"] = "scope-original",
            ["scope-remove"] = "scope-original-remove",
        };
        var state = new Dictionary<string, object?>
        {
            ["{OriginalFormat}"] = "original {secret}",
            ["secret"] = "event-original",
            ["event-wins"] = "event",
            ["event-remove"] = "remove-original",
            ["numbers"] = numbers,
            ["names"] = names,
            ["long-text"] = "short",
            ["flag"] = true,
            ["count"] = 123L,
            ["ratio"] = 1.5,
            ["empty"] = null,
        };
        var exception = ThrowSynthetic();
        var exceptionData = new[] { 5, 6 };
        exception.Data["secret"] = exceptionData;
        var originalStack = exception.ToString();
        var callbackNumbers = new[] { 71, 72 };
        var callbackList = new List<int> { 81, 82 };
        List<KeyValuePair<string, object?>>? callbackAttributes = null;
        var formatterCalls = 0;
        fixture.Capture.Mask = record =>
        {
            Check.Normalized(record);
            Check.Equal(
                thread,
                Environment.CurrentManagedThreadId,
                "callback runs synchronously on logging thread"
            );
            Check.Equal(
                child.TraceId,
                record.TraceId,
                "native callback trace is the emitting child trace"
            );
            Check.Equal(
                child.SpanId,
                record.SpanId,
                "native callback span is the child, not SERVER"
            );
            var attributes = record.Attributes!.ToDictionary(pair => pair.Key, pair => pair.Value);
            Check.Equal(
                server.SpanId.ToHexString(),
                attributes[MaskingProcessor.ServerAttribute],
                "fixture SERVER linkage visible before callback"
            );
            Check.Equal(
                exception.GetType().FullName,
                attributes["exception.type"],
                "exception type copied to string"
            );
            Check.Equal(
                exception.Message,
                attributes["exception.message"],
                "exception message copied to string"
            );
            Check.Equal(
                originalStack,
                attributes["exception.stacktrace"],
                "actual thrown stack copied to string"
            );
            Check.True(
                originalStack.Contains(nameof(ThrowSynthetic), StringComparison.Ordinal),
                "synthetic exception has a real stack"
            );
            if (record.EventId.Id == 2)
            {
                record.Body = null;
                foreach (
                    var key in new[]
                    {
                        "exception.type",
                        "exception.message",
                        "exception.stacktrace",
                    }
                )
                    attributes.Remove(key);
                record.Attributes = attributes.ToList();
                return record;
            }
            Check.Equal(
                adapterFirst ? 1 : 2,
                formatterCalls,
                "adapter formats once before native callback, independent sink may also format"
            );
            Check.Equal(
                "original event-original",
                record.Body,
                "native callback sees rendered text, not template"
            );
            Check.Equal("event", attributes["event-wins"], "event overrides inner and outer");
            Check.Equal("inner", attributes["scope-wins"], "inner overrides outer");
            Check.Equal("outer-value", attributes["outer-only"], "outer-only fields survive");
            Check.Equal(
                "scope-name",
                attributes["scope.name"],
                "formatted scope keeps named field"
            );
            Check.True(
                !attributes.ContainsKey("Scope"),
                "plain scope has no synthesized Scope attribute"
            );
            Check.True(!attributes.Values.Contains("plain-label"), "plain scope label omitted");
            Check.True(
                !attributes.Values.Contains("formatted scope-name"),
                "formatted scope rendered label omitted"
            );
            ((int[])attributes["numbers"]!)[0] = 99;
            ((string[])attributes["names"]!)[0] = "masked-name";
            ((List<int>)attributes["scope-numbers"]!)[0] = 33;
            attributes["secret"] = "masked-secret";
            attributes.Remove("event-remove");
            attributes["scope-secret"] = "masked-scope";
            attributes.Remove("scope-remove");
            attributes.Remove("exception.type");
            attributes["exception.message"] = "masked-exception";
            attributes["exception.stacktrace"] = "masked-stack";
            attributes["callback-numbers"] = callbackNumbers;
            attributes["callback-list"] = callbackList;
            attributes["long-text"] = new string('a', 2050);
            record.Body = new string('m', 2050);
            callbackAttributes = attributes.ToList();
            record.Attributes = callbackAttributes;
            return record;
        };
        var logger = fixture.Factory.CreateLogger("App.Orders");
        using (logger.BeginScope(outer))
        using (logger.BeginScope("plain-label"))
        using (logger.BeginScope("formatted {scope.name}", "scope-name"))
        using (logger.BeginScope(inner))
        {
            logger.Log(
                LogLevel.Error,
                new EventId(1, "order"),
                state,
                exception,
                (values, error) =>
                {
                    formatterCalls++;
                    Check.True(
                        ReferenceEquals(error, exception),
                        "application formatter receives original exception"
                    );
                    return $"original {values["secret"]}";
                }
            );
        }
        Check.Equal(2, formatterCalls, "one adapter formatter call plus one independent sink call");
        Check.Equal(1, numbers[0], "native callback array mutation cannot alter application input");
        Check.Equal(
            "original-name",
            names[0],
            "native callback string-array mutation cannot alter application input"
        );
        Check.Equal(
            3,
            scopeNumbers[0],
            "native callback list mutation cannot alter application scope"
        );
        Check.Equal("event-original", state["secret"], "application structured state unchanged");
        Check.Equal(
            "scope-original",
            inner["scope-secret"],
            "application structured scope unchanged"
        );
        Check.True(
            ReferenceEquals(exceptionData, exception.Data["secret"]),
            "exception Data object identity unchanged"
        );
        Check.Equal(5, exceptionData[0], "mutable exception Data unchanged");
        var user = fixture.Sink.Records.Single();
        Check.Equal("original event-original", user.Message, "independent sink output unchanged");
        Check.True(
            ReferenceEquals(state, user.State),
            "independent sink gets original state identity"
        );
        Check.True(
            ReferenceEquals(exception, user.Exception),
            "independent sink gets original exception identity"
        );
        Check.Equal(
            5,
            user.ExceptionDataAtLog,
            "independent sink sees unchanged exception Data at call time"
        );
        Check.Equal(
            1,
            ((int[])user.Fields!["numbers"]!)[0],
            "independent sink observed original structured array"
        );
        Check.Equal(
            "event-original",
            user.Fields["secret"],
            "independent sink observed original structured string"
        );
        Check.Equal(4, user.Scopes.Count, "user scope chain is unchanged");
        Check.True(
            ReferenceEquals(outer, user.Scopes[0].Original),
            "user receives original outer scope identity"
        );
        Check.Equal(
            3,
            ((List<int>)user.Scopes[0].Fields!["scope-numbers"]!)[0],
            "user sees original scope list at call time"
        );
        Check.Equal("plain-label", user.Scopes[1].Original, "user keeps plain scope label");
        Check.Equal(
            "formatted {scope.name}",
            user.Scopes[2].Fields!["{OriginalFormat}"],
            "user keeps formatted scope template"
        );
        Check.Equal(
            "scope-name",
            user.Scopes[2].Fields!["scope.name"],
            "user keeps formatted scope field"
        );
        Check.True(
            ReferenceEquals(inner, user.Scopes[3].Original),
            "user receives original inner scope identity"
        );
        Check.Equal(
            "scope-original",
            user.Scopes[3].Fields!["scope-secret"],
            "user sees unchanged inner scope"
        );
        logger.LogError(new EventId(2), exception, "remove exception and body");
        Check.Equal(
            2,
            fixture.Capture.Pending.Count,
            "accepted owned snapshots buffered synchronously"
        );
        Check.Equal(
            0,
            fixture.Exporter.Records.Count,
            "export deferred until explicit fixture release"
        );
        numbers[1] = 200;
        names[0] = "late-application-name";
        scopeNumbers[1] = 400;
        state.Clear();
        outer.Clear();
        inner.Clear();
        exceptionData[0] = 500;
        callbackNumbers[0] = 700;
        callbackList[0] = 800;
        callbackAttributes!.Clear();
        fixture.Flush(2);
        var output = fixture.Exporter.Records.ToArray();
        var accepted = output[0];
        Check.Equal(
            new string('m', 2048),
            accepted.Body,
            "edited Body exported after ASCII truncation"
        );
        Check.Equal(
            new string('a', 2048),
            accepted.Attributes["long-text"],
            "edited string attr truncated after callback"
        );
        Check.Equal(
            "masked-secret",
            accepted.Attributes["secret"],
            "ordinary attribute edit exported"
        );
        Check.True(
            !accepted.Attributes.ContainsKey("event-remove"),
            "ordinary attribute removal exported"
        );
        Check.Equal(
            "masked-scope",
            accepted.Attributes["scope-secret"],
            "scope attribute edit exported"
        );
        Check.True(
            !accepted.Attributes.ContainsKey("scope-remove"),
            "scope attribute removal exported"
        );
        Check.Equal(
            "masked-exception",
            accepted.Attributes["exception.message"],
            "exception message edit exported"
        );
        Check.Equal(
            "masked-stack",
            accepted.Attributes["exception.stacktrace"],
            "exception stack edit exported"
        );
        Check.True(
            !accepted.Attributes.ContainsKey("exception.type"),
            "exception type removal exported"
        );
        Check.Equal(
            99,
            ((int[])accepted.Attributes["numbers"]!)[0],
            "callback mutation of isolated array exported"
        );
        Check.Equal(
            2,
            ((int[])accepted.Attributes["numbers"]!)[1],
            "late application array mutation detached"
        );
        Check.Equal(
            "masked-name",
            ((string[])accepted.Attributes["names"]!)[0],
            "callback string-array edit survives late mutation"
        );
        Check.Equal(
            33,
            ((List<int>)accepted.Attributes["scope-numbers"]!)[0],
            "callback mutation of isolated list exported"
        );
        Check.Equal(
            4,
            ((List<int>)accepted.Attributes["scope-numbers"]!)[1],
            "late application scope mutation detached"
        );
        Check.Equal(
            71,
            ((int[])accepted.Attributes["callback-numbers"]!)[0],
            "callback-owned replacement array detached before buffering"
        );
        Check.Equal(
            81,
            ((List<int>)accepted.Attributes["callback-list"]!)[0],
            "callback-owned replacement list detached before buffering"
        );
        Check.Equal(true, accepted.Attributes["flag"], "boolean scalar preserved");
        Check.Equal(123L, accepted.Attributes["count"], "long scalar preserved");
        Check.Equal(1.5, accepted.Attributes["ratio"], "double scalar preserved");
        Check.Equal<object?>(null, accepted.Attributes["empty"], "null scalar preserved");
        Check.Equal("event", accepted.Attributes["event-wins"], "event precedence survives export");
        Check.Equal("inner", accepted.Attributes["scope-wins"], "scope precedence survives export");
        Check.Equal(
            "scope-name",
            accepted.Attributes["scope.name"],
            "formatted scope field exported"
        );
        Check.True(
            !accepted.Attributes.ContainsKey("{OriginalFormat}")
                && !accepted.Attributes.ContainsKey("Scope"),
            "no template or invented scope exported"
        );
        Check.Equal(child.TraceId, accepted.TraceId, "owned trace identity preserved");
        Check.Equal(child.SpanId, accepted.SpanId, "owned child span identity preserved");
        Check.True(accepted.SpanId != server.SpanId, "SERVER not substituted for emitting span");
        Check.Equal(
            server.SpanId.ToHexString(),
            accepted.Attributes[MaskingProcessor.ServerAttribute],
            "owned fixture SERVER association preserved"
        );
        Check.Equal(ActivityTraceFlags.Recorded, accepted.TraceFlags, "trace flags copied");
        Check.Equal("App.Orders", accepted.Category, "category copied");
        Check.Equal("order", accepted.EventId.Name, "EventId name copied");
        Check.Equal(LogLevel.Error, accepted.Level, "level copied");
        Check.True(
            accepted.Timestamp > DateTime.MinValue
                && accepted.ObservedTimestamp >= accepted.Timestamp,
            "timestamps copied"
        );
        Check.Equal<string?>(null, output[1].Body, "null Body has no unmodified fallback");
        Check.True(
            !output[1]
                .Attributes.Keys.Any(key => key.StartsWith("exception.", StringComparison.Ordinal)),
            "removed exception strings are not restored"
        );
        Check.True(
            fixture.Exporter.Threads.All(id => id != thread),
            "owned in-memory exports completed on batch worker"
        );
        Console.WriteLine(
            $"PASS native callback, formatter, input/output isolation, exceptions, flattened scopes, child context; adapterFirst={adapterFirst}"
        );
    }

    public static void DropResults()
    {
        using var fixture = new Fixture(true);
        LogRecord? replacement = null;
        using var other = NativeProvider(new Observe(record => replacement = record));
        var otherLogger = other.CreateLogger("Misuse.Replacement");
        fixture.Capture.Mask = record =>
        {
            Check.Normalized(record);
            switch (record.Body)
            {
                case "null":
                    return null;
                case "throw":
                    throw new InvalidOperationException("synthetic callback failure");
                case "replacement":
                    // Deliberate misuse: another public provider lends a record while this one is active.
                    otherLogger.LogInformation("borrowed record");
                    Check.True(
                        replacement is not null && !ReferenceEquals(record, replacement),
                        "replacement is a different real native record"
                    );
                    return replacement;
                default:
                    return record;
            }
        };
        var logger = fixture.Factory.CreateLogger("App.Drops");
        foreach (var body in new[] { "keep", "null", "throw", "replacement", "keep-again" })
            logger.LogInformation("{Text}", body);
        replacement = null;
        Check.Equal(5, fixture.Capture.CallbackCalls, "all selected callback outcomes exercised");
        Check.Equal(3, fixture.Capture.CallbackDrops, "null, throwing, replacement each drop");
        Check.Equal(
            5,
            fixture.Sink.Records.Count,
            "private drops leave independent sink output intact"
        );
        fixture.Flush(2);
        Check.Equal(
            "keep|keep-again",
            string.Join('|', fixture.Exporter.Records.Select(record => record.Body)),
            "only accepted same-record snapshots reach batching/export"
        );
        Console.WriteLine(
            "PASS exact same-record acceptance; null/throw/replacement drops using public native records"
        );
    }

    public static void PoolingAndLateMutation()
    {
        using var fixture = new Fixture(true);
        LogRecord? retained = null;
        fixture.Capture.Mask = record =>
        {
            Check.Normalized(record);
            if (record.EventId.Id == 1)
            {
                // Deliberate negative-test misuse only. Callers must never retain native records.
                retained = record;
                record.Body = "masked-first";
                record.Attributes = [new("numbers", new[] { 9, 2 })];
                return record;
            }
            Check.True(
                ReferenceEquals(retained, record),
                "actual thread-local native pool reused the same record"
            );
            Check.Equal(
                "second",
                retained!.Body,
                "retained reference now describes a different call"
            );
            return null;
        };
        var logger = fixture.Factory.CreateLogger("App.Pool");
        logger.LogInformation(new EventId(1), "first");
        logger.LogInformation(new EventId(2), "second");
        retained!.Body = "late-native-mutation";
        retained.Attributes = [new("numbers", new[] { 100, 200 })];
        retained = null;
        fixture.Flush(1);
        var accepted = fixture.Exporter.Records.Single();
        Check.Equal(
            "masked-first",
            accepted.Body,
            "accepted Body survives actual reuse and late native mutation"
        );
        Check.Equal(
            9,
            ((int[])accepted.Attributes["numbers"]!)[0],
            "accepted attributes survive actual reuse and late mutation"
        );
        Console.WriteLine(
            "PASS negative native lifetime probe: actual pool reuse and late mutation cannot change owned export"
        );
    }

    public static void UnsupportedValues()
    {
        using var fixture = new Fixture(true);
        fixture.Capture.Mask = record =>
        {
            Check.Normalized(record);
            if (record.Body == "unsupported-output")
                record.Attributes = [new("unknown", new object())];
            return record;
        };
        var logger = fixture.Factory.CreateLogger("App.Unsupported");
        foreach (
            var value in new object[]
            {
                new object(),
                new object[] { "nested" },
                new Dictionary<string, object?> { ["nested"] = 1 },
            }
        )
            logger.Log(
                LogLevel.Information,
                default,
                new Dictionary<string, object?> { ["unknown"] = value },
                null,
                static (_, _) => "unsupported-input"
            );
        using (logger.BeginScope(new Dictionary<string, object?> { ["unknown"] = new object() }))
            logger.LogInformation("unsupported-scope");
        logger.LogInformation("unsupported-output");
        logger.LogInformation("supported-recovery");
        Check.Equal(
            4,
            fixture.Adapter.InputDrops,
            "unsupported probe state/scope values fail closed before callback"
        );
        Check.Equal(
            2,
            fixture.Capture.CallbackCalls,
            "unsupported input never reaches native callback"
        );
        Check.Equal(
            1,
            fixture.Capture.OutputDrops,
            "unsupported callback value fails closed before buffering"
        );
        Check.Equal(
            6,
            fixture.Sink.Records.Count,
            "unsupported private values do not affect independent provider"
        );
        fixture.Flush(1, expectedInputDrops: 4, expectedOutputDrops: 1);
        Check.Equal(
            "supported-recovery",
            fixture.Exporter.Records.Single().Body,
            "private capture continues after rejected probe values"
        );
        Console.WriteLine(
            "PASS experimental finite value set fails closed at both ownership boundaries (not production normalization policy)"
        );
    }

    public static void FormatterOnce()
    {
        using var fixture = new Fixture(true);
        var calls = 0;
        fixture
            .Adapter.CreateLogger("App.Direct")
            .Log(
                LogLevel.Information,
                default,
                "plain state",
                null,
                (state, _) =>
                {
                    calls++;
                    return state;
                }
            );
        Check.Equal(1, calls, "adapter-only forwarding calls application formatter exactly once");
        fixture.Flush(1);
        Check.Equal(
            "plain state",
            fixture.Exporter.Records.Single().Body,
            "unstructured state becomes rendered Body without raw State"
        );
        Console.WriteLine("PASS formatter invoked exactly once for private forwarding");
    }

    public static void ParallelScopes(ActivitySource source)
    {
        using var fixture = new Fixture(true);
        fixture.Capture.Mask = record =>
        {
            Check.Normalized(record);
            var fields = record.Attributes!.ToDictionary(pair => pair.Key, pair => pair.Value);
            var worker = (int)fields["worker"]!;
            Check.Equal(
                worker,
                ((int[])fields["scope-array"]!)[0],
                "parallel call sees its own scope array"
            );
            Check.Equal($"worker-{worker}", record.Body, "parallel scope matches rendered event");
            Check.Equal(
                Activity.Current!.SpanId,
                record.SpanId,
                "parallel native context matches current child"
            );
            ((int[])fields["scope-array"]!)[0] += 100;
            return record;
        };
        var tasks = Enumerable
            .Range(0, 12)
            .Select(worker =>
                Task.Run(async () =>
                {
                    using var server = source.StartActivity(
                        $"request-{worker}",
                        ActivityKind.Server
                    )!;
                    using var child = source.StartActivity($"child-{worker}")!;
                    fixture.Capture.Associations[(child.TraceId, child.SpanId)] =
                        server.SpanId.ToHexString();
                    var logger = fixture.Factory.CreateLogger("App.Parallel");
                    var array = new[] { worker };
                    using var scope = logger.BeginScope(
                        new Dictionary<string, object?>
                        {
                            ["worker"] = worker,
                            ["scope-array"] = array,
                        }
                    );
                    for (var call = 0; call < 3; call++)
                    {
                        await Task.Yield();
                        logger.LogInformation("worker-{Worker}", worker);
                        Check.Equal(
                            worker,
                            array[0],
                            "parallel callback mutation leaves original scope intact"
                        );
                    }
                    return (
                        worker,
                        child.TraceId,
                        child.SpanId,
                        ServerId: server.SpanId.ToHexString()
                    );
                })
            )
            .ToArray();
        Check.True(
            Task.WhenAll(tasks).Wait(TimeSpan.FromSeconds(10)),
            "bounded parallel logging completed"
        );
        fixture.Flush(36);
        var identities = tasks.Select(task => task.Result).ToDictionary(item => item.worker);
        foreach (var record in fixture.Exporter.Records)
        {
            var worker = (int)record.Attributes["worker"]!;
            var identity = identities[worker];
            Check.Equal(
                worker + 100,
                ((int[])record.Attributes["scope-array"]!)[0],
                "parallel exported scope isolated"
            );
            Check.Equal(identity.TraceId, record.TraceId, "parallel trace copied coherently");
            Check.Equal(identity.SpanId, record.SpanId, "parallel child copied coherently");
            Check.Equal(
                identity.ServerId,
                record.Attributes[MaskingProcessor.ServerAttribute],
                "parallel fixture SERVER association coherent"
            );
        }
        Check.Equal(36, fixture.Sink.Records.Count, "independent sink receives every parallel log");
        Check.True(
            fixture
                .Exporter.Records.GroupBy(record => record.Attributes["worker"])
                .All(group => group.Count() == 3),
            "each independent scope exports exactly three calls"
        );
        Console.WriteLine(
            "PASS 12 parallel async scopes / 36 logs, detached arrays and coherent SERVER/child fixture identities"
        );
    }

    private static Exception ThrowSynthetic()
    {
        try
        {
            throw new InvalidOperationException("synthetic secret");
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static OpenTelemetryLoggerProvider NativeProvider(
        params BaseProcessor<LogRecord>[] processors
    )
    {
        var options = new OpenTelemetryLoggerOptions
        {
            IncludeScopes = false,
            IncludeFormattedMessage = false,
            ParseStateValues = true,
        };
        foreach (var processor in processors)
            options.AddProcessor(processor);
        return new OpenTelemetryLoggerProvider(new FixedOptions(options));
    }
}

internal sealed class Fixture : IDisposable
{
    public MemoryExporter Exporter { get; } = new();
    public OwnedBatch Batch { get; }
    public MaskingProcessor Capture { get; } = new();
    public CaptureAdapter Adapter { get; }
    public UserSink Sink { get; } = new();
    public ILoggerFactory Factory { get; }

    public Fixture(bool adapterFirst)
    {
        Batch = new OwnedBatch(Exporter);
        Adapter = new CaptureAdapter(Capture);
        Factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            if (adapterFirst)
                builder.AddProvider(Adapter);
            builder.AddProvider(Sink);
            if (!adapterFirst)
                builder.AddProvider(Adapter);
        });
    }

    public void Flush(int expected, int expectedInputDrops = 0, int expectedOutputDrops = 0)
    {
        Check.Equal(expectedInputDrops, Adapter.InputDrops, "exact input drop count");
        Check.Equal(expectedOutputDrops, Capture.OutputDrops, "exact output drop count");
        Check.Equal(
            expected,
            Capture.Pending.Count,
            "exact accepted snapshots before deferred release"
        );
        while (Capture.Pending.TryDequeue(out var snapshot))
            Batch.OnEnd(snapshot);
        Check.Equal(expected, Batch.Submitted, "only accepted owned snapshots submitted to batch");
        Check.True(Batch.ForceFlush(5000), "bounded batch flush succeeded");
        Check.True(
            Exporter.WaitForCount(expected, 5000),
            "exporter completed exact expected output within bound"
        );
    }

    public void Dispose()
    {
        Factory.Dispose();
        Adapter.Dispose();
        Sink.Dispose();
        Check.True(Batch.Shutdown(5000), "batch shutdown completed within bound");
        Batch.Dispose();
    }
}

internal sealed class UserSink : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider scopes = new LoggerExternalScopeProvider();
    public ConcurrentQueue<UserRow> Records { get; } = new();

    public ILogger CreateLogger(string categoryName) => new SinkLogger(this);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => scopes = scopeProvider;

    public void Dispose() { }

    private sealed class SinkLogger(UserSink owner) : ILogger
    {
        public bool IsEnabled(LogLevel level) => level != LogLevel.None;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public void Log<TState>(
            LogLevel level,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            var capturedScopes = new List<ScopeRow>();
            owner.scopes.ForEachScope(
                (scope, target) => target.Add(new(scope, ObserveFields(scope))),
                capturedScopes
            );
            owner.Records.Enqueue(
                new(
                    state,
                    formatter(state, exception),
                    exception,
                    (exception?.Data["secret"] as int[])?[0],
                    ObserveFields(state),
                    capturedScopes
                )
            );
        }
    }

    private static Dictionary<string, object?>? ObserveFields(object? state) =>
        (state as IEnumerable<KeyValuePair<string, object?>>)?.ToDictionary(
            pair => pair.Key,
            pair => ObserveValue(pair.Value)
        );

    private static object? ObserveValue(object? value)
    {
        try
        {
            return ProbeValues.Copy(value);
        }
        // The independent sink may keep originals; the private capture path must not.
        catch (NotSupportedException)
        {
            return value;
        }
    }
}

internal sealed record ScopeRow(object? Original, Dictionary<string, object?>? Fields);

internal sealed record UserRow(
    object? State,
    string Message,
    Exception? Exception,
    int? ExceptionDataAtLog,
    Dictionary<string, object?>? Fields,
    List<ScopeRow> Scopes
);

internal sealed class Observe(Action<LogRecord> action) : BaseProcessor<LogRecord>
{
    public override void OnEnd(LogRecord record) => action(record);
}

internal static class Check
{
    private static int count;
    public static int Count => Volatile.Read(ref count);
    public static ConcurrentQueue<string> Failures { get; } = new();

    public static void True(bool condition, string message)
    {
        Interlocked.Increment(ref count);
        if (condition)
            return;
        var failure = $"ASSERTION FAILED: {message}";
        Failures.Enqueue(failure);
        throw new InvalidOperationException(failure);
    }

    public static void Equal<T>(T expected, T actual, string message) =>
        True(
            EqualityComparer<T>.Default.Equals(expected, actual),
            $"{message}: expected <{expected}>, actual <{actual}>"
        );

    public static void Normalized(LogRecord record)
    {
        Equal(
            typeof(LogRecord),
            record.GetType(),
            "callback receives actual OpenTelemetry.Logs.LogRecord"
        );
#pragma warning disable CS0618 // Inspect obsolete State only synchronously to verify input normalization.
        Equal<object?>(null, record.State, "ParseStateValues=true leaves native State null");
#pragma warning restore CS0618
        Equal<Exception?>(
            null,
            record.Exception,
            "native callback receives no original exception object"
        );
        Equal<string?>(
            null,
            record.FormattedMessage,
            "synchronous processor clears native FormattedMessage"
        );
        True(
            record.Attributes?.All(pair => pair.Key != "{OriginalFormat}") == true,
            "native callback attributes contain no OriginalFormat"
        );
        var scopes = new List<object?>();
        record.ForEachScope((scope, target) => target.Add(scope.Scope), scopes);
        Equal(0, scopes.Count, "private native scope chain is empty");
    }
}
