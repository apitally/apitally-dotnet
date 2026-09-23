using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using PrivateLogging;

Console.WriteLine(
    $"Runtime: {RuntimeInformation.FrameworkDescription}; SDK: 10.0.301; OpenTelemetry: 1.19.0"
);
using var listener = new ActivityListener
{
    ShouldListenTo = source => source.Name == "PrivateLogging.Poc",
    Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
        ActivitySamplingResult.AllDataAndRecorded,
};
ActivitySource.AddActivityListener(listener);
using var source = new ActivitySource("PrivateLogging.Poc");
foreach (var bridgeFirst in new[] { true, false })
{
    Tests.Isolation(source, bridgeFirst);
    Tests.Filters(source, bridgeFirst);
}
Tests.Masking(source);
Tests.Buffering(source);
Tests.InternalEvents(source);
Tests.OpaqueValues(source);
Tests.PoolingAndDrop();
Console.WriteLine(
    $"PASS: {Check.Count} assertions; all providers and batch workers disposed; no network exporters."
);

internal static class Tests
{
    public static void Isolation(ActivitySource source, bool bridgeFirst)
    {
        using var fixture = new Fixture(bridgeFirst);
        using var server = source.StartActivity("request", ActivityKind.Server)!;
        var request = fixture.Associate(server);
        using var child = source.StartActivity("child", ActivityKind.Internal)!;
        fixture.Capture.Associate(child, request);
        OwnedLog? callbackRecord = null;
        var thread = Environment.CurrentManagedThreadId;
        fixture.Capture.Mask = record =>
        {
            if (record.EventId.Id != 101)
                return record;
            Check.Equal(thread, Environment.CurrentManagedThreadId, "mask runs synchronously");
            Check.Equal("original raw", record.Body, "mask sees rendered body");
            Check.Equal(
                "original {value}",
                record.Attributes["{OriginalFormat}"],
                "original template preserved"
            );
            var payload = (Dictionary<string, object?>)record.Attributes["payload"]!;
            payload["secret"] = "masked";
            ((int[])record.Attributes["numbers"]!)[0] = 99;
            ((Dictionary<string, object?>)record.Scopes[1]!)["scope-secret"] = "masked-scope";
            record.Body = new string('m', 2050);
            record.Attributes["long"] = new string('a', 3000);
            callbackRecord = record;
            return record;
        };
        var logger = fixture.Factory.CreateLogger("App.Orders");
        var payload = new Dictionary<string, object?>
        {
            ["secret"] = "raw",
            ["nested"] = new object?[]
            {
                new Dictionary<string, object?> { ["key"] = "nested-original" },
            },
        };
        var numbers = new[] { 1, 2 };
        var scopes = new Dictionary<string, object?>
        {
            ["scope-secret"] = "scope-original",
            ["array"] = new[] { "scope-array" },
            ["long-scope"] = new string('s', 3000),
        };
        var state = new Dictionary<string, object?>
        {
            ["{OriginalFormat}"] = "original {value}",
            ["value"] = "raw",
            ["payload"] = payload,
            ["numbers"] = numbers,
            ["long"] = "short",
            ["code.file.path"] = "Orders.cs",
            ["string-array"] = new[] { new string('z', 3000) },
            ["code.line.number"] = 32,
            ["code.function.name"] = "Read",
        };
        using (logger.BeginScope("outer"))
        using (logger.BeginScope(scopes))
            logger.Log(
                LogLevel.Information,
                new EventId(101, "order"),
                state,
                null,
                static (values, _) => $"original {values["value"]}"
            );
        Check.Equal("raw", payload["secret"], "callback leaves application dictionary unchanged");
        Check.Equal(1, numbers[0], "callback leaves application array unchanged");
        Check.Equal(
            "scope-original",
            scopes["scope-secret"],
            "callback leaves application scope unchanged"
        );
        Check.True(
            ReferenceEquals(state, fixture.Sink.Records[0].OriginalState),
            "user sink receives original state object"
        );
        Check.Equal("original raw", fixture.Sink.Records[0].Message, "user output is unchanged");
        Check.Equal("original raw", fixture.User.Records[0].Body, "user OTel body is unchanged");
        Check.Equal(
            "raw",
            ((Dictionary<string, object?>)fixture.User.Records[0].Attributes["payload"]!)["secret"],
            "user OTel nested state is unchanged"
        );
        Check.Equal(
            1,
            ((int[])fixture.User.Records[0].Attributes["numbers"]!)[0],
            "user OTel array is unchanged"
        );
        Check.Equal(
            "scope-original",
            ((Dictionary<string, object?>)fixture.User.Records[0].Scopes[1]!)["scope-secret"],
            "user OTel scope is unchanged"
        );
        Check.Equal(1, request.Count, "snapshot buffered synchronously");

        payload["secret"] = "changed-after-log";
        ((Dictionary<string, object?>)((object?[])payload["nested"]!)[0]!)["key"] =
            "changed-after-log";
        numbers[1] = 200;
        ((string[])scopes["array"]!)[0] = "changed-after-log";
        callbackRecord!.Body = "changed-by-retained-callback";
        ((Dictionary<string, object?>)callbackRecord.Attributes["payload"]!)["secret"] =
            "changed-by-retained-callback";
        for (var index = 0; index < 50; index++)
            logger.LogInformation("pool churn {Index}", index);
        fixture.Flush(0);
        Check.Equal(0, fixture.Exported.Length, "batch flush cannot bypass unresolved request");
        request.TransportCompleted(keep: true);
        fixture.Flush(0);
        Check.Equal(0, fixture.Exported.Length, "transport completion alone does not release");
        request.ServerCompleted();
        fixture.Flush(51);
        Check.Equal(51, fixture.Exported.Length, "all request records exported exactly once");
        var captured = fixture.Exported[0];
        Check.Equal(new string('m', 2048), captured.Body, "body truncation occurs after masking");
        Check.Equal(
            new string('a', 2048),
            captured.Attributes["long"],
            "attribute truncation is exact"
        );
        Check.Equal(
            "masked",
            ((Dictionary<string, object?>)captured.Attributes["payload"]!)["secret"],
            "delayed dictionary is private"
        );
        var nested = (object?[])
            ((Dictionary<string, object?>)captured.Attributes["payload"]!)["nested"]!;
        Check.Equal(
            "nested-original",
            ((Dictionary<string, object?>)nested[0]!)["key"],
            "nested object arrays copied recursively"
        );
        Check.Equal(
            99,
            ((int[])captured.Attributes["numbers"]!)[0],
            "mask can mutate private array"
        );
        Check.Equal(
            2,
            ((int[])captured.Attributes["numbers"]!)[1],
            "post-log array mutation does not leak"
        );
        Check.Equal(
            "scope-array",
            ((string[])((Dictionary<string, object?>)captured.Scopes[1]!)["array"]!)[0],
            "disposed scope snapshot remains stable"
        );
        Check.Equal(
            "masked-scope",
            ((Dictionary<string, object?>)captured.Scopes[1]!)["scope-secret"],
            "private scope mutation retained"
        );
        Check.Equal(
            new string('s', 2048),
            ((Dictionary<string, object?>)captured.Scopes[1]!)["long-scope"],
            "scope string attributes truncated"
        );
        Check.Equal(
            3000,
            ((string[])captured.Attributes["string-array"]!)[0].Length,
            "non-string attribute value is not truncated"
        );
        Check.Equal("outer", captured.Scopes[0], "scope order preserved");
        Check.Equal(child.TraceId, captured.TraceId, "emitting trace preserved");
        Check.Equal(child.SpanId, captured.SpanId, "emitting child span preserved");
        Check.Equal(
            server.SpanId.ToHexString(),
            captured.Attributes[CaptureProcessor.ServerAttribute],
            "separate SERVER association"
        );
        Check.True(captured.SpanId != server.SpanId, "child is not rewritten to SERVER");
        Check.Equal(ActivityTraceFlags.Recorded, captured.TraceFlags, "trace flags preserved");
        Check.Equal("Orders.cs", captured.Attributes["code.file.path"], "code location retained");
        Check.Equal(32, captured.Attributes["code.line.number"], "code line retained");
        Check.Equal("Read", captured.Attributes["code.function.name"], "code function retained");
        Check.Equal("App.Orders", captured.Scope, "category maps to instrumentation scope");
        Check.Equal("order", captured.EventName, "EventId.Name supplies native event name");
        Check.Equal(101, captured.EventId.Id, "numeric EventId copied");
        Check.True(
            captured.Timestamp > DateTime.MinValue
                && captured.ObservedTimestamp >= captured.Timestamp,
            "timestamps copied"
        );
        Check.Equal("private-poc", fixture.Capture.ResourceService, "private resource isolated");
        Check.Equal("user-poc", fixture.User.ResourceService, "user resource preserved");
        logger.LogInformation("late");
        request.ServerCompleted();
        request.TransportCompleted(keep: true);
        fixture.Flush(52);
        Check.Equal(
            52,
            fixture.Exported.Length,
            "late log released and duplicate completion ignored"
        );
        Check.Equal(52, fixture.Sink.Records.Count, "user sink exact count");
        Check.Equal(52, fixture.User.Records.Count, "user OTel exact count");
        Check.True(
            fixture.Exporter.Threads.All(id => id != thread),
            "owned snapshots exported on batch worker"
        );
        Console.WriteLine(
            $"PASS isolation, scopes, child correlation, delayed copies; bridgeFirst={bridgeFirst}"
        );
    }

