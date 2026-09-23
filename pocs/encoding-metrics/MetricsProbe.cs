using System.Diagnostics;
using System.Diagnostics.Metrics;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Resources;
using OtlpMetric = OpenTelemetry.Proto.Metrics.V1.Metric;

namespace EncodingMetrics;

internal static class MetricsProbe
{
    internal static ExportMetricsServiceRequest Run()
    {
        var result = DeltaHistograms();
        Reclamation();
        SameNameMeters();
        ScopedMeters();
        FactoryMeters();
        IdleGauges();
        return result;
    }

    private static ExportMetricsServiceRequest DeltaHistograms()
    {
        var owner = new object();
        using var meter = new Meter(new MeterOptions("apitally") { Scope = owner });
        using var pipeline = new Pipeline(owner);
        var duration = meter.CreateHistogram<double>("http.server.request.duration", "s");
        var requestSize = meter.CreateHistogram<long>("http.server.request.body.size", "By");
        var responseSize = meter.CreateHistogram<long>("http.server.response.body.size", "By");
        var tags = Tags("consumer-a");
        duration.Record(0, tags);
        duration.Record(0.125, tags);
        duration.Record(0.5, tags);
        requestSize.Record(0, tags);
        requestSize.Record(50_000, tags);
        responseSize.Record(100_000, tags);
        Program.Check(pipeline.Exporter.ExportCount == 0, "Reader must be non-periodic");
        var first = pipeline.Collect();
        var frozen = first.ToByteArray();
        var firstDuration = Histogram(first, duration.Name).Single();
        Program.Check(
            firstDuration.Count == 3 && firstDuration.Sum == 0.625 && firstDuration.ZeroCount == 1,
            "First delta count/sum/zero"
        );
        Program.Check(
            firstDuration.HasSum
                && firstDuration.HasMin
                && firstDuration.Min == 0
                && firstDuration.HasMax
                && firstDuration.Max == 0.5,
            "Histogram optional fields"
        );
        Program.Check(
            firstDuration.Scale == 3 && firstDuration.Positive.Offset == -25,
            "Scale and bucket offset"
        );
        Program.Check(
            firstDuration.Positive.BucketCounts.Sum(value => (long)value) == 2
                && firstDuration.Positive.BucketCounts.First() == 1
                && firstDuration.Positive.BucketCounts.Last() == 1
                && firstDuration.Negative == null
                && firstDuration.ZeroThreshold == 0,
            "Positive/negative/zero fields"
        );
        var requestPoint = Histogram(first, requestSize.Name).Single();
        var responsePoint = Histogram(first, responseSize.Name).Single();
        Program.Check(
            requestPoint.Count == 2
                && requestPoint.Sum == 50_000
                && requestPoint.ZeroCount == 1
                && responsePoint.Count == 1
                && responsePoint.Sum == 100_000,
            "Exact size counts and sums"
        );
        foreach (var metric in Metrics(first))
        {
            Program.Check(
                metric.ExponentialHistogram.AggregationTemporality
                    == OpenTelemetry.Proto.Metrics.V1.AggregationTemporality.Delta,
                "Delta wire temporality"
            );
            Program.Check(
                metric.Unit == (metric.Name == duration.Name ? "s" : "By"),
                "Histogram units"
            );
            Program.Check(
                metric
                    .ExponentialHistogram.DataPoints.Single()
                    .Attributes.SequenceEqual(firstDuration.Attributes),
                "Size/duration tuple equality"
            );
        }
        duration.Record(0.25, tags);
        requestSize.Record(256, tags);
        responseSize.Record(512, tags);
        var second = pipeline.Collect();
        var secondDuration = Histogram(second, duration.Name).Single();
        Program.Check(
            secondDuration.Count == 1 && secondDuration.Sum == 0.25,
            "Independent second delta"
        );
        Program.Check(
            secondDuration.StartTimeUnixNano == firstDuration.TimeUnixNano
                && secondDuration.TimeUnixNano >= secondDuration.StartTimeUnixNano,
            "Contiguous delta times"
        );
        Program.Check(
            first.ToByteArray().SequenceEqual(frozen),
            "Owned encoded snapshot survives another collect"
        );
        Program.Check(!Metrics(pipeline.Collect()).Any(), "No repeated inactive histogram values");
        Console.WriteLine(
            $"PASS delta: counts=3,1; units=s,By,By; scale={firstDuration.Scale}; offset={firstDuration.Positive.Offset}; snapshots copied before exporter return"
        );

        using var defaults = new Pipeline(owner, maxScale: null);
        duration.Record(0.25, tags);
        var defaultPoint = Histogram(defaults.Collect(), duration.Name).Single();
        var configuration = new Base2ExponentialBucketHistogramConfiguration();
        Program.Check(
            configuration.MaxSize == 160
                && configuration.MaxScale == 20
                && defaultPoint.Scale == 20,
            "Native exponential defaults"
        );
        Console.WriteLine(
            "PASS scale: public MaxScale=3, MaxSize=160; untouched exponential view defaults=20/160; no public minimum-scale clamp used"
        );
        duration.Record(0.000000001, tags);
        duration.Record(31_536_000, tags);
        requestSize.Record(1, tags);
        requestSize.Record(long.MaxValue, tags);
        var wide = pipeline.Collect();
        var scales = Metrics(wide)
            .SelectMany(metric => metric.ExponentialHistogram.DataPoints)
            .Select(point => point.Scale)
            .ToArray();
        Program.Check(
            scales.Length == 2 && scales.All(scale => scale is >= -2 and <= 3),
            "Wide duration and byte-size ranges stay in ingestion range"
        );
        Console.WriteLine(
            $"PASS adaptive scale: 1ns..1year duration and 1..Int64.MaxValue bytes produce scales={string.Join(',', scales)}"
        );
        return first;
    }

