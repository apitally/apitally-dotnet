using Apitally.Export;
using Apitally.Logging;
using Google.Protobuf;
using OpenTelemetry.Resources;

namespace Apitally.Metrics;

internal sealed class ApitallyMetrics(
    Resource resource,
    TelemetrySpool spool,
    TimeProvider timeProvider,
    SdkDiagnostics diagnostics
)
{
    // Bounds memory when most requests form a new combination, for example when a request ID
    // is used as the consumer identifier.
    private const int MaxCombinations = 50_000;

    // At up to about 1.4 KB per combination, requests stay well below OtlpEncoder.MaxRequestSize.
    private const int CombinationsPerRequest = 1_000;

    private readonly object sync = new();
    private readonly ProcessMetrics processMetrics = new(timeProvider);
    private Dictionary<RequestMetricKey, RequestMetricValues> requests = [];
    private DateTime intervalStart = timeProvider.GetUtcNow().UtcDateTime;

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
        var key = new RequestMetricKey(method, route, statusCode, scheme, consumerIdentifier);
        lock (sync)
        {
            if (!requests.TryGetValue(key, out var values))
            {
                if (requests.Count >= MaxCombinations)
                    return;
                values = new RequestMetricValues();
                requests.Add(key, values);
            }
            values.Duration.Record(duration.TotalSeconds);
            if (requestSize is { } requestBytes)
                values.RequestBodySize.Record(requestBytes);
            if (responseSize is { } responseBytes)
                values.ResponseBodySize.Record(responseBytes);
        }
    }

    // Recording is blocked only while the interval's combinations are swapped out.
    public void Collect()
    {
        Dictionary<RequestMetricKey, RequestMetricValues> collected;
        DateTime start;
        DateTime end;
        lock (sync)
        {
            collected = requests;
            requests = [];
            start = intervalStart;
            end = intervalStart = timeProvider.GetUtcNow().UtcDateTime;
        }
        if (collected.Count >= MaxCombinations)
            diagnostics.MetricCapacityExceeded();
        try
        {
            var gauges = OtlpMetricMapper.BuildProcessRequest(
                processMetrics.Observe(),
                start,
                end,
                resource
            );
            spool.Append(TelemetrySignal.Metrics, gauges.CalculateSize(), gauges.WriteTo);
            OtlpEncoder.EncodeRequests(
                [.. collected],
                chunk => OtlpMetricMapper.BuildRequestMetricsRequest(chunk, start, end, resource),
                (request, size) => spool.Append(TelemetrySignal.Metrics, size, request.WriteTo),
                "metrics",
                diagnostics,
                CombinationsPerRequest
            );
        }
        catch (Exception exception)
        {
            diagnostics.MetricExportFailed(exception);
        }
    }
}

internal readonly record struct RequestMetricKey(
    string Method,
    string Route,
    int StatusCode,
    string Scheme,
    string? ConsumerIdentifier
);

internal sealed class RequestMetricValues
{
    public ExponentialHistogram Duration;
    public ExponentialHistogram RequestBodySize;
    public ExponentialHistogram ResponseBodySize;
}
