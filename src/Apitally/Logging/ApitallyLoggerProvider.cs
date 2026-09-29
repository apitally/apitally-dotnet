using System.Diagnostics;
using Apitally.Requests;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Apitally.Logging;

// An additive ILogger provider capturing the rendered messages of request-scoped application
// logs. It is inert until the runtime attaches it and never changes what other providers
// receive. Structured values and scopes are not captured, because the server does not store them.
[ProviderAlias("Apitally")]
internal sealed class ApitallyLoggerProvider : ILoggerProvider
{
    private volatile LogCapture? capture;

    public ILogger CreateLogger(string categoryName) =>
        IsExcludedCategory(categoryName)
            ? NullLogger.Instance
            : new ApitallyLogger(categoryName, this);

    public void Attach(
        RequestRegistry registry,
        Func<LogRecordSnapshot, LogRecordSnapshot?>? mask
    ) => capture = new LogCapture(registry, mask);

    public void Detach() => capture = null;

    public void Dispose() => Detach();

    // SDK and OTel diagnostics must never feed back into the export. Framework request, HttpClient
    // and YARP forwarder logs repeat what the request log and CLIENT spans already show, and
    // HttpClient logs on .NET 8 and YARP forwarder logs include unredacted query strings.
    private static bool IsExcludedCategory(string categoryName) =>
        categoryName == SdkDiagnostics.CategoryName
        || categoryName.StartsWith(SdkDiagnostics.CategoryName + ".", StringComparison.Ordinal)
        || categoryName.StartsWith("OpenTelemetry", StringComparison.Ordinal)
        || categoryName.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
        || categoryName.StartsWith("System.Net.Http.HttpClient", StringComparison.Ordinal)
        || categoryName.StartsWith("Yarp.ReverseProxy.Forwarder", StringComparison.Ordinal);

    // Exceptions, a replacement record or an empty body drop the record rather than export
    // unmasked content.
    private static bool TryMask(
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

    private sealed record LogCapture(
        RequestRegistry Registry,
        Func<LogRecordSnapshot, LogRecordSnapshot?>? Mask
    );

    private sealed class ApitallyLogger(string categoryName, ApitallyLoggerProvider provider)
        : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None
            && provider.capture is not null
            && Activity.Current is not null;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (
                logLevel == LogLevel.None
                || provider.capture is not { } capture
                || Activity.Current is not { } activity
                || !capture.Registry.TryGet(activity.TraceId, activity.SpanId, out var request)
                || !request.IsAcceptingDetail
            )
                return;
            var record = new LogRecordSnapshot(
                DateTime.UtcNow,
                categoryName,
                logLevel,
                eventId,
                formatter(state, exception)
            );
            if (!TryMask(record, capture.Mask))
                return;
            // Linkage is added after masking, so the callback cannot unlink or reassign a record.
            request.AddLog(
                LogSnapshot.ForApplicationLog(
                    record,
                    activity.TraceId,
                    activity.SpanId,
                    activity.ActivityTraceFlags,
                    request.ServerSpanId
                )
            );
        }
    }
}