    private static void Reclamation()
    {
        var owner = new object();
        using var meter = new Meter(new MeterOptions("apitally") { Scope = owner });
        using var pipeline = new Pipeline(owner, cardinality: 2);
        var histogram = meter.CreateHistogram<double>("http.server.request.duration", "s");
        foreach (var consumer in new[] { "a", "b", "c" })
            histogram.Record(1, Tags(consumer));
        var first = Histogram(pipeline.Collect(), histogram.Name).ToArray();
        Program.Check(
            first.Length == 3 && first.Sum(point => (long)point.Count) == 3,
            "Two dimensions plus separate overflow point"
        );
        var overflow = first.Single(point =>
            point.Attributes.Any(tag => tag.Key == "otel.metric.overflow" && tag.Value.BoolValue)
        );
        Program.Check(
            overflow.Attributes.Count == 1 && overflow.Count == 1,
            "Overflow loses endpoint/consumer tuple"
        );
        histogram.Record(2, Tags("a"));
        histogram.Record(3, Tags("d"));
        var late = Histogram(pipeline.Collect(), histogram.Name).ToArray();
        Program.Check(
            late.Length == 2 && late.Sum(point => (long)point.Count) == 2,
            "Late active point and overflow before reclamation"
        );
        Program.Check(
            late.Single(point =>
                point.Attributes.Any(tag => tag.Key == "apitally.consumer.identifier")
            ).Sum == 2,
            "Late observation retained"
        );
        Program.Check(!Metrics(pipeline.Collect()).Any(), "Inactive dimensions collection empty");
        histogram.Record(4, Tags("e"));
        histogram.Record(5, Tags("f"));
        var reclaimed = Histogram(pipeline.Collect(), histogram.Name).ToArray();
        Program.Check(
            reclaimed.Length == 2
                && reclaimed.All(point => point.Attributes.Any(tag => tag.Key == "http.route")),
            "Capacity reused by new dimensions"
        );
        Program.Check(
            reclaimed.Sum(point => point.Sum) == 9,
            "Reclaimed dimensions contain only new observations"
        );
        Console.WriteLine(
            "PASS cardinality probe=2: a,b retained; c overflow; late a retained/d overflow; idle collection frees capacity for e,f. Overflow is invalid for ingestion, not a product limit"
        );
    }

    private static void SameNameMeters()
    {
        using var first = new Meter("apitally");
        using var second = new Meter("apitally");
        using var privateA = new Pipeline();
        using var privateB = new Pipeline();
        using var user = new Pipeline(temporality: MetricReaderTemporalityPreference.Cumulative);
        var a = first.CreateCounter<long>("host.probe");
        var b = second.CreateCounter<long>("host.probe");
        a.Add(1);
        b.Add(10);
        var values = new[]
        {
            Total(privateA.Collect()),
            Total(privateB.Collect()),
            Total(user.Collect()),
        };
        Program.Check(
            values.SequenceEqual(new long[] { 11, 11, 11 }),
            "Name subscription is process-wide"
        );
        Console.WriteLine(
            "PASS negative isolation: two private providers and name-subscribed user provider each observe 11 from distinct same-name meters (1+10)"
        );
    }

