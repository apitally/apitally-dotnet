using System.Diagnostics;
using Apitally.Export;

namespace Apitally.Logging;

// A log entry queued for export: either an accepted application record with its request
// linkage, or an SDK internal event.
internal sealed class LogSnapshot
{
    public required DateTime Timestamp { get; init; }

    // Set for application logs.
    public LogRecordSnapshot? Record { get; init; }

    // Set for internal events: a string or a Dictionary<string, object?> body.
    public string? EventName { get; init; }
    public object? EventBody { get; init; }

    public ActivityTraceId TraceId { get; init; }
    public ActivitySpanId SpanId { get; init; }
    public ActivityTraceFlags TraceFlags { get; init; }
    public ActivitySpanId ServerSpanId { get; init; }

    public string ScopeName => Record?.CategoryName ?? OtlpEncoder.ScopeName;

    public static LogSnapshot ForApplicationLog(
        LogRecordSnapshot record,
        ActivityTraceId traceId,
        ActivitySpanId spanId,
        ActivityTraceFlags traceFlags,
        ActivitySpanId serverSpanId
    ) =>
        new()
        {
            Timestamp = record.Timestamp,
            Record = record,
            TraceId = traceId,
            SpanId = spanId,
            TraceFlags = traceFlags,
            ServerSpanId = serverSpanId,
        };

    public static LogSnapshot ForInternalEvent(string eventName, object body, DateTime timestamp) =>
        new()
        {
            Timestamp = timestamp,
            EventName = eventName,
            EventBody = body,
        };
}
