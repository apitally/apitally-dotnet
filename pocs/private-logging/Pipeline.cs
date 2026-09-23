using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;

namespace PrivateLogging;

// POC-owned data only, not a proposed public callback or exporter API.
internal sealed class OwnedLog
{
    public DateTime Timestamp { get; init; }
    public DateTime ObservedTimestamp { get; init; }
    public string Scope { get; init; } = "";
    public EventId EventId { get; init; }
    public string? EventName { get; init; }
    public object? Body { get; set; }
    public LogLevel Level { get; init; }
    public ActivityTraceId TraceId { get; init; }
    public ActivitySpanId SpanId { get; init; }
    public ActivityTraceFlags TraceFlags { get; init; }
    public Dictionary<string, object?> Attributes { get; init; } = [];
    public List<object?> Scopes { get; init; } = [];

    public static OwnedLog Copy(LogRecord record, bool internalEvent = false)
    {
        var attributes =
            record.Attributes?.ToDictionary(p => p.Key, p => Values.Copy(p.Value)) ?? [];
        var scopes = new List<object?>();
        if (!internalEvent)
            record.ForEachScope((scope, target) => target.Add(Values.Copy(scope.Scope)), scopes);
        if (record.Exception is { } exception)
        {
            attributes["exception.type"] = exception.GetType().FullName;
            attributes["exception.message"] = exception.Message;
            attributes["exception.stacktrace"] = exception.ToString();
        }
        var body = (object?)(record.FormattedMessage ?? record.Body);
        if (internalEvent)
        {
            body = attributes["poc.internal.body"];
            attributes.Remove("poc.internal.body");
        }
        return new OwnedLog
        {
            Timestamp = record.Timestamp,
            ObservedTimestamp = record.ObservedTimestamp,
            Scope = record.CategoryName ?? "",
            EventId = record.EventId,
            EventName = record.EventId.Name,
            Body = body,
            Level = record.LogLevel,
            TraceId = record.TraceId,
            SpanId = record.SpanId,
            TraceFlags = record.TraceFlags,
            Attributes = attributes,
            Scopes = scopes,
        };
    }

    public OwnedLog Copy() =>
        new()
        {
            Timestamp = Timestamp,
            ObservedTimestamp = ObservedTimestamp,
            Scope = Scope,
            EventId = EventId,
            EventName = EventName,
            Body = Values.Copy(Body),
            Level = Level,
            TraceId = TraceId,
            SpanId = SpanId,
            TraceFlags = TraceFlags,
            Attributes = Attributes.ToDictionary(p => p.Key, p => Values.Copy(p.Value)),
            Scopes = Scopes.Select(Values.Copy).ToList(),
        };

    public void Truncate()
    {
        if (Body is string body)
            Body = Values.Truncate(body);
        foreach (var key in Attributes.Keys.ToArray())
            if (Attributes[key] is string value)
                Attributes[key] = Values.Truncate(value);
        foreach (var scope in Scopes.OfType<Dictionary<string, object?>>())
        foreach (var key in scope.Keys.ToArray())
            if (scope[key] is string value)
                scope[key] = Values.Truncate(value);
        for (var index = 0; index < Scopes.Count; index++)
            if (Scopes[index] is string value)
                Scopes[index] = Values.Truncate(value);
    }
}

internal sealed class CaptureBridge : ILoggerProvider, ISupportExternalScope
{
    private readonly OpenTelemetryLoggerProvider provider;
    public bool CaptureEnabled { get; set; } = true;

    public CaptureBridge(BaseProcessor<LogRecord> processor)
    {
        var options = new OpenTelemetryLoggerOptions
        {
            IncludeScopes = true,
            IncludeFormattedMessage = true,
            ParseStateValues = true,
        };
        options.SetResourceBuilder(ResourceBuilder.CreateEmpty().AddService("private-poc"));
        options.AddProcessor(processor);
        provider = new OpenTelemetryLoggerProvider(new FixedOptions(options));
    }