    private static void ScopedMeters()
    {
        var ownerA = new object();
        var ownerB = new object();
        using var meterA = new Meter(new MeterOptions("apitally") { Scope = ownerA });
        using var meterB = new Meter(new MeterOptions("apitally") { Scope = ownerB });
        using var privateA = new Pipeline(ownerA);
        using var privateB = new Pipeline(ownerB);
        using var user = new Pipeline(temporality: MetricReaderTemporalityPreference.Cumulative);
        meterA.CreateCounter<long>("host.probe").Add(1);
        meterB.CreateCounter<long>("host.probe").Add(10);
        Program.Check(
            Total(privateA.Collect()) == 1
                && Total(privateB.Collect()) == 10
                && Total(user.Collect()) == 11,
            "Public Scope+Drop view isolates only scoped private providers"
        );
        Console.WriteLine(
            "PASS explicit MeterOptions.Scope + AddView(Drop foreign scope): private=1,10; user AddMeter remains process-wide=11"
        );
    }

    private static void FactoryMeters()
    {
        using var servicesA = new ServiceCollection().AddMetrics().BuildServiceProvider();
        using var servicesB = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var factoryA = servicesA.GetRequiredService<IMeterFactory>();
        var factoryB = servicesB.GetRequiredService<IMeterFactory>();
        var meterA = factoryA.Create(new MeterOptions("apitally"));
        var meterB = factoryB.Create(new MeterOptions("apitally"));
        Program.Check(
            ReferenceEquals(meterA.Scope, factoryA) && ReferenceEquals(meterB.Scope, factoryB),
            "Factory is the public host scope"
        );
        using var privateA = new Pipeline(factoryA);
        using var privateB = new Pipeline(factoryB);
        using var user = new Pipeline(temporality: MetricReaderTemporalityPreference.Cumulative);
        var a = meterA.CreateCounter<long>("host.probe");
        var b = meterB.CreateCounter<long>("host.probe");
        a.Add(1);
        b.Add(10);
        Program.Check(
            Total(privateA.Collect()) == 1
                && Total(privateB.Collect()) == 10
                && Total(user.Collect()) == 11,
            "Factory requires explicit view filtering for OTel host isolation"
        );
        privateA.Dispose();
        servicesA.Dispose();
        b.Add(20);
        var userAfterDisposal = user.Collect();
        Program.Check(
            Total(privateB.Collect()) == 20 && Total(userAfterDisposal) == 31,
            "Disposing host A leaves host B/user functional"
        );
        Program.Check(
            Metrics(userAfterDisposal)
                .All(metric =>
                    metric.Sum.AggregationTemporality
                    == OpenTelemetry.Proto.Metrics.V1.AggregationTemporality.Cumulative
                ),
            "User-selected temporality unchanged"
        );
        Console.WriteLine(
            "PASS IMeterFactory scope filter: private=1,10; cumulative user=11; after A disposal B delta=20/user cumulative=31. Factories alone do not restrict name subscribers"
        );
    }

    private static void IdleGauges()
    {
        var owner = new object();
        using var meter = new Meter(new MeterOptions("apitally") { Scope = owner });
        using var pipeline = new Pipeline(owner);
        using var process = Process.GetCurrentProcess();
        var elapsed = Stopwatch.StartNew();
        var processStart = process.StartTime.ToUniversalTime();
        double cpu = 0;
        long memory = 0;
        bool enabled = true;
        var previousCpu = process.TotalProcessorTime;
        var previousTime = elapsed.Elapsed;
        meter.CreateObservableGauge(
            "process.cpu.utilization",
            () => enabled ? new[] { new Measurement<double>(cpu) } : [],
            "1"
        );
        meter.CreateObservableGauge(
            "process.memory.usage",
            () => enabled ? new[] { new Measurement<long>(memory) } : [],
            "By"
        );
        meter.CreateObservableGauge(
            "process.uptime",
            () => (DateTime.UtcNow - processStart).TotalSeconds,
            "s"
        );
        for (var interval = 0; interval < 2; interval++)
        {
            Thread.SpinWait(100_000);
            process.Refresh();
            var now = elapsed.Elapsed;
            var totalCpu = process.TotalProcessorTime;
            cpu = Math.Clamp(
                (totalCpu - previousCpu).TotalSeconds
                    / (now - previousTime).TotalSeconds
                    / Environment.ProcessorCount,
                0,
                1
            );
            memory = process.WorkingSet64;
            previousCpu = totalCpu;
            previousTime = now;
            var collection = pipeline.Collect();
            var points = Metrics(collection)
                .ToDictionary(metric => metric.Name, metric => metric.Gauge.DataPoints.Single());
            Program.Check(
                points.Count == 3
                    && points["process.memory.usage"].AsInt > 0
                    && points["process.cpu.utilization"].AsDouble is >= 0 and <= 1,
                "Idle process values"
            );
            var skew = Math.Abs(
                (long)points["process.cpu.utilization"].TimeUnixNano
                    - (long)points["process.memory.usage"].TimeUnixNano
            );
            Program.Check(skew <= 1_000_000_000, "CPU/memory paired timestamps");
            Console.WriteLine(
                $"PASS idle gauges cycle={interval + 1}: cpu/memory timestamp skew={skew}ns, uptime present"
            );
        }
        enabled = false;
        Program.Check(
            Metrics(pipeline.Collect())
                .Select(metric => metric.Name)
                .SequenceEqual(new[] { "process.uptime" }),
            "Uptime alone keeps idle collection nonempty"
        );
        Console.WriteLine("PASS idle gauges with CPU/memory disabled: uptime only");
    }

