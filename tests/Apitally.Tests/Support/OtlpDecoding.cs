using System.IO.Compression;
using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;

namespace Apitally.Tests.Support;

internal static class OtlpDecoding
{
    public static byte[] Gunzip(byte[] bytes)
    {
        using var input = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    public static ExportTraceServiceRequest Traces(byte[] gzipped) =>
        ExportTraceServiceRequest.Parser.ParseFrom(Gunzip(gzipped));

    public static ExportLogsServiceRequest Logs(byte[] gzipped) =>
        ExportLogsServiceRequest.Parser.ParseFrom(Gunzip(gzipped));

    public static ExportMetricsServiceRequest Metrics(byte[] gzipped) =>
        ExportMetricsServiceRequest.Parser.ParseFrom(Gunzip(gzipped));

    public static object? Value(AnyValue value) =>
        value.ValueCase switch
        {
            AnyValue.ValueOneofCase.StringValue => value.StringValue,
            AnyValue.ValueOneofCase.BoolValue => value.BoolValue,
            AnyValue.ValueOneofCase.IntValue => value.IntValue,
            AnyValue.ValueOneofCase.DoubleValue => value.DoubleValue,
            AnyValue.ValueOneofCase.BytesValue => value.BytesValue.ToByteArray(),
            AnyValue.ValueOneofCase.ArrayValue => value.ArrayValue.Values.Select(Value).ToArray(),
            AnyValue.ValueOneofCase.KvlistValue => Attributes(value.KvlistValue.Values),
            _ => null,
        };

    public static Dictionary<string, object?> Attributes(IEnumerable<KeyValue> attributes) =>
        attributes.ToDictionary(attribute => attribute.Key, attribute => Value(attribute.Value));

    public static byte[] Concat(params IMessage[] messages) =>
        messages.SelectMany(message => message.ToByteArray()).ToArray();
}
