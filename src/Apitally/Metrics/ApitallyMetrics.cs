using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using Apitally.Export;
using Apitally.Logging;
using Google.Protobuf;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using SdkMetric = OpenTelemetry.Metrics.Metric;

namespace Apitally.Metrics;

// A private meter and provider. The provider only accepts instruments from this meter
// instance, so other meters named "apitally" never reach Apitally.
internal sealed class ApitallyMetrics : IDisposable
{
    public const string MeterName = "apitally";

    // Fixed per-histogram capacity; delta collection reclaims inactive attribute sets.
    private const int CardinalityLimit = 10_000;

    private readonly object sync = new();
    private readonly Meter meter;
    private readonly MeterProvider provider;
    private readonly BaseExportingMetricReader reader;
    private readonly Histogram<double> requestDuration;
    private readonly Histogram<long> requestBodySize;
    private readonly Histogram<long> responseBodySize;

    public ApitallyMetrics(
        Resource resource,
        TelemetrySpool spool,
        TimeProvider timeProvider,
        SdkDiagnostics diagnostics
    )
    {
        var scope = new object();
        meter = new Meter(new MeterOptions(MeterName) { Scope = scope });
        reader = new SynchronizedMetricReader(
            new SpoolExporter(resource, spool, diagnostics, sync),
            sync
        )
        {
            TemporalityPreference = MetricReaderTemporalityPreference.Delta,
        };
        provider = Sdk.CreateMeterProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateEmpty().AddAttributes(resource.Attributes))
            .AddMeter(MeterName)
            .AddView(instrument =>
                !ReferenceEquals(instrument.Meter.Scope, scope) ? MetricStreamConfiguration.Drop
                : instrument is Histogram<double> or Histogram<long>
                    ? new Base2ExponentialBucketHistogramConfiguration
                    {
                        MaxScale = 3,
                        CardinalityLimit = CardinalityLimit,
                    }
                : null
            )
            .SetExemplarFilter(ExemplarFilterType.AlwaysOff)
            .AddReader(reader)
            .Build();
        requestDuration = meter.CreateHistogram<double>(
            "http.server.request.duration",
            "s",
            "Duration of HTTP server requests"
        );
        requestBodySize = meter.CreateHistogram<long>(
            "http.server.request.body.size",
            "By",
            "Size of HTTP server request bodies"
        );
        responseBodySize = meter.CreateHistogram<long>(
            "http.server.response.body.size",
            "By",
            "Size of HTTP server response bodies"
        );
        _ = new ProcessMetrics(meter, timeProvider);
    }

    // Duration is the count anchor; sizes use the identical attribute tuple so they join to it.
    public void RecordRequest(
        string method,
        string route,
        int statusCode,
        string? consumerIdentifier,
        string scheme,
        TimeSpan duration,
        long? requestSize,
        long? responseSize
    )
    {
        var tags = new TagList
        {
            { "http.request.method", method },
            { "http.route", route },
            { "http.response.status_code", statusCode },
            { "url.scheme", scheme },
        };
        if (consumerIdentifier is not null)
            tags.Add("apitally.consumer.identifier", consumerIdentifier);
        if (statusCode >= 500)
            tags.Add("error.type", statusCode.ToString(CultureInfo.InvariantCulture));
        lock (sync)
        {
            requestDuration.Record(duration.TotalSeconds, tags);
            if (requestSize is { } requestBytes)
                requestBodySize.Record(requestBytes, tags);
            if (responseSize is { } responseBytes)
                responseBodySize.Record(responseBytes, tags);
        }
    }

    public bool Collect(int timeoutMilliseconds) => reader.Collect(timeoutMilliseconds);

    // Performs the final collection; the reader rejects any later collection.
    public void Shutdown() => reader.Shutdown(Timeout.Infinite);

    public void Dispose()
    {
        provider.Dispose();
        meter.Dispose();
    }

    private sealed class SynchronizedMetricReader(BaseExporter<SdkMetric> exporter, object sync)
        : BaseExportingMetricReader(exporter)
    {
        // Shutdown and provider disposal also collect through this override. The exporter
        // releases the lock once the snapshot is taken.
        protected override bool OnCollect(int timeoutMilliseconds)
        {
            Monitor.Enter(sync);
            try
            {
                return base.OnCollect(timeoutMilliseconds);
            }
            finally
            {
                if (Monitor.IsEntered(sync))
                    Monitor.Exit(sync);
            }
        }
    }

    private sealed class SpoolExporter(
        Resource resource,
        TelemetrySpool spool,
        SdkDiagnostics diagnostics,
        object sync
    ) : BaseExporter<SdkMetric>
    {
        public override ExportResult Export(in Batch<SdkMetric> batch)
        {
            // The reader calls this synchronously after the snapshot, so recording can resume
            // while the batch is mapped and spooled.
            if (Monitor.IsEntered(sync))
                Monitor.Exit(sync);
            try
            {
                var metrics = new List<SdkMetric>();
                foreach (var metric in batch)
                    metrics.Add(metric);
                var mapped = OtlpMetricMapper.Map(metrics, out var hasOverflow);
                if (hasOverflow)
                    diagnostics.MetricCapacityExceeded();
                // The server joins the three request histograms within one request, so each
                // collection is appended whole. The 10,000-point capacity bounds its size.
                spool.Append(
                    TelemetrySignal.Metrics,
                    OtlpMetricMapper.BuildRequest(mapped, resource).ToByteArray()
                );
                return ExportResult.Success;
            }
            catch (Exception exception)
            {
                diagnostics.MetricExportFailed(exception);
                return ExportResult.Failure;
            }
        }
    }
}
