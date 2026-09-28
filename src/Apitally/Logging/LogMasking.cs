using Apitally.Export;
using Microsoft.Extensions.Logging;

namespace Apitally.Logging;

// Builds Apitally's own copy of an application log record and applies the mask callback.
// Values are normalized first, so the callback sees what will be exported before truncation.
internal static class LogMasking
{
    private const string OriginalFormatKey = "{OriginalFormat}";

    public static LogRecordSnapshot? CreateRecord<TState>(
        string categoryName,
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter,
        IExternalScopeProvider? scopeProvider
    )
    {
        var body = formatter(state, exception);
        // Explicit fields win over inner scopes, which win over outer scopes. Scopes are
        // enumerated outermost first, so later values overwrite earlier ones.
        var values = new List<KeyValuePair<string, object?>>();
        scopeProvider?.ForEachScope(
            static (scope, values) =>
            {
                if (scope is IEnumerable<KeyValuePair<string, object?>> fields)
                    AddStructuredValues(fields, values);
            },
            values
        );
        if (state is IEnumerable<KeyValuePair<string, object?>> stateValues)
            AddStructuredValues(stateValues, values);
        if (exception is not null)
        {
            values.Add(new("exception.type", exception.GetType().FullName));
            values.Add(new("exception.message", exception.Message));
            values.Add(new("exception.stacktrace", exception.ToString()));
        }
        return new LogRecordSnapshot(
            DateTime.UtcNow,
            categoryName,
            logLevel,
            eventId,
            body,
            AttributeValues.Normalize(values)
        );
    }

    // Exceptions, a replacement record or an empty body drop the record rather than export
    // unmasked content.
    public static bool TryMask(
        LogRecordSnapshot record,
        Func<LogRecordSnapshot, LogRecordSnapshot?>? mask
    )
    {
        if (mask is not null)
        {
            try
            {
                if (!ReferenceEquals(mask(record), record))
                    return false;
            }
            catch
            {
                return false;
            }
        }
        return !string.IsNullOrEmpty(record.Body);
    }

    // Unstructured scope labels and message templates are omitted.
    private static void AddStructuredValues(
        IEnumerable<KeyValuePair<string, object?>> fields,
        List<KeyValuePair<string, object?>> values
    )
    {
        foreach (var field in fields)
        {
            if (!string.IsNullOrEmpty(field.Key) && field.Key != OriginalFormatKey)
                values.Add(field);
        }
    }
}
