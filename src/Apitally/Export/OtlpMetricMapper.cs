using System.Globalization;
using Apitally.Metrics;
using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Resources;

namespace Apitally.Export;

internal static class OtlpMetricMapper
{
    public static IMessage BuildProcessRequest(
        ProcessMetricValues values,
        DateTime start,
        DateTime end,
        Resource resource
    )
    {
        var metrics = new List<Metric>();
        if (values.CpuUtilization is { } cpuUtilization)
            metrics.Add(
                Gauge(
                    "process.cpu.utilization",
                    "1",
                    "CPU utilization of the process, normalized across available CPUs",
                    new NumberDataPoint { AsDouble = cpuUtilization },
                    start,
                    end
                )
            );
        metrics.Add(
            Gauge(
                "process.memory.usage",
                "By",
                "Physical memory in use by the process",
                new NumberDataPoint { AsInt = values.MemoryUsage },
                start,
                end
            )
        );
        metrics.Add(
            Gauge(
                "process.uptime",
                "s",
                "Time since the process started",
                new NumberDataPoint { AsDouble = values.Uptime },
                start,
                end
            )
        );
        return BuildRequest(metrics, resource);
    }

    // Each combination's three histograms must be in the same request for the server to join them.
    public static IMessage BuildRequestMetricsRequest(
        IReadOnlyList<KeyValuePair<RequestMetricKey, RequestMetricValues>> requests,
        DateTime start,
        DateTime end,
        Resource resource
    )
    {
        var duration = Histogram(
            "http.server.request.duration",
            "s",
            "Duration of HTTP server requests"
        );
        var requestSize = Histogram(
            "http.server.request.body.size",
            "By",
            "Size of HTTP server request bodies"
        );
        var responseSize = Histogram(
            "http.server.response.body.size",
            "By",
            "Size of HTTP server response bodies"
        );
        var startNanoseconds = OtlpEncoder.ToUnixNanoseconds(start);
        var endNanoseconds = OtlpEncoder.ToUnixNanoseconds(end);
        foreach (var (key, values) in requests)
        {
            var attributes = ToAttributes(key);
            AddPoint(duration, values.Duration, attributes, startNanoseconds, endNanoseconds);
            AddPoint(
                requestSize,
                values.RequestBodySize,
                attributes,
                startNanoseconds,
                endNanoseconds
            );
            AddPoint(
                responseSize,
                values.ResponseBodySize,
                attributes,
                startNanoseconds,
                endNanoseconds
            );
        }
        List<Metric> metrics = [duration, requestSize, responseSize];
        metrics.RemoveAll(metric => metric.ExponentialHistogram.DataPoints.Count == 0);
        return BuildRequest(metrics, resource);
    }

    private static IMessage BuildRequest(IReadOnlyList<Metric> metrics, Resource resource) =>
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

    private static Metric Gauge(
        string name,
        string unit,
        string description,
        NumberDataPoint point,
        DateTime start,
        DateTime end
    )
    {
        point.StartTimeUnixNano = OtlpEncoder.ToUnixNanoseconds(start);
        point.TimeUnixNano = OtlpEncoder.ToUnixNanoseconds(end);
        return new()
        {
            Name = name,
            Unit = unit,
            Description = description,
            Gauge = new Gauge { DataPoints = { point } },
        };
    }

    private static Metric Histogram(string name, string unit, string description) =>
        new()
        {
            Name = name,
            Unit = unit,
            Description = description,
            ExponentialHistogram = new() { AggregationTemporality = AggregationTemporality.Delta },
        };

    private static List<KeyValue> ToAttributes(RequestMetricKey key)
    {
        var attributes = new List<KeyValue>
        {
            OtlpEncoder.ToKeyValue("http.request.method", key.Method),
            OtlpEncoder.ToKeyValue("http.route", key.Route),
            OtlpEncoder.ToKeyValue("http.response.status_code", (long)key.StatusCode),
            OtlpEncoder.ToKeyValue("url.scheme", key.Scheme),
        };
        if (key.ConsumerIdentifier is not null)
            attributes.Add(
                OtlpEncoder.ToKeyValue("apitally.consumer.identifier", key.ConsumerIdentifier)
            );
        if (key.StatusCode >= 500)
            attributes.Add(
                OtlpEncoder.ToKeyValue(
                    "error.type",
                    key.StatusCode.ToString(CultureInfo.InvariantCulture)
                )
            );
        return attributes;
    }

    private static void AddPoint(
        Metric metric,
        Metrics.ExponentialHistogram histogram,
        List<KeyValue> attributes,
        ulong start,
        ulong end
    )
    {
        if (histogram.Count == 0)
            return;
        var point = new ExponentialHistogramDataPoint
        {
            StartTimeUnixNano = start,
            TimeUnixNano = end,
            Count = (ulong)histogram.Count,
            Sum = histogram.Sum,
            Scale = Metrics.ExponentialHistogram.Scale,
            ZeroCount = (ulong)histogram.ZeroCount,
            Min = histogram.Min,
            Max = histogram.Max,
            Positive = new ExponentialHistogramDataPoint.Types.Buckets
            {
                Offset = histogram.Offset,
            },
            Attributes = { attributes },
        };
        if (histogram.Buckets is { } buckets)
        {
            point.Positive.BucketCounts.Capacity = buckets.Length;
            foreach (var count in buckets)
                point.Positive.BucketCounts.Add((ulong)count);
        }
        metric.ExponentialHistogram.DataPoints.Add(point);
    }
}
