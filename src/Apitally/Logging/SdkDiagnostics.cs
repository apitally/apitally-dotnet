using System.Collections.Concurrent;
using Apitally.Export;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Apitally.Logging;

// Diagnostics go through the application's logging; the Apitally logger provider excludes
// this category so they never reach Apitally.
internal sealed partial class SdkDiagnostics
{
    public const string CategoryName = "Apitally";
    private const string SpoolWriteFailedKey = "spool-write-failed";

    private readonly ILogger logger;
    private readonly ConcurrentDictionary<string, bool> raisedWarnings = new();

    public SdkDiagnostics(ILoggerFactory loggerFactory) =>
        logger = loggerFactory.CreateLogger(CategoryName);

    public static SdkDiagnostics None { get; } = new(NullLoggerFactory.Instance);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Apitally write token is missing, so telemetry is disabled. Set the Apitally:WriteToken configuration value or the APITALLY_WRITE_TOKEN environment variable."
    )]
    public partial void WriteTokenMissing();

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Apitally write token has an invalid format ({MaskedToken}), so telemetry is disabled."
    )]
    public partial void WriteTokenInvalid(string maskedToken);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Pattern in Apitally option {OptionName} ignored because it is not a valid regular expression: {Pattern}"
    )]
    public partial void PatternInvalid(string optionName, string pattern, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Apitally is disabled.")]
    public partial void Disabled();

    public void OversizedRecordDropped(string signalName, int limit)
    {
        if (ShouldWarn("oversized-record-" + signalName))
            LogOversizedRecordDropped(signalName, limit);
    }

    public void SpanRedactionFailed(Exception exception)
    {
        if (ShouldWarn("span-redaction-failed"))
            LogSpanRedactionFailed(exception);
    }

    public void BodyMaskFailed(string optionName, Exception exception)
    {
        if (ShouldWarn("body-mask-failed-" + optionName))
            LogBodyMaskFailed(optionName, exception);
    }

    public void SpoolWriteFailed(Exception exception)
    {
        if (ShouldWarn(SpoolWriteFailedKey))
            LogSpoolWriteFailed(exception);
    }

    // The write failure warning is raised again only after writes have recovered.
    public void SpoolWriteSucceeded() => raisedWarnings.TryRemove(SpoolWriteFailedKey, out _);

    public void SpoolSizeLimitReached(TelemetrySignal signal)
    {
        if (ShouldWarn("spool-size-limit-" + signal))
            LogSpoolSizeLimitReached(signal);
    }

    public void ExportRejected(TelemetrySignal signal, int statusCode)
    {
        if (ShouldWarn("export-rejected-" + statusCode))
            LogExportRejected(signal, statusCode);
    }

    public void MetricCapacityExceeded()
    {
        if (ShouldWarn("metric-capacity-exceeded"))
            LogMetricCapacityExceeded();
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Apitally failed to start, so the application runs without telemetry."
    )]
    public partial void PreparationFailed(Exception exception);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Apitally failed to activate, so the application runs without telemetry."
    )]
    public partial void ActivationFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error during Apitally shutdown.")]
    public partial void ShutdownFailed(Exception exception);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Error reading the application's routes for the Apitally startup event."
    )]
    public partial void RouteEnumerationFailed(Exception exception);

    public void SamplingCallbackFailed(string optionName, Exception exception)
    {
        if (ShouldWarn("sampling-callback-failed-" + optionName))
            LogSamplingCallbackFailed(optionName, exception);
    }

    public void SamplingCallbackInvalid(string optionName)
    {
        if (ShouldWarn("sampling-callback-invalid-" + optionName))
            LogSamplingCallbackInvalid(optionName);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Error processing a request for Apitally.")]
    public partial void RequestProcessingFailed(Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The Apitally {OptionName} callback threw an exception, so the request was kept."
    )]
    private partial void LogSamplingCallbackFailed(string optionName, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The Apitally {OptionName} callback returned a value outside [0, 1], so the request was kept. Return a probability between 0 and 1, or null to abstain."
    )]
    private partial void LogSamplingCallbackInvalid(string optionName);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Error exporting Apitally metrics.")]
    public partial void MetricExportFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Error in Apitally export cycle.")]
    public partial void ExportCycleFailed(Exception exception);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Sending buffered {Signal} to Apitally failed (HTTP {StatusCode}), will retry."
    )]
    public partial void ExportRetryable(TelemetrySignal signal, int? statusCode);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Unable to create temporary files, so Apitally buffers telemetry in memory (max {MaxMegabytes} MB)."
    )]
    public partial void SpoolUsingMemory(long maxMegabytes);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Buffered Apitally {Signal} could not be delivered within an hour and were dropped."
    )]
    public partial void SpoolFileExpired(TelemetrySignal signal);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Apitally rejected buffered {Signal} with HTTP {StatusCode}, so they were dropped. Check the Apitally write token and environment."
    )]
    private partial void LogExportRejected(TelemetrySignal signal, int statusCode);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Apitally request metrics exceeded the capacity of 10,000 distinct attribute combinations per collection interval, so some request metrics are missing. See https://docs.apitally.io or contact support if this persists."
    )]
    private partial void LogMetricCapacityExceeded();

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "An Apitally {SignalName} record exceeded the {Limit}-byte export limit on its own and was dropped. Reduce the size of large attributes or log messages."
    )]
    private partial void LogOversizedRecordDropped(string signalName, int limit);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to redact a span for export to Apitally, so the span was dropped."
    )]
    private partial void LogSpanRedactionFailed(Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The Apitally {OptionName} callback threw an exception, so the body was replaced with [REDACTED]."
    )]
    private partial void LogBodyMaskFailed(string optionName, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Error writing Apitally telemetry to the buffer, so buffered telemetry was dropped."
    )]
    private partial void LogSpoolWriteFailed(Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Apitally buffer size limit reached, so the oldest buffered {Signal} were dropped. Check connectivity to Apitally."
    )]
    private partial void LogSpoolSizeLimitReached(TelemetrySignal signal);

    private bool ShouldWarn(string key) => raisedWarnings.TryAdd(key, true);
}