    public static void Filters(ActivitySource source, bool bridgeFirst)
    {
        using var fixture = new Fixture(bridgeFirst);
        Check.True(
            fixture.Bridge.CreateLogger("App.Ordinary").IsEnabled(LogLevel.Trace),
            "bridge itself accepts most permissive level"
        );
        var ordinary = fixture.Factory.CreateLogger("App.Ordinary");
        ordinary.LogInformation("outside request");
        using var server = source.StartActivity("filters", ActivityKind.Server)!;
        var request = fixture.Associate(server);
        ordinary.LogTrace("trace hidden");
        ordinary.LogDebug("debug hidden");
        ordinary.LogInformation("info");
        fixture.Factory.CreateLogger("App.Noisy.Component").LogInformation("category hidden");
        fixture.Factory.CreateLogger("App.Noisy.Component").LogWarning("category warning");
        fixture.Factory.CreateLogger("App.ProviderSpecific").LogInformation("not in user OTel");
        fixture.Factory.CreateLogger("Apitally.Runtime").LogInformation("SDK diagnostic");
        fixture.Factory.CreateLogger("OpenTelemetry.Diagnostics").LogError("OTel diagnostic");
        request.ServerCompleted();
        request.TransportCompleted(keep: true);
        fixture.Flush(3);
        Check.Equal(
            "info|category warning|not in user OTel",
            string.Join('|', fixture.Exported.Select(r => r.Body)),
            "generic and category filters apply to additive provider"
        );
        Check.Equal(
            6,
            fixture.Sink.Records.Count,
            "user sink keeps own filters and SDK diagnostics"
        );
        Check.Equal(5, fixture.User.Records.Count, "provider-specific user OTel filter unchanged");
        fixture.Bridge.CaptureEnabled = false;
        fixture.Bridge.Dispose();
        ordinary.LogInformation("user provider survives private shutdown");
        Check.Equal(7, fixture.Sink.Records.Count, "private shutdown preserves user sink");
        Check.Equal(6, fixture.User.Records.Count, "private shutdown preserves user OTel");
        Check.True(!fixture.User.Disposed, "private shutdown does not dispose user processor");
        Console.WriteLine(
            $"PASS generic/category/provider-specific filters and independent disposal; bridgeFirst={bridgeFirst}"
        );
    }

