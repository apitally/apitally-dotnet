using Apitally.Export;
using Apitally.Tests.Support;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Resources;

namespace Apitally.Tests.Export;

public class OtlpEncoderTests
{
    [Fact]
    public void RequestsAreSplitToFitTheExactSizeLimit()
    {
        using var diagnostics = new DiagnosticsCollector();
        var spans = Enumerable
            .Range(0, 10)
            .Select(_ => TestSpans.Create(new() { ["payload"] = new string('x', 900_000) }))
            .ToList();
        var requests = new List<byte[]>();

        OtlpEncoder.EncodeRequests(
            spans,
            OtlpTraceMapper.BuildRequest,
            (request, size) =>
            {
                Assert.Equal(request.CalculateSize(), size);
                requests.Add(request.ToByteArray());
            },
            "traces",
            diagnostics.Diagnostics
        );

        Assert.All(requests, request => Assert.True(request.Length <= OtlpEncoder.MaxRequestSize));
        Assert.Equal(
            10,
            requests.Sum(request =>
                ExportTraceServiceRequest
                    .Parser.ParseFrom(request)
                    .ResourceSpans.Single()
                    .ScopeSpans.Single()
                    .Spans.Count
            )
        );
        Assert.Empty(diagnostics.Collector.GetSnapshot());
    }

    [Fact]
    public void RecordThatCannotFitAloneIsDroppedWithOneWarning()
    {
        using var diagnostics = new DiagnosticsCollector();
        var spans = new[]
        {
            TestSpans.Create(new() { ["payload"] = new string('x', OtlpEncoder.MaxRequestSize) }),
            TestSpans.Create(),
            TestSpans.Create(new() { ["payload"] = new string('y', OtlpEncoder.MaxRequestSize) }),
        };
        var requests = new List<byte[]>();

        OtlpEncoder.EncodeRequests(
            spans,
            OtlpTraceMapper.BuildRequest,
            (request, size) =>
            {
                Assert.Equal(request.CalculateSize(), size);
                requests.Add(request.ToByteArray());
            },
            "traces",
            diagnostics.Diagnostics
        );

        var request = ExportTraceServiceRequest.Parser.ParseFrom(Assert.Single(requests));
        Assert.Equal(
            spans[1].SpanId.ToHexString(),
            Convert
                .ToHexString(
                    request.ResourceSpans[0].ScopeSpans[0].Spans.Single().SpanId.ToByteArray()
                )
                .ToLowerInvariant()
        );
        Assert.Single(diagnostics.Records(LogLevel.Warning));
    }

    [Fact]
    public void ResourceUsesApitallyIdentityOverEnvironmentAttributes()
    {
        using var environment = new EnvironmentVariables(
            ("OTEL_RESOURCE_ATTRIBUTES", "deployment.environment.name=other,team=a"),
            ("OTEL_SERVICE_NAME", "orders")
        );

        var attributes = OtlpEncoder.CreateResource("prod").Attributes.ToDictionary();

        Assert.Equal("prod", attributes["deployment.environment.name"]);
        Assert.Equal(OtlpEncoder.InstanceId, attributes["service.instance.id"]);
        Assert.Equal("apitally-dotnet", attributes["telemetry.distro.name"]);
        Assert.Equal("orders", attributes["service.name"]);
        Assert.Equal("a", attributes["team"]);
    }

    [Fact]
    public void ExportResourceKeepsApplicationAttributesWithApitallyIdentity()
    {
        var application = new Resource(
            new Dictionary<string, object>
            {
                ["service.name"] = "orders",
                ["service.instance.id"] = "app-instance",
                ["deployment.environment.name"] = "staging",
            }
        );

        var attributes = OtlpEncoder
            .CreateExportResource(application, "prod")
            .Attributes.ToDictionary();

        Assert.Equal("orders", attributes["service.name"]);
        Assert.Equal(OtlpEncoder.InstanceId, attributes["service.instance.id"]);
        Assert.Equal("prod", attributes["deployment.environment.name"]);
    }

    [Fact]
    public void ValuesMapToOtlpAnyValues()
    {
        var map = new Dictionary<string, object?> { ["a"] = 1L, ["b"] = null };

        Assert.Equal("x", OtlpEncoder.ToAnyValue("x").StringValue);
        Assert.Equal(3, OtlpEncoder.ToAnyValue(3L).IntValue);
        Assert.Equal(1.5, OtlpEncoder.ToAnyValue(1.5).DoubleValue);
        Assert.True(OtlpEncoder.ToAnyValue(true).BoolValue);
        Assert.Equal([1, 2], OtlpEncoder.ToAnyValue(new byte[] { 1, 2 }).BytesValue.ToByteArray());
        Assert.Equal(
            new object?[] { "a", null },
            OtlpDecoding.Value(OtlpEncoder.ToAnyValue(new[] { "a", null }))
        );
        Assert.Equal(map, OtlpDecoding.Value(OtlpEncoder.ToAnyValue(map)));
        Assert.Equal(
            OpenTelemetry.Proto.Common.V1.AnyValue.ValueOneofCase.None,
            OtlpEncoder.ToAnyValue(null).ValueCase
        );
    }
}
