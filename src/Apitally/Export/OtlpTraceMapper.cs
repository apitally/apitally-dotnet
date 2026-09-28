using System.Diagnostics;
using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Trace.V1;
using OtlpSpan = OpenTelemetry.Proto.Trace.V1.Span;
using OtlpStatus = OpenTelemetry.Proto.Trace.V1.Status;

namespace Apitally.Export;

internal static class OtlpTraceMapper
{
    public static IMessage BuildRequest(IReadOnlyList<SpanSnapshot> spans)
    {
        var request = new ExportTraceServiceRequest();
        foreach (var resourceGroup in spans.GroupBy(span => span.Resource))
        {
            var resourceSpans = new ResourceSpans
            {
                Resource = OtlpEncoder.ToOtlpResource(resourceGroup.Key),
            };
            foreach (
                var scopeGroup in resourceGroup.GroupBy(span => (span.ScopeName, span.ScopeVersion))
            )
            {
                var scopeSpans = new ScopeSpans
                {
                    Scope = new InstrumentationScope
                    {
                        Name = scopeGroup.Key.ScopeName,
                        Version = scopeGroup.Key.ScopeVersion ?? "",
                    },
                };
                scopeSpans.Spans.Add(scopeGroup.Select(ToOtlpSpan));
                resourceSpans.ScopeSpans.Add(scopeSpans);
            }
            request.ResourceSpans.Add(resourceSpans);
        }
        return request;
    }

    private static OtlpSpan ToOtlpSpan(SpanSnapshot span)
    {
        var output = new OtlpSpan
        {
            TraceId = OtlpEncoder.ToByteString(span.TraceId),
            SpanId = OtlpEncoder.ToByteString(span.SpanId),
            ParentSpanId = OtlpEncoder.ToByteString(span.ParentSpanId),
            TraceState = span.TraceStateString ?? "",
            Flags = (uint)span.TraceFlags,
            Name = span.DisplayName,
            Kind = ToOtlpKind(span.Kind),
            StartTimeUnixNano = OtlpEncoder.ToUnixNanoseconds(span.StartTimeUtc),
            EndTimeUnixNano = OtlpEncoder.ToUnixNanoseconds(
                span.StartTimeUtc + (span.Duration ?? TimeSpan.Zero)
            ),
            Status = ToOtlpStatus(span.Status, span.StatusDescription),
        };
        output.Attributes.Add(OtlpEncoder.ToKeyValues(span.Attributes));
        foreach (var activityEvent in span.Events)
        {
            var otlpEvent = new OtlpSpan.Types.Event
            {
                Name = activityEvent.Name,
                TimeUnixNano = OtlpEncoder.ToUnixNanoseconds(activityEvent.Timestamp.UtcDateTime),
            };
            otlpEvent.Attributes.Add(OtlpEncoder.ToKeyValues(activityEvent.Tags));
            output.Events.Add(otlpEvent);
        }
        foreach (var link in span.Links)
        {
            var otlpLink = new OtlpSpan.Types.Link
            {
                TraceId = OtlpEncoder.ToByteString(link.Context.TraceId),
                SpanId = OtlpEncoder.ToByteString(link.Context.SpanId),
                TraceState = link.Context.TraceState ?? "",
                Flags = (uint)link.Context.TraceFlags,
            };
            if (link.Tags is not null)
                otlpLink.Attributes.Add(OtlpEncoder.ToKeyValues(link.Tags));
            output.Links.Add(otlpLink);
        }
        return output;
    }

    private static OtlpSpan.Types.SpanKind ToOtlpKind(ActivityKind kind) =>
        kind switch
        {
            ActivityKind.Server => OtlpSpan.Types.SpanKind.Server,
            ActivityKind.Client => OtlpSpan.Types.SpanKind.Client,
            ActivityKind.Producer => OtlpSpan.Types.SpanKind.Producer,
            ActivityKind.Consumer => OtlpSpan.Types.SpanKind.Consumer,
            _ => OtlpSpan.Types.SpanKind.Internal,
        };

    private static OtlpStatus ToOtlpStatus(ActivityStatusCode status, string? description) =>
        status switch
        {
            ActivityStatusCode.Ok => new OtlpStatus { Code = OtlpStatus.Types.StatusCode.Ok },
            ActivityStatusCode.Error => new OtlpStatus
            {
                Code = OtlpStatus.Types.StatusCode.Error,
                Message = description ?? "",
            },
            _ => new OtlpStatus(),
        };
}
