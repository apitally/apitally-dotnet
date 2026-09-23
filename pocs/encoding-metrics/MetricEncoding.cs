using Google.Protobuf;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Resources;
using OtlpMetric = OpenTelemetry.Proto.Metrics.V1.Metric;
using OtlpResource = OpenTelemetry.Proto.Resource.V1.Resource;
using SdkMetric = OpenTelemetry.Metrics.Metric;

namespace EncodingMetrics;

internal sealed class MetricEncoding : BaseExporter<SdkMetric>
{
    public byte[] Bytes { get; private set; } = [];
    public int ExportCount { get; private set; }
    public Exception? Error { get; private set; }

    public override ExportResult Export(in Batch<SdkMetric> batch)
    {
        try
        {
            var request = new ExportMetricsServiceRequest();
            var resource = new ResourceMetrics { Resource = new OtlpResource() };
            foreach (var attribute in ParentProvider.GetResource().Attributes)
                resource.Resource.Attributes.Add(Wire.Attribute(attribute.Key, attribute.Value));
            request.ResourceMetrics.Add(resource);
            foreach (var metric in batch)
            {
                var scope = new ScopeMetrics
                {
                    Scope = new InstrumentationScope
                    {
                        Name = metric.MeterName,
                        Version = metric.MeterVersion ?? "",
                    },
                };
                resource.ScopeMetrics.Add(scope);
                var output = new OtlpMetric
                {
                    Name = metric.Name,
                    Unit = metric.Unit ?? "",
                    Description = metric.Description ?? "",
                };
                scope.Metrics.Add(output);
                foreach (ref readonly var point in metric.GetMetricPoints())
                {
                    var tags = new List<KeyValue>();
                    foreach (var tag in point.Tags)
                        tags.Add(Wire.Attribute(tag.Key, tag.Value));
                    if (metric.MetricType == MetricType.ExponentialHistogram)
                    {
                        output.ExponentialHistogram ??= new ExponentialHistogram
                        {
                            AggregationTemporality = Temporality(metric),
                        };
                        var data = point.GetExponentialHistogramData();
                        var value = new ExponentialHistogramDataPoint
                        {
                            StartTimeUnixNano = Wire.Nanoseconds(point.StartTime),
                            TimeUnixNano = Wire.Nanoseconds(point.EndTime),
                            Count = checked((ulong)point.GetHistogramCount()),
                            Sum = point.GetHistogramSum(),
                            Scale = data.Scale,
                            ZeroCount = checked((ulong)data.ZeroCount),
                            ZeroThreshold = 0,
                            Positive = new ExponentialHistogramDataPoint.Types.Buckets
                            {
                                Offset = data.PositiveBuckets.Offset,
                            },
                        };
                        foreach (var count in data.PositiveBuckets)
                            value.Positive.BucketCounts.Add(checked((ulong)count));
                        if (point.TryGetHistogramMinMaxValues(out var min, out var max))
                        {
                            value.Min = min;
                            value.Max = max;
                        }
                        value.Attributes.Add(tags);
                        output.ExponentialHistogram.DataPoints.Add(value);
                    }
                    else if (
                        metric.MetricType
                        is MetricType.LongGauge
                            or MetricType.DoubleGauge
                            or MetricType.LongSum
                    )
                    {
                        var value = new NumberDataPoint
                        {
                            StartTimeUnixNano = Wire.Nanoseconds(point.StartTime),
                            TimeUnixNano = Wire.Nanoseconds(point.EndTime),
                        };
                        value.Attributes.Add(tags);
                        if (metric.MetricType == MetricType.LongSum)
                        {
                            value.AsInt = point.GetSumLong();
                            output.Sum ??= new Sum
                            {
                                AggregationTemporality = Temporality(metric),
                                IsMonotonic = true,
                            };
                            output.Sum.DataPoints.Add(value);
                        }
                        else
                        {
                            if (metric.MetricType == MetricType.LongGauge)
                                value.AsInt = point.GetGaugeLastValueLong();
                            else
                                value.AsDouble = point.GetGaugeLastValueDouble();
                            output.Gauge ??= new Gauge();
                            output.Gauge.DataPoints.Add(value);
                        }
                    }
                    else
                        throw new NotSupportedException($"POC mapping only: {metric.MetricType}");
                }
            }
            // Serialize before returning; metric-point storage belongs to the SDK.
            Bytes = request.ToByteArray();
            ExportCount++;
            return ExportResult.Success;
        }
        catch (Exception exception)
        {
            Error = exception;
            return ExportResult.Failure;
        }
    }

    private static OpenTelemetry.Proto.Metrics.V1.AggregationTemporality Temporality(
        SdkMetric metric
    ) =>
        metric.Temporality == OpenTelemetry.Metrics.AggregationTemporality.Delta
            ? OpenTelemetry.Proto.Metrics.V1.AggregationTemporality.Delta
            : OpenTelemetry.Proto.Metrics.V1.AggregationTemporality.Cumulative;
}

internal static class Wire
{
    internal static readonly string InstanceId = Guid.NewGuid().ToString();

    internal static OtlpResource Resource() =>
        new()
        {
            Attributes =
            {
                Attribute("service.instance.id", InstanceId),
                Attribute("deployment.environment.name", "poc"),
                Attribute("telemetry.distro.name", "apitally-dotnet"),
                Attribute("telemetry.distro.version", "experimental"),
            },
        };

    internal static ulong Nanoseconds(DateTimeOffset time) =>
        checked((ulong)(time.UtcTicks - DateTimeOffset.UnixEpoch.Ticks) * 100);

    internal static KeyValue Attribute(string name, object? value) =>
        new() { Key = name, Value = Value(value) };

    internal static AnyValue Value(object? value) =>
        value switch
        {
            null => new AnyValue(),
            string text => new AnyValue { StringValue = text },
            bool boolean => new AnyValue { BoolValue = boolean },
            int number => new AnyValue { IntValue = number },
            long number => new AnyValue { IntValue = number },
            uint number => new AnyValue { IntValue = number },
            double number => new AnyValue { DoubleValue = number },
            byte[] bytes => new AnyValue { BytesValue = ByteString.CopyFrom(bytes) },
            IReadOnlyDictionary<string, object?> fields => new AnyValue
            {
                KvlistValue = new KeyValueList
                {
                    Values = { fields.Select(field => Attribute(field.Key, field.Value)) },
                },
            },
            object?[] items => new AnyValue
            {
                ArrayValue = new ArrayValue { Values = { items.Select(Value) } },
            },
            _ => throw new NotSupportedException($"POC AnyValue mapping only: {value.GetType()}"),
        };
}
