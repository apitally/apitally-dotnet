using System.Diagnostics.Metrics;
using System.Net.Http.Json;
using Apitally.Export;
using Apitally.Metrics;
using Apitally.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Proto.Metrics.V1;
using OtlpMetric = OpenTelemetry.Proto.Metrics.V1.Metric;

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
            {
                Assert.Equal(
                    AggregationTemporality.Delta,
                    metric.ExponentialHistogram.AggregationTemporality
                );
                Assert.All(
                    metric.ExponentialHistogram.DataPoints,
                    point => Assert.InRange(point.Scale, -2, 3)
                );
            }
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
    public void ForeignMetersWithTheSameNameAreNotExported()
    {
        using var foreign = new Meter(ApitallyMetrics.MeterName);
        foreign.CreateCounter<long>("foreign.counter").Add(1);

        metrics.RecordRequest("GET", "/a", 200, null, "http", TimeSpan.FromSeconds(1), null, null);
        var exported = Collect();

        Assert.DoesNotContain(exported, metric => metric.Name == "foreign.counter");
        Assert.Contains(exported, metric => metric.Name == "http.server.request.duration");
    }

    [Fact]
    public void OverflowPointsAreOmittedWithOneWarning()
    {
        for (var i = 0; i < 10_005; i++)
            metrics.RecordRequest(
                "GET",
                "/a",
                200,
                $"consumer-{i}",
                "http",
                TimeSpan.FromSeconds(1),
                null,
                null
            );

        var duration = Collect().Single(metric => metric.Name == "http.server.request.duration");

        Assert.Equal(10_000, duration.ExponentialHistogram.DataPoints.Count);
        Assert.DoesNotContain(
            duration.ExponentialHistogram.DataPoints,
            point => OtlpDecoding.Attributes(point.Attributes).ContainsKey("otel.metric.overflow")
        );
        Assert.Single(diagnostics.Records(LogLevel.Warning));
    }

    [Fact]
    public void InactiveAttributeSetsAreReclaimedAfterAnIdleCollection()
    {
        for (var i = 0; i < 9_000; i++)
            metrics.RecordRequest(
                "GET",
                "/a",
                200,
                $"first-{i}",
                "http",
                TimeSpan.FromSeconds(1),
                null,
                null
            );
        Collect();
        Collect();
        for (var i = 0; i < 9_000; i++)
            metrics.RecordRequest(
                "GET",
                "/a",
                200,
                $"second-{i}",
                "http",
                TimeSpan.FromSeconds(1),
                null,
                null
            );

        var duration = Collect().Single(metric => metric.Name == "http.server.request.duration");

        Assert.Equal(9_000, duration.ExponentialHistogram.DataPoints.Count);
        Assert.Empty(diagnostics.Records(LogLevel.Warning));
    }

    public void Dispose()
    {
        metrics.Dispose();
        spool.Dispose();
        diagnostics.Dispose();
        Directory.Delete(directory, recursive: true);
    }

    private List<OtlpMetric> Collect()
    {
        foreach (var file in spool.GetPendingFiles())
            spool.Delete(file);
        Assert.True(metrics.Collect(10_000));
        spool.CloseCurrentFiles();
        return spool
            .GetPendingFiles()
            .SelectMany(file => OtlpDecoding.Metrics(file.ReadStoredBytes()).ResourceMetrics)
            .SelectMany(resource => resource.ScopeMetrics)
            .SelectMany(scope => scope.Metrics)
            .ToList();
    }
}
