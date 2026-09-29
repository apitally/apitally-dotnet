using System.Diagnostics;
using Apitally.Export;
using Apitally.Tests.Support;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Resources;
using OtlpSpan = OpenTelemetry.Proto.Trace.V1.Span;

namespace Apitally.Tests.Export;

public class OtlpTraceMapperTests
{
    [Fact]
    public void MapsCompleteSpanFields()
    {
        var parent = ActivitySpanId.CreateRandom();
        var linked = new ActivityContext(
            ActivityTraceId.CreateRandom(),
            ActivitySpanId.CreateRandom(),
            ActivityTraceFlags.Recorded,
            "k=v"
        );
        var span = new SpanSnapshot
        {
            TraceId = ActivityTraceId.CreateRandom(),
            SpanId = ActivitySpanId.CreateRandom(),
            ParentSpanId = parent,
            TraceFlags = ActivityTraceFlags.Recorded,
            TraceStateString = "vendor=1",
            DisplayName = "GET /items/{id}",
            Kind = ActivityKind.Server,
            StartTimeUtc = new DateTime(2026, 9, 28, 10, 0, 0, DateTimeKind.Utc),
            Duration = TimeSpan.FromMilliseconds(250),
            Status = ActivityStatusCode.Error,
            StatusDescription = "failed",
            OwnedAttributes = new()
            {
                ["http.route"] = "/items/{id}",
                ["http.response.status_code"] = 500L,
            },
            OwnedEvents =
            [
                new ActivityEvent(
                    "exception",
                    new DateTimeOffset(2026, 9, 28, 10, 0, 0, 100, TimeSpan.Zero),
                    new ActivityTagsCollection { ["exception.type"] = "System.Exception" }
                ),
            ],
            Links =
            [
                new ActivityLink(linked, new ActivityTagsCollection { ["link.kind"] = "retry" }),
            ],
            Resource = TestSpans.Resource,
            ScopeName = "Microsoft.AspNetCore",
            ScopeVersion = "1.0",
        };

        var request = (ExportTraceServiceRequest)OtlpTraceMapper.BuildRequest([span]);

        var resourceSpans = Assert.Single(request.ResourceSpans);
        Assert.Contains(resourceSpans.Resource.Attributes, a => a.Key == "service.instance.id");
        var scopeSpans = Assert.Single(resourceSpans.ScopeSpans);
        Assert.Equal("Microsoft.AspNetCore", scopeSpans.Scope.Name);
        Assert.Equal("1.0", scopeSpans.Scope.Version);
        var output = Assert.Single(scopeSpans.Spans);
        Assert.Equal(span.TraceId.ToHexString(), output.TraceId.Hex());
        Assert.Equal(span.SpanId.ToHexString(), output.SpanId.Hex());
        Assert.Equal(parent.ToHexString(), output.ParentSpanId.Hex());
        Assert.Equal("vendor=1", output.TraceState);
        Assert.Equal(1u, output.Flags);
        Assert.Equal("GET /items/{id}", output.Name);
        Assert.Equal(OtlpSpan.Types.SpanKind.Server, output.Kind);
        Assert.Equal(250_000_000ul, output.EndTimeUnixNano - output.StartTimeUnixNano);
        Assert.Equal(
            OpenTelemetry.Proto.Trace.V1.Status.Types.StatusCode.Error,
            output.Status.Code
        );
        Assert.Equal("failed", output.Status.Message);
        Assert.Equal(
            new Dictionary<string, object?>
            {
                ["http.route"] = "/items/{id}",
                ["http.response.status_code"] = 500L,
            },
            OtlpDecoding.Attributes(output.Attributes)
        );
        var exceptionEvent = Assert.Single(output.Events);
        Assert.Equal("exception", exceptionEvent.Name);
        Assert.Equal(
            "System.Exception",
            OtlpDecoding.Attributes(exceptionEvent.Attributes)["exception.type"]
        );
        var link = Assert.Single(output.Links);
        Assert.Equal(linked.SpanId.ToHexString(), link.SpanId.Hex());
        Assert.Equal("k=v", link.TraceState);
        Assert.Equal("retry", OtlpDecoding.Attributes(link.Attributes)["link.kind"]);
    }

    [Fact]
    public void GroupsSpansByResource()
    {
        var other = new Resource(new Dictionary<string, object> { ["service.name"] = "other" });
        var spans = new[]
        {
            TestSpans.Create(),
            TestSpans.Create(kind: ActivityKind.Internal),
            new SpanSnapshot
            {
                TraceId = ActivityTraceId.CreateRandom(),
                SpanId = ActivitySpanId.CreateRandom(),
                DisplayName = "work",
                Kind = ActivityKind.Internal,
                StartTimeUtc = DateTime.UtcNow,
                Duration = TimeSpan.Zero,
                Resource = other,
                ScopeName = "apitally.otel",
            },
        };

        var request = (ExportTraceServiceRequest)OtlpTraceMapper.BuildRequest(spans);

        Assert.Equal(2, request.ResourceSpans.Count);
        Assert.Equal(2, request.ResourceSpans[0].ScopeSpans.Single().Spans.Count);
        Assert.Equal("apitally.otel", request.ResourceSpans[1].ScopeSpans.Single().Scope.Name);
        Assert.True(request.ResourceSpans[1].ScopeSpans[0].Spans[0].ParentSpanId.IsEmpty);
    }
}