    public static void Masking(ActivitySource source)
    {
        using var fixture = new Fixture(true);
        using var server = source.StartActivity("masking", ActivityKind.Server)!;
        var request = fixture.Associate(server);
        fixture.Capture.Mask = record =>
            record.Body switch
            {
                "null" => null,
                "throw" => throw new InvalidOperationException("mask failure"),
                "replacement" => new OwnedLog { Body = "not allowed" },
                _ => record,
            };
        var logger = fixture.Factory.CreateLogger("App.Mask");
        foreach (var text in new[] { "keep", "null", "throw", "replacement", "keep-again" })
            logger.LogInformation("{Text}", text);
        Check.Equal(5, fixture.Capture.MaskCalls, "all callbacks synchronous before buffering");
        Check.Equal(
            3,
            fixture.Capture.CallbackDrops,
            "null, throwing and replacement callbacks drop"
        );
        Check.Equal(2, request.Count, "only keep results reach request buffer");
        request.ServerCompleted();
        request.TransportCompleted(keep: true);
        fixture.Flush(2);
        Check.Equal(
            "keep|keep-again",
            string.Join('|', fixture.Exported.Select(r => r.Body)),
            "dropped records never reach downstream batch"
        );
        Check.Equal(5, fixture.Sink.Records.Count, "mask failures never drop user sink output");
        Check.Equal(5, fixture.User.Records.Count, "mask failures never drop user OTel output");
        var unicode = new string('x', 2047) + "\U0001F600" + "tail";
        logger.LogInformation("{Text}", unicode);
        logger.LogInformation("{Text}", new string('b', 2048));
        logger.LogError(new InvalidOperationException("original exception"), "exception body");
        fixture.Flush(5);
        Check.Equal(5, fixture.Exported.Length, "late truncation and exception exact count");
        Check.Equal(
            new string('x', 2047) + "\U0001F600",
            fixture.Exported[2].Body,
            "2048 Unicode scalars without broken surrogate"
        );
        Check.Equal(new string('b', 2048), fixture.Exported[3].Body, "2048 boundary unchanged");
        Check.Equal(
            "original exception",
            fixture.Exported[4].Attributes["exception.message"],
            "exception copied as strings, not retained object"
        );
        Console.WriteLine(
            "PASS exact callback drop control, post-mask truncation, Unicode boundary, exception copy"
        );
    }

