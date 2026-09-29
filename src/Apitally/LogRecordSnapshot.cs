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

    /// <summary>The logger's category name, usually the logging class's full name.</summary>
    public string CategoryName { get; }

    /// <summary>The record's log level.</summary>
    public LogLevel LogLevel { get; }

    /// <summary>The record's event ID.</summary>
    public EventId EventId { get; }

    /// <summary>The rendered log message.</summary>
    public string? Body { get; set; }
}
