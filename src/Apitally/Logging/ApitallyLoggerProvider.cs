using System.Diagnostics;
using Apitally.Requests;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Apitally.Logging;

// An additive ILogger provider capturing request-scoped application logs. It is inert until
// the runtime attaches it and never changes what other providers receive.
[ProviderAlias("Apitally")]
internal sealed class ApitallyLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private volatile LogCapture? capture;
    private IExternalScopeProvider? scopeProvider;

    public ILogger CreateLogger(string categoryName) =>
        IsExcludedCategory(categoryName)
            ? NullLogger.Instance
            : new ApitallyLogger(categoryName, this);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) =>
        this.scopeProvider = scopeProvider;

    public void Attach(
        RequestRegistry registry,
        Func<LogRecordSnapshot, LogRecordSnapshot?>? mask
    ) => capture = new LogCapture(registry, mask);

    public void Detach() => capture = null;

    public void Dispose() => Detach();

    // SDK and OTel diagnostics must never feed back into the export, and framework request
    // logs repeat what the request log already shows.
    private static bool IsExcludedCategory(string categoryName) =>
        categoryName == SdkDiagnostics.CategoryName
        || categoryName.StartsWith(SdkDiagnostics.CategoryName + ".", StringComparison.Ordinal)
        || categoryName.StartsWith("OpenTelemetry", StringComparison.Ordinal)
        || categoryName.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal);

    private sealed record LogCapture(
        RequestRegistry Registry,
        Func<LogRecordSnapshot, LogRecordSnapshot?>? Mask
    );

    private sealed class ApitallyLogger(string categoryName, ApitallyLoggerProvider provider)
        : ILogger
    {
        // The logger factory pushes scopes to the external scope provider.
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
                !IsEnabled(logLevel)
                || provider.capture is not { } capture
                || Activity.Current is not { } activity
                || !capture.Registry.TryGet(activity.TraceId, activity.SpanId, out var request)
                || !request.IsAcceptingDetail
            )
                return;
            var record = LogMasking.CreateRecord(
                categoryName,
                logLevel,
                eventId,
                state,
                exception,
                formatter,
                provider.scopeProvider
            );
            if (record is null || !LogMasking.TryMask(record, capture.Mask))
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
