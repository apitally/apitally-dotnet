#if UNSUPPORTED_PUBLIC_API
using OpenTelemetry.Logs;

internal static class UnsupportedApi
{
    public static void Probe(LogRecord record)
    {
        record.Body = new Dictionary<string, object?> { ["count"] = 1 };
        record.EventName = "apitally.request.server_error";
    }
}
#endif
