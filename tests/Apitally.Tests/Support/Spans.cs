using OpenTelemetry.Proto.Trace.V1;

namespace Apitally.Tests.Support;

internal static class Spans
{
    public static Dictionary<string, object?> Attributes(this Span span) =>
        OtlpDecoding.Attributes(span.Attributes);

    public static string Hex(this Google.Protobuf.ByteString bytes) =>
        Convert.ToHexString(bytes.ToByteArray()).ToLowerInvariant();

    public static Span Server(this IEnumerable<Span> spans) =>
        Assert.Single(spans, span => span.Kind == Span.Types.SpanKind.Server);
}
