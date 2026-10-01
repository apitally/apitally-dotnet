using System.Net.Http.Json;
using Apitally.Export;
using Apitally.Metrics;
using Apitally.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Proto.Metrics.V1;

namespace Apitally.Tests.Metrics;

public sealed class ApitallyMetricsTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("apitally-tests").FullName;
    private readonly DiagnosticsCollector diagnostics = new();
    private readonly TelemetrySpool spool;
    private readonly ApitallyMetrics metrics;

    public ApitallyMetricsTests()
    {
        spool = new(diagnostics.Diagnostics, TimeProvider.System, directory);
        metrics = new(TestSpans.Resource, spool, TimeProvider.System, diagnostics.Diagnostics);
    }

    [Fact]
    public async Task RequestsAreRecordedAsDeltaExponentialHistogramsWithSharedAttributes()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        await using var host = await ApplicationHost.StartMinimalAsync(receiver);

        await host.Client.PostAsJsonAsync("/items", new { id = 1, name = "a" });
        await host.Client.GetAsync("/error");
        await host.Client.GetAsync("/consumers/acme");
        await host.StopAsync();

        var duration = Assert.Single(receiver.Metrics("http.server.request.duration"));
        var requestSize = Assert.Single(receiver.Metrics("http.server.request.body.size"));
        var responseSize = Assert.Single(receiver.Metrics("http.server.response.body.size"));
        Assert.Equal("s", duration.Unit);
        Assert.Equal("By", requestSize.Unit);
        Assert.All(
            new[] { duration, requestSize, responseSize },
            metric =>
                Assert.Equal(
                    AggregationTemporality.Delta,
                    metric.ExponentialHistogram.AggregationTemporality
                )
        );
        var points = duration.ExponentialHistogram.DataPoints.ToDictionary(point =>
            (string)OtlpDecoding.Attributes(point.Attributes)["http.route"]!
        );
        Assert.Equal(3, points.Count);
        Assert.All(points.Values, point => Assert.Equal(1ul, point.Count));
        Assert.Equal(
            new Dictionary<string, object?>
            {
                ["http.request.method"] = "GET",
                ["http.route"] = "/error",
                ["http.response.status_code"] = 500L,
                ["url.scheme"] = "http",
                ["error.type"] = "500",
            },
            OtlpDecoding.Attributes(points["/error"].Attributes)
        );
        Assert.Equal(
            "acme",
            OtlpDecoding.Attributes(points["/consumers/{identifier}"].Attributes)[
                "apitally.consumer.identifier"
            ]
        );
        var postAttributes = OtlpDecoding.Attributes(points["/items"].Attributes);
        Assert.Equal(
            postAttributes,
            OtlpDecoding.Attributes(
                requestSize.ExponentialHistogram.DataPoints.Single(p => p.Sum > 0).Attributes
            )
        );
        Assert.Equal(3, responseSize.ExponentialHistogram.DataPoints.Count);
    }

    [Fact]
    public async Task OptionsAndUnmatchedRequestsAreNotRecordedButExcludedAndSampledOutAre()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        await using var host = await ApplicationHost.StartMinimalAsync(
            receiver,
            builder =>
                builder.Services.AddApitally(options =>
                {
                    options.SampleRate = 0;
                    options.ExcludePaths = ["^/hello$"];
                })
        );

        await host.Client.GetAsync("/hello");
        await host.Client.GetAsync("/items/1");
        await host.Client.GetAsync("/missing");
        await host.Client.SendAsync(new HttpRequestMessage(HttpMethod.Options, "/items/1"));
        await host.StopAsync();

        var routes = receiver
            .Metrics("http.server.request.duration")
            .SelectMany(metric => metric.ExponentialHistogram.DataPoints)
            .Select(point => OtlpDecoding.Attributes(point.Attributes)["http.route"])
            .Order();
        Assert.Equal(["/hello", "/items/{id:int}"], routes);
        Assert.Empty(receiver.Spans());
    }

    [Fact]
    public void HistogramPointsCoverTheIntervalSinceThePreviousCollection()
    {
        var start = Collect().Single()[0].Gauge.DataPoints[0].TimeUnixNano;
        Record(4, 4);
        Record(0.5, 1024);
        Record(4, 0);

        var requests = Collect();

        var end = requests[0][0].Gauge.DataPoints[0].TimeUnixNano;
        Assert.Equal(
            ["http.server.request.duration", "http.server.response.body.size"],
            requests[1].Select(metric => metric.Name)
        );
        Assert.Equal(
            Point(
                start,
                end,
                count: 3,
                sum: 8.5,
                min: 0.5,
                max: 4,
                zeroCount: 0,
                offset: -9,
                bucketCounts: [1, .. new ulong[23], 2]
            ),
            requests[1][0].ExponentialHistogram.DataPoints.Single()
        );
        Assert.Equal(
            Point(
                start,
                end,
                count: 3,
                sum: 1028,
                min: 0,
                max: 1024,
                zeroCount: 1,
                offset: 15,
                bucketCounts: [1, .. new ulong[63], 1]
            ),
            requests[1][1].ExponentialHistogram.DataPoints.Single()
        );
    }

    [Fact]
    public void CombinationsBeyondCapacityAreDroppedUntilTheNextCollection()
    {
        RecordConsumers("first", 50_005);
        var first = DurationPoints(Collect());
        RecordConsumers("second", 1);

        var second = Assert.Single(DurationPoints(Collect()));

        Assert.Equal(50_000, first.Count);
        Assert.Equal(
            "second-0",
            OtlpDecoding.Attributes(second.Attributes)["apitally.consumer.identifier"]
        );
        Assert.Single(diagnostics.Records(LogLevel.Warning));
    }

    [Fact]
    public void CombinationsAreSplitIntoRequestsWithAllTheirHistograms()
    {
        RecordConsumers("consumer", 2_500);

        var requests = Collect();

        Assert.Equal(4, requests.Count);
        Assert.All(requests[0], metric => Assert.NotNull(metric.Gauge));
        var histogramRequests = requests.Skip(1).ToList();
        Assert.Equal(
            [1_000, 1_000, 500],
            histogramRequests.Select(request => request[0].ExponentialHistogram.DataPoints.Count)
        );
        Assert.All(
            histogramRequests,
            request =>
            {
                Assert.Equal(
                    [
                        "http.server.request.duration",
                        "http.server.request.body.size",
                        "http.server.response.body.size",
                    ],
                    request.Select(metric => metric.Name)
                );
                var consumers = request.Select(metric =>
                    metric.ExponentialHistogram.DataPoints.Select(point =>
                        OtlpDecoding.Attributes(point.Attributes)["apitally.consumer.identifier"]
                    )
                );
                Assert.All(consumers, ids => Assert.Equal(consumers.First(), ids));
            }
        );
    }

    public void Dispose()
    {
        spool.Dispose();
        diagnostics.Dispose();
        Directory.Delete(directory, recursive: true);
    }

    private void Record(double durationSeconds, long responseSize) =>
        metrics.RecordRequest(
            "GET",
            "/a",
            200,
            null,
            "http",
            TimeSpan.FromSeconds(durationSeconds),
            null,
            responseSize
        );

    private void RecordConsumers(string prefix, int count)
    {
        for (var i = 0; i < count; i++)
            metrics.RecordRequest(
                "GET",
                "/a",
                200,
                $"{prefix}-{i}",
                "http",
                TimeSpan.FromSeconds(1),
                10,
                20
            );
    }

    private static ExponentialHistogramDataPoint Point(
        ulong start,
        ulong end,
        ulong count,
        double sum,
        double min,
        double max,
        ulong zeroCount,
        int offset,
        ulong[] bucketCounts
    ) =>
        new()
        {
            StartTimeUnixNano = start,
            TimeUnixNano = end,
            Count = count,
            Sum = sum,
            Scale = 3,
            ZeroCount = zeroCount,
            Min = min,
            Max = max,
            Positive = new() { Offset = offset, BucketCounts = { bucketCounts } },
            Attributes =
            {
                OtlpEncoder.ToKeyValue("http.request.method", "GET"),
                OtlpEncoder.ToKeyValue("http.route", "/a"),
                OtlpEncoder.ToKeyValue("http.response.status_code", 200L),
                OtlpEncoder.ToKeyValue("url.scheme", "http"),
            },
        };

    private static List<ExponentialHistogramDataPoint> DurationPoints(
        List<List<Metric>> requests
    ) =>
        requests
            .SelectMany(request => request)
            .Where(metric => metric.Name == "http.server.request.duration")
            .SelectMany(metric => metric.ExponentialHistogram.DataPoints)
            .ToList();

    // Returns the metrics of each appended request; a spool file holds concatenated requests.
    private List<List<Metric>> Collect()
    {
        foreach (var file in spool.GetPendingFiles())
            spool.Delete(file);
        metrics.Collect();
        spool.CloseCurrentFiles();
        return spool
            .GetPendingFiles()
            .SelectMany(file => OtlpDecoding.Metrics(file.ReadStoredBytes()).ResourceMetrics)
            .Select(resource => resource.ScopeMetrics.SelectMany(scope => scope.Metrics).ToList())
            .ToList();
    }
}