    public static void Buffering(ActivitySource source)
    {
        using var fixture = new Fixture(true);
        var logger = fixture.Factory.CreateLogger("App.Buffer");
        using (var server = source.StartActivity("bounded", ActivityKind.Server)!)
        {
            var request = fixture.Associate(server);
            for (var index = 0; index < 1002; index++)
                logger.LogInformation("{Index}", index);
            Check.Equal(1000, request.Count, "earliest 1000 retained");
            request.ServerCompleted();
            fixture.Flush(0);
            Check.Equal(0, fixture.Exported.Length, "SERVER completion alone does not release");
            request.TransportCompleted(keep: true);
            fixture.Flush(1000);
            Check.Equal(1000, fixture.Exported.Length, "bounded exact export count");
            Check.True(
                fixture
                    .Exported.Select(r => (int)r.Attributes["Index"]!)
                    .SequenceEqual(Enumerable.Range(0, 1000)),
                "earliest records and order retained"
            );
            Check.Equal(0, request.Count, "release clears buffer");
        }
        using (var server = source.StartActivity("dropped", ActivityKind.Server)!)
        {
            var request = fixture.Associate(server);
            logger.LogInformation("buffered drop");
            request.TransportCompleted(keep: false);
            Check.Equal(0, request.Count, "response drop clears buffer");
            logger.LogInformation("late drop");
            request.ServerCompleted();
            fixture.Flush(1000);
            Check.Equal(
                1000,
                fixture.Exported.Length,
                "response drop and late drop export nothing"
            );
        }
        Console.WriteLine(
            "PASS both completion orders, 1000 earliest cap, response drop and late drop"
        );
    }

