using Apitally.AspNetCore;

namespace Apitally.Requests;

// Bounded validation and server error groups between drains. Values are normalized and
// truncated before keying; new groups beyond the limit are silently ignored.
internal sealed class ErrorAggregates
{
    public const int MaxGroups = 100;

    private readonly object sync = new();
    private Dictionary<ValidationErrorKey, long> validationErrors = [];
    private Dictionary<ServerErrorKey, long> serverErrors = [];

    public void AddValidationErrors(
        string? consumer,
        string method,
        string path,
        IEnumerable<ValidationDetail> details
    )
    {
        if (!IsValidMethod(method))
            return;
        lock (sync)
        {
            foreach (var detail in details)
            {
                var key = new ValidationErrorKey(
                    consumer is null ? null : Truncate(consumer, 128),
                    method,
                    Truncate(path, 2_000),
                    Truncate(detail.Source, 32),
                    Truncate(detail.Field, 2_048),
                    Truncate(detail.Message, 2_048),
                    Truncate(detail.Type, 128)
                );
                Increment(validationErrors, key);
            }
        }
    }

    public void AddServerError(string? consumer, string method, string path, Exception exception)
    {
        if (!IsValidMethod(method))
            return;
        var key = new ServerErrorKey(
            consumer is null ? null : Truncate(consumer, 128),
            method,
            Truncate(path, 2_000),
            Truncate(exception.GetType().FullName ?? exception.GetType().Name, 256),
            Truncate(exception.Message.Trim(), 2_048),
            Truncate(ExceptionStacktrace.Get(exception), 65_536)
        );
        lock (sync)
            Increment(serverErrors, key);
    }

    // Swaps the groups atomically; the caller emits events outside the lock.
    public (
        List<Dictionary<string, object?>> Validation,
        List<Dictionary<string, object?>> Server
    ) Drain()
    {
        Dictionary<ValidationErrorKey, long> validation;
        Dictionary<ServerErrorKey, long> server;
        lock (sync)
        {
            (validation, validationErrors) = (validationErrors, []);
            (server, serverErrors) = (serverErrors, []);
        }
        return (
            [.. validation.Select(group => group.Key.ToBody(group.Value))],
            [.. server.Select(group => group.Key.ToBody(group.Value))]
        );
    }

    private static void Increment<TKey>(Dictionary<TKey, long> groups, TKey key)
        where TKey : notnull
    {
        if (groups.TryGetValue(key, out var count))
            groups[key] = Math.Min(count + 1, uint.MaxValue);
        else if (groups.Count < MaxGroups)
            groups[key] = 1;
    }

    private static bool IsValidMethod(string method) =>
        method.Length is >= 2 and <= 12 && method.All(c => char.IsAsciiLetterUpper(c) || c == '-');

    private static string Truncate(string value, int maxLength) =>
        value.Length > maxLength ? value[..maxLength] : value;

    private sealed record ValidationErrorKey(
        string? Consumer,
        string Method,
        string Path,
        string Source,
        string Field,
        string Message,
        string Type
    )
    {
        public Dictionary<string, object?> ToBody(long count) =>
            WithConsumer(
                Consumer,
                new()
                {
                    ["method"] = Method,
                    ["path"] = Path,
                    ["source"] = Source,
                    ["field"] = Field,
                    ["message"] = Message,
                    ["type"] = Type,
                    ["count"] = count,
                }
            );
    }

    private sealed record ServerErrorKey(
        string? Consumer,
        string Method,
        string Path,
        string Type,
        string Message,
        string Stacktrace
    )
    {
        public Dictionary<string, object?> ToBody(long count) =>
            WithConsumer(
                Consumer,
                new()
                {
                    ["method"] = Method,
                    ["path"] = Path,
                    ["type"] = Type,
                    ["message"] = Message,
                    ["stacktrace"] = Stacktrace,
                    ["count"] = count,
                }
            );
    }

    private static Dictionary<string, object?> WithConsumer(
        string? consumer,
        Dictionary<string, object?> body
    )
    {
        if (consumer is not null)
            body["consumer"] = consumer;
        return body;
    }
}
