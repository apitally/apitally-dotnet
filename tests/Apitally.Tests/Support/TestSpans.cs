using System.Diagnostics;
using Apitally.Export;
using OpenTelemetry.Resources;

namespace Apitally.Tests.Support;

internal static class TestSpans
{
    public static readonly Resource Resource = OtlpEncoder.CreateResource("test");

    public static SpanSnapshot Create(
        Dictionary<string, object?>? attributes = null,
        ActivityKind kind = ActivityKind.Server,
        string name = "GET /items/{id}",
        ActivitySpanId parentSpanId = default,
        ActivityTraceId? traceId = null
    ) =>
        new()
        {
            TraceId = traceId ?? ActivityTraceId.CreateRandom(),
            SpanId = ActivitySpanId.CreateRandom(),
            ParentSpanId = parentSpanId,
            TraceFlags = ActivityTraceFlags.Recorded,
            DisplayName = name,
            Kind = kind,
            StartTimeUtc = new DateTime(2026, 9, 28, 10, 0, 0, DateTimeKind.Utc),
            Duration = TimeSpan.FromMilliseconds(25),
            OwnedAttributes = attributes ?? [],
            Resource = Resource,
            ScopeName = "Microsoft.AspNetCore",
        };
}