    public ILogger CreateLogger(string categoryName) => new BridgeLogger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopes) =>
        ((ISupportExternalScope)provider).SetScopeProvider(scopes);

    public void EmitInternal(string eventName, object body)
    {
        var previous = Activity.Current;
        try
        {
            Activity.Current = null;
            // EventId.Name maps to native event_name; object bodies need the owned mapping below.
            provider
                .CreateLogger("apitally")
                .Log(
                    LogLevel.Information,
                    new EventId(0, eventName),
                    new Dictionary<string, object?> { ["poc.internal.body"] = body },
                    null,
                    static (_, _) => "internal body mapped by private processor"
                );
        }
        finally
        {
            Activity.Current = previous;
        }
    }

    public void Dispose() => provider.Dispose();

    private sealed class BridgeLogger(CaptureBridge owner, string category) : ILogger
    {
        private readonly ILogger logger = owner.provider.CreateLogger(category);
        private readonly bool excluded = new[] { "apitally", "OpenTelemetry" }.Any(prefix =>
            category.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || category.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase)
        );

        public bool IsEnabled(LogLevel level) =>
            owner.CaptureEnabled && !excluded && logger.IsEnabled(level);

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
                logger.Log(level, eventId, state, exception, formatter);
            }
            catch
            {
                // The POC contains capture failures without altering the caller's logging path.
            }
        }
    }
}

internal sealed class CaptureProcessor(OwnedBatch batch) : BaseProcessor<LogRecord>
{
    public const string ServerAttribute = "apitally.request.server_span_id";
    private readonly ConcurrentDictionary<
        (ActivityTraceId, ActivitySpanId),
        RequestBuffer
    > requests = new();
    public Func<OwnedLog, OwnedLog?>? Mask { get; set; }
    public int MaskCalls { get; private set; }
    public int CallbackDrops { get; private set; }
    public bool InternalContextWasEmpty { get; private set; } = true;
    public string? ResourceService { get; private set; }

    public void Associate(Activity activity, RequestBuffer request) =>
        requests[(activity.TraceId, activity.SpanId)] = request;

    public override void OnEnd(LogRecord record)
    {
        ResourceService ??= (string?)
            ParentProvider.GetResource().Attributes.Single(p => p.Key == "service.name").Value;
        if (record.CategoryName == "apitally")
        {
            InternalContextWasEmpty &=
                record.TraceId == default
                && record.SpanId == default
                && record.TraceFlags == default;
            batch.OnEnd(OwnedLog.Copy(record, internalEvent: true));
            return;
        }
        if (
            !requests.TryGetValue((record.TraceId, record.SpanId), out var request)
            || request.Dropped
        )
            return;
        var copy = OwnedLog.Copy(record);
        copy.Attributes[ServerAttribute] = request.ServerSpanId.ToHexString();
        if (Mask is { } mask)
        {
            MaskCalls++;
            try
            {
                if (!ReferenceEquals(mask(copy), copy))
                {
                    CallbackDrops++;
                    return;
                }
            }
            catch
            {
                CallbackDrops++;
                return;
            }
        }
        // Detach again from a callback that may retain the supplied object.
        var buffered = copy.Copy();
        buffered.Attributes[ServerAttribute] = request.ServerSpanId.ToHexString();
        buffered.Truncate();
        request.Add(buffered);
    }
}

internal sealed class RequestBuffer(ActivitySpanId serverSpanId, OwnedBatch batch)
{
    private readonly object sync = new();
    private readonly List<OwnedLog> logs = [];
    private bool transportCompleted;
    private bool serverCompleted;
    private bool dropped;
    private bool released;
    public ActivitySpanId ServerSpanId { get; } = serverSpanId;
    public bool Dropped
    {
        get
        {
            lock (sync)
                return dropped;
        }
    }
    public int Count
    {
        get
        {
            lock (sync)
                return logs.Count;
        }
    }

    public void Add(OwnedLog log)
    {
        lock (sync)
        {
            if (dropped)
                return;
            if (released)
                batch.OnEnd(log);
            else if (logs.Count < 1000)
                logs.Add(log);
        }
    }

