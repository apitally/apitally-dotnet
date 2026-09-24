using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;

namespace NativeLogMasking;

internal sealed class CaptureAdapter : ILoggerProvider, ISupportExternalScope
{
    private readonly OpenTelemetryLoggerProvider provider;
    private IExternalScopeProvider scopes = new LoggerExternalScopeProvider();
    private int inputDrops;
    public int InputDrops => Volatile.Read(ref inputDrops);

    public CaptureAdapter(MaskingProcessor processor)
    {
        var options = new OpenTelemetryLoggerOptions
        {
            IncludeScopes = false,
            IncludeFormattedMessage = false,
            ParseStateValues = true,
        };
        options.SetResourceBuilder(ResourceBuilder.CreateEmpty().AddService("native-mask-poc"));
        options.AddProcessor(processor);
        provider = new OpenTelemetryLoggerProvider(new FixedOptions(options));
    }

    public ILogger CreateLogger(string categoryName) => new CaptureLogger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => scopes = scopeProvider;

    public void Dispose() => provider.Dispose();

    private sealed class CaptureLogger(CaptureAdapter owner, string category) : ILogger
    {
        private readonly ILogger logger = owner.provider.CreateLogger(category);

        public bool IsEnabled(LogLevel level) => logger.IsEnabled(level);

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
            if (!IsEnabled(level))
                return;
            try
            {
                var body = formatter(state, exception);
                var attributes = new Dictionary<string, object?>();
                owner.scopes.ForEachScope(
                    static (scope, target) => Merge(scope, target),
                    attributes
                );
                Merge(state, attributes);
                if (exception is not null)
                {
                    attributes["exception.type"] = exception.GetType().FullName;
                    attributes["exception.message"] = exception.Message;
                    attributes["exception.stacktrace"] = exception.ToString();
                }
                // The private formatter closes over immutable text, never application state.
                logger.Log(level, eventId, attributes.ToList(), null, (_, _) => body);
            }
            catch
            {
                Interlocked.Increment(ref owner.inputDrops);
            }
        }

        private static void Merge(object? source, Dictionary<string, object?> target)
        {
            if (source is not IEnumerable<KeyValuePair<string, object?>> fields)
                return;
            // Probe inputs have unique keys within each source. Other shapes are not modeled.
            var copied = fields
                .Where(pair => pair.Key != "{OriginalFormat}")
                .ToDictionary(pair => pair.Key, pair => ProbeValues.Copy(pair.Value));
            foreach (var pair in copied)
                target[pair.Key] = pair.Value;
        }
    }
}

internal sealed class MaskingProcessor : BaseProcessor<LogRecord>
{
    public const string ServerAttribute = "apitally.request.server_span_id";
    private int callbackCalls;
    private int callbackDrops;
    private int outputDrops;
    public int CallbackCalls => Volatile.Read(ref callbackCalls);
    public int CallbackDrops => Volatile.Read(ref callbackDrops);
    public int OutputDrops => Volatile.Read(ref outputDrops);
    public Func<LogRecord, LogRecord?> Mask { get; set; } = record => record;
    public ConcurrentQueue<OwnedSnapshot> Pending { get; } = new();

    // Test fixture only: no request discovery, completion, or retention policy is implied.
    public ConcurrentDictionary<(ActivityTraceId, ActivitySpanId), string> Associations { get; } =
        new();

    public override void OnEnd(LogRecord record)
    {
        // Without OriginalFormat, the provider fills this even when its option is false.
        record.FormattedMessage = null;
        if (Associations.TryGetValue((record.TraceId, record.SpanId), out var serverId))
        {
            var attributes = record.Attributes!.ToList();
            attributes.Add(new(ServerAttribute, serverId));
            record.Attributes = attributes;
        }
        Interlocked.Increment(ref callbackCalls);
        try
        {
            if (!ReferenceEquals(Mask(record), record))
            {
                Interlocked.Increment(ref callbackDrops);
                return;
            }
        }
        catch
        {
            Interlocked.Increment(ref callbackDrops);
            return;
        }
        try
        {
            // No native record or callback-owned mutable value crosses this boundary.
            Pending.Enqueue(OwnedSnapshot.Copy(record));
        }
        catch
        {
            Interlocked.Increment(ref outputDrops);
        }
    }
}

