using Google.Protobuf;
using OpenTelemetry.Metrics;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Resources;
using OtlpMetric = OpenTelemetry.Proto.Metrics.V1.Metric;
using OtlpTemporality = OpenTelemetry.Proto.Metrics.V1.AggregationTemporality;
using SdkMetric = OpenTelemetry.Metrics.Metric;

namespace Apitally.Export;

internal static class OtlpMetricMapper
{
    private const string OverflowAttribute = "otel.metric.overflow";

    // Must run inside the exporter call: metric points reference reusable SDK storage.
    // Overflow points carry no request dimensions and are omitted.
    public static List<OtlpMetric> Map(IEnumerable<SdkMetric> metrics, out bool hasOverflow)
    {
        hasOverflow = false;
        var output = new List<OtlpMetric>();
        foreach (var metric in metrics)
        {
            var mapped = new OtlpMetric
            {
                Name = metric.Name,
                Unit = metric.Unit ?? "",
                Description = metric.Description ?? "",
            };
            foreach (ref readonly var point in metric.GetMetricPoints())
            {
                var attributes = new List<KeyValue>();
                var isOverflow = false;
                foreach (var tag in point.Tags)
                {
                    isOverflow |= tag.Key == OverflowAttribute;
                    attributes.Add(OtlpEncoder.ToKeyValue(tag.Key, tag.Value));
                }
                if (isOverflow)
                {
                    hasOverflow = true;
                    continue;
                }
                AddPoint(mapped, metric, point, attributes);
            }
            if (mapped.DataCase != OtlpMetric.DataOneofCase.None)
                output.Add(mapped);
        }
        return output;
    }

    public static IMessage BuildRequest(IReadOnlyList<OtlpMetric> metrics, Resource resource) =>
        new ExportMetricsServiceRequest
        {
            ResourceMetrics =
            {
                new ResourceMetrics
                {
                    Resource = OtlpEncoder.ToOtlpResource(resource),
                    ScopeMetrics =
                    {
                        new ScopeMetrics
                        {
                            Scope = new InstrumentationScope { Name = OtlpEncoder.ScopeName },
                            Metrics = { metrics },
                        },
                    },
                },
            },
        };

    private static void AddPoint(
        OtlpMetric output,
        SdkMetric metric,
        in MetricPoint point,
        List<KeyValue> attributes
    )
    {
        var start = OtlpEncoder.ToUnixNanoseconds(point.StartTime.UtcDateTime);
        var end = OtlpEncoder.ToUnixNanoseconds(point.EndTime.UtcDateTime);
        switch (metric.MetricType)
        {
            case MetricType.ExponentialHistogram:
                var data = point.GetExponentialHistogramData();
                var histogramPoint = new ExponentialHistogramDataPoint
                {
                    StartTimeUnixNano = start,
                    TimeUnixNano = end,
                    Count = (ulong)point.GetHistogramCount(),
                    Sum = point.GetHistogramSum(),
                    Scale = data.Scale,
                    ZeroCount = (ulong)data.ZeroCount,
                    Positive = new ExponentialHistogramDataPoint.Types.Buckets
                    {
                        Offset = data.PositiveBuckets.Offset,
                    },
                    Attributes = { attributes },
                };
                foreach (var count in data.PositiveBuckets)
                    histogramPoint.Positive.BucketCounts.Add((ulong)count);
                if (point.TryGetHistogramMinMaxValues(out var min, out var max))
                {
                    histogramPoint.Min = min;
                    histogramPoint.Max = max;
                }
                output.ExponentialHistogram ??= new ExponentialHistogram
                {
                    AggregationTemporality = ToOtlpTemporality(metric.Temporality),
                };
                output.ExponentialHistogram.DataPoints.Add(histogramPoint);
                break;
            case MetricType.DoubleGauge:
            case MetricType.LongGauge:
                var gaugePoint = new NumberDataPoint
                {
                    StartTimeUnixNano = start,
                    TimeUnixNano = end,
                    Attributes = { attributes },
                };
                if (metric.MetricType == MetricType.LongGauge)
                    gaugePoint.AsInt = point.GetGaugeLastValueLong();
                else
                    gaugePoint.AsDouble = point.GetGaugeLastValueDouble();
                output.Gauge ??= new Gauge();
                output.Gauge.DataPoints.Add(gaugePoint);
                break;
        }
    }

    private static OtlpTemporality ToOtlpTemporality(
        OpenTelemetry.Metrics.AggregationTemporality temporality
    ) =>
        temporality == OpenTelemetry.Metrics.AggregationTemporality.Delta
            ? OtlpTemporality.Delta
            : OtlpTemporality.Cumulative;
}