    public void TransportCompleted(bool keep)
    {
        lock (sync)
        {
            if (released || dropped)
                return;
            transportCompleted = true;
            if (!keep)
            {
                dropped = true;
                logs.Clear();
            }
            Release();
        }
    }

    public void ServerCompleted()
    {
        lock (sync)
        {
            serverCompleted = true;
            Release();
        }
    }

    private void Release()
    {
        if (!transportCompleted || !serverCompleted || dropped || released)
            return;
        released = true;
        foreach (var log in logs)
            batch.OnEnd(log);
        logs.Clear();
    }
}

internal sealed class OwnedBatch(BaseExporter<OwnedLog> exporter)
    : BatchExportProcessor<OwnedLog>(
        exporter,
        maxQueueSize: 4096,
        scheduledDelayMilliseconds: 1000,
        exporterTimeoutMilliseconds: 5000,
        maxExportBatchSize: 256
    )
{
    private int submittedCount;
    public int SubmittedCount => Volatile.Read(ref submittedCount);

    public override void OnEnd(OwnedLog data)
    {
        Interlocked.Increment(ref submittedCount);
        base.OnEnd(data);
    }
}

internal sealed class MemoryExporter : BaseExporter<OwnedLog>
{
    private readonly object completion = new();
    private int completedCount;
    public ConcurrentQueue<OwnedLog> Records { get; } = new();
    public ConcurrentQueue<int> Threads { get; } = new();

    public override ExportResult Export(in Batch<OwnedLog> batch)
    {
        Threads.Enqueue(Environment.CurrentManagedThreadId);
        var count = 0;
        foreach (var item in batch)
        {
            Records.Enqueue(item);
            count++;
        }
        lock (completion)
        {
            completedCount += count;
            Monitor.PulseAll(completion);
        }
        return ExportResult.Success;
    }

    public bool WaitForCount(int expectedCount, int timeoutMilliseconds)
    {
        var elapsed = Stopwatch.StartNew();
        lock (completion)
        {
            while (completedCount < expectedCount)
            {
                var remaining = timeoutMilliseconds - (int)elapsed.ElapsedMilliseconds;
                if (remaining <= 0 || !Monitor.Wait(completion, remaining))
                    return false;
            }
            return completedCount == expectedCount;
        }
    }
}

internal static class Values
{
    public static object? Copy(object? value) =>
        value switch
        {
            null
            or string
            or bool
            or char
            or byte
            or sbyte
            or short
            or ushort
            or int
            or uint
            or long
            or ulong
            or float
            or double
            or decimal
            or DateTime
            or DateTimeOffset
            or Guid => value,
            IEnumerable<KeyValuePair<string, object?>> pairs => pairs.ToDictionary(
                p => p.Key,
                p => Copy(p.Value)
            ),
            Array array when array.Rank == 1 => CopyArray(array),
            IEnumerable sequence => sequence.Cast<object?>().Select(Copy).ToArray(),
            _ => value.ToString(),
        };

    public static string Truncate(string text)
    {
        var offset = 0;
        var count = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (count++ == 2048)
                return text[..offset];
            offset += rune.Utf16SequenceLength;
        }
        return text;
    }

    private static Array CopyArray(Array source)
    {
        // Object arrays may contain nested mutable dictionaries/arrays.
        var copy = (Array)source.Clone();
        for (var index = 0; index < copy.Length; index++)
            copy.SetValue(Copy(source.GetValue(index)), index);
        return copy;
    }
}

internal sealed class FixedOptions(OpenTelemetryLoggerOptions options)
    : IOptionsMonitor<OpenTelemetryLoggerOptions>
{
    public OpenTelemetryLoggerOptions CurrentValue => options;

    public OpenTelemetryLoggerOptions Get(string? name) => options;

    public IDisposable? OnChange(Action<OpenTelemetryLoggerOptions, string?> listener) => null;
}