    public static void InternalEvents(ActivitySource source)
    {
        using var fixture = new Fixture(true);
        using var server = source.StartActivity("internal", ActivityKind.Server)!;
        var request = fixture.Associate(server);
        using var child = source.StartActivity("internal-child")!;
        fixture.Capture.Associate(child, request);
        fixture.Capture.Mask = _ => throw new InvalidOperationException("must not run");
        fixture.Bridge.CaptureEnabled = false;
        var logger = fixture.Factory.CreateLogger("App.Disabled");
        using var scope = logger.BeginScope(
            new Dictionary<string, object?> { ["secret"] = "request-scope" }
        );
        logger.LogInformation("application capture disabled");
        var startup = JsonSerializer.Serialize(
            new
            {
                framework = "aspnetcore",
                versions = new { dotnet = Environment.Version.ToString() },
                config = new { capture_logs = false },
                paths = new[] { new { method = "GET", path = "/orders" } },
                openapi = new string('s', 3000),
            }
        );
        var error = new Dictionary<string, object?>
        {
            ["method"] = "GET",
            ["path"] = "/orders",
            ["type"] = "System.Exception",
            ["message"] = "failure",
            ["stacktrace"] = new string('t', 65536),
            ["count"] = uint.MaxValue,
        };
        var validation = new Dictionary<string, object?>
        {
            ["method"] = "POST",
            ["path"] = "/orders",
            ["source"] = "body",
            ["field"] = "email",
            ["message"] = "invalid",
            ["type"] = "email",
            ["count"] = 4u,
        };
        fixture.Bridge.EmitInternal("apitally.app.startup", startup);
        fixture.Bridge.EmitInternal("apitally.request.server_error", error);
        fixture.Bridge.EmitInternal("apitally.request.validation_error", validation);
        error["message"] = "changed after emit";
        validation["count"] = 20u;
        Check.True(
            ReferenceEquals(child, Activity.Current),
            "internal emission restores Activity.Current"
        );
        fixture.Flush(3);
        Check.Equal(0, fixture.Capture.MaskCalls, "internal events bypass application masking");
        Check.Equal(
            3,
            fixture.Exported.Length,
            "internal events survive capture_logs=false without request release"
        );
        Check.Equal(1, fixture.Sink.Records.Count, "internal events never enter user's sink");
        Check.Equal(
            1,
            fixture.User.Records.Count,
            "internal events never enter user's OTel provider"
        );
        Check.True(
            fixture.Capture.InternalContextWasEmpty,
            "official LogRecord already has empty context"
        );
        foreach (var record in fixture.Exported)
        {
            Check.Equal("apitally", record.Scope, "internal scope");
            Check.True(
                record.TraceId == default
                    && record.SpanId == default
                    && record.TraceFlags == default,
                "internal snapshot has no span context"
            );
            Check.True(
                !record.Attributes.ContainsKey(CaptureProcessor.ServerAttribute),
                "internal event has no SERVER association"
            );
            Check.Equal(0, record.Scopes.Count, "internal event excludes ambient request scopes");
            Check.Equal(
                record.EventName,
                record.EventId.Name,
                "native event name conveyed by public EventId.Name"
            );
            Check.True(
                !record.Attributes.ContainsKey("poc.internal.body"),
                "body carrier removed from exported attributes"
            );
            Check.True(record.Timestamp > DateTime.MinValue, "internal emission timestamp set");
        }
        Check.Equal("apitally.app.startup", fixture.Exported[0].EventName, "startup event name");
        Check.Equal(
            startup,
            fixture.Exported[0].Body,
            "startup remains full JSON string, no 2048 truncation"
        );
        Check.Equal(
            "aspnetcore",
            JsonDocument
                .Parse((string)fixture.Exported[0].Body!)
                .RootElement.GetProperty("framework")
                .GetString(),
            "startup JSON parseable"
        );
        var body = (Dictionary<string, object?>)fixture.Exported[1].Body!;
        Check.Equal("failure", body["message"], "error object is detached, not JSON text");
        Check.Equal(uint.MaxValue, body["count"], "structured error preserves integer count");
        Check.Equal(
            65536,
            ((string)body["stacktrace"]!).Length,
            "error event bypasses application truncation"
        );
        Check.Equal(
            4u,
            ((Dictionary<string, object?>)fixture.Exported[2].Body!)["count"],
            "validation structured body copied"
        );
        Console.WriteLine(
            "PASS internal events, empty context, EventId.Name, string versus owned object body"
        );
    }

