using System.Diagnostics;
using Apitally.Export;

namespace Apitally.Logging;

// A log entry queued for export: either an accepted application record with its request
// linkage, or an SDK internal event.
internal sealed class LogSnapshot
{
    private LogSnapshot(
        DateTime timestamp,
        LogRecordSnapshot? record,
        string? eventName,
        object? eventBody,
        ActivityTraceId traceId,
        ActivitySpanId spanId,
        ActivityTraceFlags traceFlags,
        ActivitySpanId serverSpanId
    )
    {
        Timestamp = timestamp;
        Record = record;
        EventName = eventName;
        EventBody = eventBody;
        TraceId = traceId;
        SpanId = spanId;
        TraceFlags = traceFlags;
        ServerSpanId = serverSpanId;
    }

    public DateTime Timestamp { get; }

    // Set for application logs.
    public LogRecordSnapshot? Record { get; }

    // Set for internal events: a string or a Dictionary<string, object?> body.
    public string? EventName { get; }
    public object? EventBody { get; }

    public ActivityTraceId TraceId { get; }
    public ActivitySpanId SpanId { get; }
    public ActivityTraceFlags TraceFlags { get; }
    public ActivitySpanId ServerSpanId { get; }

    public string ScopeName => Record?.CategoryName ?? OtlpEncoder.ScopeName;

    public static LogSnapshot ForApplicationLog(
        LogRecordSnapshot record,
        ActivityTraceId traceId,
        ActivitySpanId spanId,
        ActivityTraceFlags traceFlags,
        ActivitySpanId serverSpanId
    ) => new(record.Timestamp, record, null, null, traceId, spanId, traceFlags, serverSpanId);

    public static LogSnapshot ForInternalEvent(string eventName, object body, DateTime timestamp) =>
        new(timestamp, null, eventName, body, default, default, default, default);
}
