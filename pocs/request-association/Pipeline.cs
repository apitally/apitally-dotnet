using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;

namespace RequestAssociation;

// Mixed owned envelopes simplify this experiment, not a production signal schema.
internal sealed record Owned(
    string Signal,
    string Name,
    ActivityTraceId TraceId,
    ActivitySpanId SpanId,
    ActivitySpanId ParentSpanId,
    Dictionary<string, object?> Attributes
)
{
    public static Owned Span(Activity activity) =>
        new(
            "span",
            activity.DisplayName,
            activity.TraceId,
            activity.SpanId,
            activity.ParentSpanId,
            activity.TagObjects.ToDictionary(pair => pair.Key, pair => Primitive(pair.Value))
        );

    public static object? Primitive(object? value) =>
        value switch
        {
            null or string or bool or int or long or double => value,
            _ => throw new NotSupportedException(
                "Only immutable primitive fixtures are supported."
            ),
        };
}

internal sealed class OwnedBatch(OwnedExporter exporter)
    : BatchExportProcessor<Owned>(exporter, 2048, 100, 5000, 128)
{
    private int submitted;
    public int Submitted => Volatile.Read(ref submitted);

    public override void OnEnd(Owned value)
    {
        base.OnEnd(value);
        Interlocked.Increment(ref submitted);
    }
}

internal sealed class OwnedExporter : BaseExporter<Owned>
{
    private readonly object completion = new();
    private int completed;
    public ConcurrentQueue<Owned> Records { get; } = new();

    public override ExportResult Export(in Batch<Owned> batch)
    {
        var count = 0;
        foreach (var item in batch)
        {
            Records.Enqueue(item);
            count++;
        }
        lock (completion)
        {
            completed += count;
            Monitor.PulseAll(completion);
        }
        return ExportResult.Success;
    }

    public void Wait(int expected)
    {
        var timer = Stopwatch.StartNew();
        lock (completion)
        {
            while (completed < expected)
            {
                var remaining = 5000 - (int)timer.ElapsedMilliseconds;
                if (remaining <= 0 || !Monitor.Wait(completion, remaining))
                    throw new TimeoutException($"Exporter completed {completed}/{expected}");
            }
            Program.Check(completed == expected, "actual exporter completion count");
        }
    }
}

internal sealed class CaptureAdapter : ILoggerProvider
{
    public const string Category = "RequestAssociation.Application";
    private readonly OpenTelemetryLoggerProvider provider;

    public CaptureAdapter(Probe probe)
    {
        var options = new OpenTelemetryLoggerOptions
        {
            IncludeScopes = false,
            IncludeFormattedMessage = false,
            ParseStateValues = true,
        };
        options.SetResourceBuilder(ResourceBuilder.CreateEmpty().AddService("request-association"));
        options.AddProcessor(new NativeLogs(probe));
        provider = new OpenTelemetryLoggerProvider(new FixedOptions(options));
    }

    public ILogger CreateLogger(string categoryName) =>
        new CaptureLogger(categoryName == Category ? provider.CreateLogger(categoryName) : null);

    public void Dispose() => provider.Dispose();

    private sealed class CaptureLogger(ILogger? native) : ILogger
    {
        public bool IsEnabled(LogLevel logLevel) => native?.IsEnabled(logLevel) == true;

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
            var fields = ((IEnumerable<KeyValuePair<string, object?>>)(object)state!)
                .Where(pair => pair.Key != "{OriginalFormat}")
                .Select(pair => new KeyValuePair<string, object?>(
                    pair.Key,
                    Owned.Primitive(pair.Value)
                ))
                .ToList();
            var body = formatter(state, exception);
            native!.Log(level, eventId, fields, null, (_, _) => body);
        }
    }
}

internal sealed class NativeLogs(Probe probe) : BaseProcessor<LogRecord>
{
    public override void OnEnd(LogRecord record) =>
        probe.Guard(() =>
        {
            if (!probe.Enabled || !probe.TryGet(record.TraceId, record.SpanId, out var request))
                return;
            var attributes = record.Attributes!.ToDictionary(pair => pair.Key, pair => pair.Value);
            attributes[Probe.ServerLink] = request.ServerId.ToHexString();
            record.Attributes = attributes.ToList();
            Mask(record);
            request.Add(
                new Owned(
                    "log",
                    record.Body!,
                    record.TraceId,
                    record.SpanId,
                    default,
                    record.Attributes!.ToDictionary(
                        pair => pair.Key,
                        pair => Owned.Primitive(pair.Value)
                    )
                )
            );
        });

    private static void Mask(LogRecord record)
    {
        record.Body = "masked";
        record.FormattedMessage = null;
        record.Attributes = record
            .Attributes!.Select(pair =>
                pair.Key == "secret"
                    ? new KeyValuePair<string, object?>(pair.Key, "[redacted]")
                    : pair
            )
            .ToList();
    }
}

internal sealed class FixedOptions(OpenTelemetryLoggerOptions options)
    : IOptionsMonitor<OpenTelemetryLoggerOptions>
{
    public OpenTelemetryLoggerOptions CurrentValue => options;

    public OpenTelemetryLoggerOptions Get(string? name) => options;

    public IDisposable? OnChange(Action<OpenTelemetryLoggerOptions, string?> listener) => null;
}

internal sealed class AppExporter(Probe probe) : BaseExporter<Activity>
{
    private readonly SemaphoreSlim completed = new(0);
    public ConcurrentQueue<Owned> Records { get; } = new();
    public bool Disposed { get; private set; }

    public override ExportResult Export(in Batch<Activity> batch)
    {
        foreach (var activity in batch)
            probe.Guard(() => Records.Enqueue(Owned.Span(activity)));
        completed.Release();
        return ExportResult.Success;
    }

    public void Wait(int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (Records.Count < count)
            completed.Wait(timeout.Token);
    }

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        completed.Dispose();
    }
}

internal sealed record AppLog(
    string Body,
    ActivityTraceId TraceId,
    ActivitySpanId SpanId,
    Dictionary<string, object?> Attributes
);

internal sealed class AppLogs : ILoggerProvider
{
    public ConcurrentQueue<AppLog> Records { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Sink(this, categoryName);

    public void Dispose() { }

    private sealed class Sink(AppLogs owner, string category) : ILogger
    {
        public bool IsEnabled(LogLevel logLevel) => category == CaptureAdapter.Category;

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
            owner.Records.Enqueue(
                new AppLog(
                    formatter(state, exception),
                    Activity.Current?.TraceId ?? default,
                    Activity.Current?.SpanId ?? default,
                    ((IEnumerable<KeyValuePair<string, object?>>)(object)state!).ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value
                    )
                )
            );
        }
    }
}