    public static void OpaqueValues(ActivitySource source)
    {
        using var fixture = new Fixture(true);
        using var server = source.StartActivity("opaque", ActivityKind.Server)!;
        var request = fixture.Associate(server);
        var value = new MutableValue { Text = "at-log-time" };
        var state = new Dictionary<string, object?> { ["object"] = value };
        fixture
            .Factory.CreateLogger("App.Opaque")
            .Log(LogLevel.Information, default, state, null, static (_, _) => "object");
        Check.True(
            ReferenceEquals(state, fixture.Sink.Records[0].OriginalState),
            "opaque user state still unchanged"
        );
        value.Text = "later";
        request.ServerCompleted();
        request.TransportCompleted(keep: true);
        fixture.Flush(1);
        Check.Equal(1, fixture.Exported.Length, "opaque example exact count");
        Check.Equal(
            "at-log-time",
            fixture.Exported[0].Attributes["object"],
            "opaque object normalized synchronously, not retained"
        );
        Console.WriteLine(
            "PASS limitation probe: opaque attribute becomes string, not a faithful object clone"
        );
    }

    public static void PoolingAndDrop()
    {
        var pool = new PoolProbe();
        var next = new UserProcessor();
        var options = new OpenTelemetryLoggerOptions();
        options.AddProcessor(pool).AddProcessor(next);
        using var provider = new OpenTelemetryLoggerProvider(new FixedOptions(options));
        var logger = provider.CreateLogger("pool-probe");
        logger.LogInformation("first");
        logger.LogInformation("second");
        Check.True(pool.Reused, "same public LogRecord object recycled on same thread");
        Check.Equal(
            "second",
            pool.FirstReferenceBodyDuringSecond,
            "retained LogRecord no longer describes first log"
        );
        Check.Equal("first", pool.FirstOwned!.Body, "synchronous owned copy survives recycling");
        Check.Equal(
            2,
            next.Records.Count,
            "returning from first processor does not drop downstream records"
        );
        Console.WriteLine(
            "PASS negative probes: pooled lifetime unsafe; OnEnd return does not stop later processors"
        );
    }
}

internal sealed class Fixture : IDisposable
{
    public MemoryExporter Exporter { get; } = new();
    public OwnedBatch Batch { get; }
    public CaptureProcessor Capture { get; }
    public CaptureBridge Bridge { get; }
    public UserSink Sink { get; } = new();
    public UserProcessor User { get; } = new();
    public ILoggerFactory Factory { get; }
    public OwnedLog[] Exported => Exporter.Records.ToArray();

