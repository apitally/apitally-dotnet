using Apitally.AspNetCore;

namespace Apitally.Requests;

// Bounded validation and server errors between drains, each counted per consumer. Values are
// normalized and truncated before keying; new errors beyond the limit are silently ignored.
internal sealed class ErrorAggregates
{
    public const int MaxErrors = 100;

    private readonly object sync = new();
    private Dictionary<ValidationErrorKey, Dictionary<string, long>> validationErrors = [];
    private Dictionary<ServerErrorKey, Dictionary<string, long>> serverErrors = [];

    public void AddValidationErrors(
        string? consumer,
        string method,
        string path,
        IEnumerable<ValidationDetail> details
    )
    {
        if (!IsValidMethod(method))
            return;
        consumer = consumer is null ? null : Truncate(consumer, 128);
        lock (sync)
        {
            foreach (var detail in details)
            {
                var key = new ValidationErrorKey(
                    method,
                    Truncate(path, 2_000),
                    Truncate(detail.Source, 32),
                    Truncate(detail.Field, 2_048),
                    Truncate(detail.Message, 2_048),
                    Truncate(detail.Type, 128)
                );
                Increment(validationErrors, key, consumer);
            }
        }
    }

    public void AddServerError(string? consumer, string method, string path, Exception exception)
    {
        if (!IsValidMethod(method))
            return;
        consumer = consumer is null ? null : Truncate(consumer, 128);
        var key = new ServerErrorKey(
            method,
            Truncate(path, 2_000),
            Truncate(exception.GetType().FullName ?? exception.GetType().Name, 256),
            Truncate(exception.Message.Trim(), 2_048),
            Truncate(ExceptionStacktrace.Get(exception), 65_536)
        );
        lock (sync)
            Increment(serverErrors, key, consumer);
    }

    // Swaps the errors atomically; the caller emits events outside the lock.
    public (
        List<Dictionary<string, object?>> Validation,
        List<Dictionary<string, object?>> Server
    ) Drain()
    {
        Dictionary<ValidationErrorKey, Dictionary<string, long>> validation;
        Dictionary<ServerErrorKey, Dictionary<string, long>> server;
        lock (sync)
        {
            (validation, validationErrors) = (validationErrors, []);
            (server, serverErrors) = (serverErrors, []);
        }
        return (
            [.. validation.Select(error => error.Key.ToBody(error.Value))],
            [.. server.Select(error => error.Key.ToBody(error.Value))]
        );
    }

    private static void Increment<TKey>(
        Dictionary<TKey, Dictionary<string, long>> errors,
        TKey key,
        string? consumer
    )
        where TKey : notnull
    {
        if (!errors.TryGetValue(key, out var counts))
        {
            if (errors.Count >= MaxErrors)
                return;
            errors[key] = counts = [];
        }
        // Consumer identifiers are never empty, so "" counts requests without a consumer.
        var consumerKey = consumer ?? "";
        counts[consumerKey] = counts.GetValueOrDefault(consumerKey) + 1;
    }

    private static bool IsValidMethod(string method) =>
        method.Length is >= 2 and <= 12 && method.All(c => char.IsAsciiLetterUpper(c) || c == '-');

    private static string Truncate(string value, int maxLength) =>
        value.Length > maxLength ? value[..maxLength] : value;

    private static object?[] ToCountsBody(Dictionary<string, long> counts) =>
        [
            .. counts.Select(entry =>
                entry.Key == ""
                    ? new Dictionary<string, object?> { ["count"] = entry.Value }
                    : new Dictionary<string, object?>
                    {
                        ["consumer"] = entry.Key,
                        ["count"] = entry.Value,
                    }
            ),
        ];

    private sealed record ValidationErrorKey(
        string Method,
        string Path,
        string Source,
        string Field,
        string Message,
        string Type
    )
    {
        public Dictionary<string, object?> ToBody(Dictionary<string, long> counts) =>
            new()
            {
                ["method"] = Method,
                ["path"] = Path,
                ["source"] = Source,
                ["field"] = Field,
                ["message"] = Message,
                ["type"] = Type,
                ["counts"] = ToCountsBody(counts),
            };
    }

    private sealed record ServerErrorKey(
        string Method,
        string Path,
        string Type,
        string Message,
        string Stacktrace
    )
    {
        public Dictionary<string, object?> ToBody(Dictionary<string, long> counts) =>
            new()
            {
                ["method"] = Method,
                ["path"] = Path,
                ["type"] = Type,
                ["message"] = Message,
                ["stacktrace"] = Stacktrace,
                ["counts"] = ToCountsBody(counts),
            };
    }
}