    internal static IEnumerable<OtlpMetric> Metrics(ExportMetricsServiceRequest request) =>
        request
            .ResourceMetrics.SelectMany(resource => resource.ScopeMetrics)
            .SelectMany(scope => scope.Metrics)
            .Where(metric =>
                metric.ExponentialHistogram?.DataPoints.Count > 0
                || metric.Sum?.DataPoints.Count > 0
                || metric.Gauge?.DataPoints.Count > 0
            );

    private static IEnumerable<ExponentialHistogramDataPoint> Histogram(
        ExportMetricsServiceRequest request,
        string name
    ) =>
        Metrics(request)
            .Where(metric => metric.Name == name)
            .SelectMany(metric => metric.ExponentialHistogram.DataPoints);

    private static long Total(ExportMetricsServiceRequest request) =>
        Metrics(request).SelectMany(metric => metric.Sum.DataPoints).Sum(point => point.AsInt);

    private static TagList Tags(string consumer) =>
        new()
        {
            { "http.request.method", "POST" },
            { "http.route", "/orders/{id}" },
            { "http.response.status_code", 500 },
            { "apitally.consumer.identifier", consumer },
            { "url.scheme", "http" },
            { "error.type", "500" },
        };

    private sealed class Pipeline : IDisposable
    {
        internal MetricEncoding Exporter { get; } = new();
        private readonly BaseExportingMetricReader reader;
        private readonly MeterProvider provider;
        private bool disposed;

        internal Pipeline(
            object? scope = null,
            int? cardinality = null,
            int? maxScale = 3,
            MetricReaderTemporalityPreference temporality = MetricReaderTemporalityPreference.Delta
        )
        {
            reader = new BaseExportingMetricReader(Exporter)
            {
                TemporalityPreference = temporality,
            };
            provider = Sdk.CreateMeterProviderBuilder()
                .SetResourceBuilder(
                    ResourceBuilder
                        .CreateEmpty()
                        .AddAttributes(
                            new Dictionary<string, object>
                            {
                                ["service.instance.id"] = Wire.InstanceId,
                                ["deployment.environment.name"] = "poc",
                                ["telemetry.distro.name"] = "apitally-dotnet",
                                ["telemetry.distro.version"] = "experimental",
                            }
                        )
                )
                .AddMeter("apitally")
                .AddView(instrument =>
                {
                    if (scope != null && !ReferenceEquals(instrument.Meter.Scope, scope))
                        return MetricStreamConfiguration.Drop;
                    if (instrument is Histogram<double> or Histogram<long>)
                    {
                        var configuration = new Base2ExponentialBucketHistogramConfiguration
                        {
                            CardinalityLimit = cardinality,
                        };
                        if (maxScale.HasValue)
                            configuration.MaxScale = maxScale.Value;
                        return configuration;
                    }
                    return null;
                })
                .SetExemplarFilter(ExemplarFilterType.AlwaysOff)
                .AddReader(reader)
                .Build();
        }

        internal ExportMetricsServiceRequest Collect()
        {
            using var suppression = SuppressInstrumentationScope.Begin();
            Program.Check(reader.Collect(5_000), $"Manual collect failed: {Exporter.Error}");
            return ExportMetricsServiceRequest.Parser.ParseFrom(Exporter.Bytes);
        }

        public void Dispose()
        {
            if (!disposed)
            {
                provider.Dispose();
                disposed = true;
            }
        }
    }
}
