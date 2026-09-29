using Microsoft.Extensions.Logging;

namespace Apitally;

/// <summary>
/// Apitally's own copy of an application log record, passed to the <c>MaskLogRecord</c>
/// callback. Changes to <see cref="Body"/> apply only to the record exported to Apitally.
/// </summary>
public sealed class LogRecordSnapshot
{
    internal LogRecordSnapshot(
        DateTime timestamp,
        string categoryName,
        LogLevel logLevel,
        EventId eventId,
        string? body
    )
    {
        Timestamp = timestamp;
        CategoryName = categoryName;
        LogLevel = logLevel;
        EventId = eventId;
        Body = body;
    }

    /// <summary>The time the record was logged, in UTC.</summary>
    public DateTime Timestamp { get; }
    public string CategoryName { get; }
    public LogLevel LogLevel { get; }
    public EventId EventId { get; }

    /// <summary>The rendered log message.</summary>
    public string? Body { get; set; }
}