internal sealed record OwnedSnapshot(
    DateTime Timestamp,
    DateTime ObservedTimestamp,
    string? Category,
    EventId EventId,
    LogLevel Level,
    string? Body,
    ActivityTraceId TraceId,
    ActivitySpanId SpanId,
    ActivityTraceFlags TraceFlags,
    Dictionary<string, object?> Attributes
)
{
    public static OwnedSnapshot Copy(LogRecord record) =>
        new(
            record.Timestamp,
            record.ObservedTimestamp,
            record.CategoryName,
            record.EventId,
            record.LogLevel,
            ProbeValues.Truncate(record.Body),
            record.TraceId,
            record.SpanId,
            record.TraceFlags,
            record.Attributes?.ToDictionary(
                pair => pair.Key,
                pair =>
                    pair.Value is string text
                        ? ProbeValues.Truncate(text)
                        : ProbeValues.Copy(pair.Value)
            )
                ?? []
        );
}

internal static class ProbeValues
{
    // Experimental finite set, not a proposed general CLR normalization policy.
    public static object? Copy(object? value) =>
        value switch
        {
            null or string or bool or int or long or double => value,
            int[] numbers => numbers.ToArray(),
            string[] strings => strings.ToArray(),
            List<int> numbers => new List<int>(numbers),
            _ => throw new NotSupportedException("Value is outside the probe's supported set."),
        };

    // All truncation fixtures are ASCII. Unicode semantics remain a separate decision.
    public static string? Truncate(string? value) =>
        value is { Length: > 2048 } ? value[..2048] : value;
}

internal sealed class OwnedBatch(BaseExporter<OwnedSnapshot> exporter)
    : BatchExportProcessor<OwnedSnapshot>(
        exporter,
        maxQueueSize: 256,
        scheduledDelayMilliseconds: 1000,
        exporterTimeoutMilliseconds: 5000,
        maxExportBatchSize: 64
    )
{
    private int submitted;
    public int Submitted => Volatile.Read(ref submitted);

    public override void OnEnd(OwnedSnapshot snapshot)
    {
        Interlocked.Increment(ref submitted);
        base.OnEnd(snapshot);
    }
}

internal sealed class MemoryExporter : BaseExporter<OwnedSnapshot>
{
    private readonly object completion = new();
    private int completed;
    public ConcurrentQueue<OwnedSnapshot> Records { get; } = new();
    public ConcurrentQueue<int> Threads { get; } = new();

    public override ExportResult Export(in Batch<OwnedSnapshot> batch)
    {
        Threads.Enqueue(Environment.CurrentManagedThreadId);
        var count = 0;
        foreach (var snapshot in batch)
        {
            Records.Enqueue(snapshot);
            count++;
        }
        lock (completion)
        {
            completed += count;
            Monitor.PulseAll(completion);
        }
        return ExportResult.Success;
    }

    public bool WaitForCount(int expected, int timeoutMilliseconds)
    {
        var timer = Stopwatch.StartNew();
        lock (completion)
        {
            while (completed < expected)
            {
                var remaining = timeoutMilliseconds - (int)timer.ElapsedMilliseconds;
                if (remaining <= 0 || !Monitor.Wait(completion, remaining))
                    return false;
            }
            return completed == expected;
        }
    }
}

internal sealed class FixedOptions(OpenTelemetryLoggerOptions options)
    : IOptionsMonitor<OpenTelemetryLoggerOptions>
{
    public OpenTelemetryLoggerOptions CurrentValue => options;

    public OpenTelemetryLoggerOptions Get(string? name) => options;

    public IDisposable? OnChange(Action<OpenTelemetryLoggerOptions, string?> listener) => null;
}
