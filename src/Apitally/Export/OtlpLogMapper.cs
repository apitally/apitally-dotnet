using Apitally.Logging;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Resources;

namespace Apitally.Export;

internal static class OtlpLogMapper
{
    public const string ServerSpanIdAttribute = "apitally.request.server_span_id";

    public static IMessage BuildRequest(IReadOnlyList<LogSnapshot> logs, Resource resource)
    {
        var resourceLogs = new ResourceLogs { Resource = OtlpEncoder.ToOtlpResource(resource) };
        foreach (var scopeGroup in logs.GroupBy(log => log.ScopeName))
        {
            var scopeLogs = new ScopeLogs
            {
                Scope = new InstrumentationScope { Name = scopeGroup.Key },
            };
            scopeLogs.LogRecords.Add(scopeGroup.Select(ToOtlpLogRecord));
            resourceLogs.ScopeLogs.Add(scopeLogs);
        }
        return new ExportLogsServiceRequest { ResourceLogs = { resourceLogs } };
    }

    private static LogRecord ToOtlpLogRecord(LogSnapshot log)
    {
        var time = OtlpEncoder.ToUnixNanoseconds(log.Timestamp);
        var output = new LogRecord { TimeUnixNano = time, ObservedTimeUnixNano = time };
        if (log.Record is { } record)
        {
            output.SeverityNumber = ToSeverityNumber(record.LogLevel);
            output.SeverityText = record.LogLevel.ToString();
            output.Body = new AnyValue { StringValue = record.Body ?? "" };
            output.EventName = record.EventId.Name ?? "";
            output.TraceId = OtlpEncoder.ToByteString(log.TraceId);
            output.SpanId = OtlpEncoder.ToByteString(log.SpanId);
            output.Flags = (uint)log.TraceFlags;
            output.Attributes.Add(
                OtlpEncoder.ToKeyValue(ServerSpanIdAttribute, log.ServerSpanId.ToHexString())
            );
        }
        else
        {
            output.EventName = log.EventName ?? "";
            output.Body = OtlpEncoder.ToAnyValue(log.EventBody);
        }
        return output;
    }

    private static SeverityNumber ToSeverityNumber(LogLevel level) =>
        level switch
        {
            LogLevel.Trace => SeverityNumber.Trace,
            LogLevel.Debug => SeverityNumber.Debug,
            LogLevel.Information => SeverityNumber.Info,
            LogLevel.Warning => SeverityNumber.Warn,
            LogLevel.Error => SeverityNumber.Error,
            LogLevel.Critical => SeverityNumber.Fatal,
            _ => SeverityNumber.Unspecified,
        };
}