    public Fixture(bool bridgeFirst)
    {
        Batch = new OwnedBatch(Exporter);
        Capture = new CaptureProcessor(Batch);
        Bridge = new CaptureBridge(Capture);
        Factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddFilter("App.Noisy", LogLevel.Warning);
            builder.AddFilter<OpenTelemetryLoggerProvider>("App.ProviderSpecific", LogLevel.Error);
            if (bridgeFirst)
                builder.AddProvider(Bridge);
            builder.AddProvider(Sink);
            builder.AddOpenTelemetry(options =>
            {
                options.IncludeScopes = true;
                options.IncludeFormattedMessage = true;
                options.SetResourceBuilder(ResourceBuilder.CreateEmpty().AddService("user-poc"));
                options.AddProcessor(User);
            });
            if (!bridgeFirst)
                builder.AddProvider(Bridge);
        });
    }

    public RequestBuffer Associate(Activity server)
    {
        var request = new RequestBuffer(server.SpanId, Batch);
        Capture.Associate(server, request);
        return request;
    }

    public void Flush(int expectedCount)
    {
        Check.Equal(
            expectedCount,
            Batch.SubmittedCount,
            "exact number of records forwarded to batch"
        );
        Check.True(Batch.ForceFlush(5000), "bounded batch flush succeeded");
        Check.True(
            Exporter.WaitForCount(expectedCount, 5000),
            "exporter completed expected output within bound"
        );
    }

    public void Dispose()
    {
        Factory.Dispose();
        Bridge.Dispose();
        Check.True(User.Disposed, "user factory owns user processor disposal");
        Check.True(Batch.Shutdown(5000), "batch worker shut down within bound");
        Batch.Dispose();
    }
}

internal sealed class UserProcessor : BaseProcessor<LogRecord>
{
    public List<OwnedLog> Records { get; } = [];
    public string? ResourceService { get; private set; }
    public bool Disposed { get; private set; }

    public override void OnEnd(LogRecord record)
    {
        ResourceService ??=
            ParentProvider
                .GetResource()
                .Attributes.FirstOrDefault(p => p.Key == "service.name")
                .Value as string;
        Records.Add(OwnedLog.Copy(record));
    }

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}

internal sealed class UserSink : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider scopes = new LoggerExternalScopeProvider();
    public List<UserRow> Records { get; } = [];

    public ILogger CreateLogger(string categoryName) => new SinkLogger(this);

    public void SetScopeProvider(IExternalScopeProvider provider) => scopes = provider;

    public void Dispose() { }

    private sealed class SinkLogger(UserSink sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel level) => level != LogLevel.None;

        public void Log<TState>(
            LogLevel level,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            var scopes = new List<object?>();
            sink.scopes.ForEachScope((scope, target) => target.Add(Values.Copy(scope)), scopes);
            sink.Records.Add(
                new UserRow(state, formatter(state, exception), Values.Copy(state), scopes)
            );
        }
    }
}

internal sealed record UserRow(
    object? OriginalState,
    string Message,
    object? CopiedState,
    List<object?> Scopes
);

internal sealed class MutableValue
{
    public string Text { get; set; } = "";

    public override string ToString() => Text;
}

internal sealed class PoolProbe : BaseProcessor<LogRecord>
{
    // Deliberately unsafe, confined to the negative lifetime probe.
    private LogRecord? first;
    public OwnedLog? FirstOwned { get; private set; }
    public bool Reused { get; private set; }
    public string? FirstReferenceBodyDuringSecond { get; private set; }

    public override void OnEnd(LogRecord record)
    {
        if (first is null)
        {
            first = record;
            FirstOwned = OwnedLog.Copy(record);
            return;
        }
        Reused = ReferenceEquals(first, record);
        FirstReferenceBodyDuringSecond = first.Body;
        first = null;
    }
}

internal static class Check
{
    public static int Count { get; private set; }

    public static void True(bool condition, string message)
    {
        Count++;
        if (!condition)
            throw new InvalidOperationException($"ASSERTION FAILED: {message}");
    }

    public static void Equal<T>(T expected, T actual, string message) =>
        True(
            EqualityComparer<T>.Default.Equals(expected, actual),
            $"{message}: expected <{expected}>, actual <{actual}>"
        );
}
