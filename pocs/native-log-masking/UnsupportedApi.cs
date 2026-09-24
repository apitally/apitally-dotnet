#if UNSUPPORTED_PUBLIC_CONSTRUCTOR
using OpenTelemetry.Logs;

internal static class UnsupportedApi
{
    public static LogRecord Construct() => new LogRecord();
}
#endif
