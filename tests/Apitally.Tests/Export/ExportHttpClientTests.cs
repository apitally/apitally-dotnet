using System.Net;
using Apitally.Export;
using Apitally.Tests.Support;

namespace Apitally.Tests.Export;

public class ExportHttpClientTests
{
    [Fact]
    public async Task PostsStoredBytesWithRequiredHeaders()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        using var client = new ExportHttpClient(
            receiver.Endpoint,
            TestConfiguration.WriteToken,
            "prod",
            null
        );
        var body = new byte[] { 1, 2, 3 };

        var response = await client.PostAsync(
            TelemetrySignal.Metrics,
            body,
            CancellationToken.None
        );

        Assert.Equal(ExportOutcome.Accepted, response.Outcome);
        var export = Assert.Single(receiver.Exports);
        Assert.Equal("/v1/metrics", export.Path);
        Assert.Equal("application/x-protobuf", export.ContentType);
        Assert.Equal("gzip", export.ContentEncoding);
        Assert.Equal("Bearer " + TestConfiguration.WriteToken, export.Authorization);
        Assert.Equal("prod", export.Env);
        Assert.Equal(body, export.Body);
    }

    [Theory]
    [InlineData(200, "Accepted")]
    [InlineData(408, "Retryable")]
    [InlineData(429, "Retryable")]
    [InlineData(503, "Retryable")]
    [InlineData(401, "Rejected")]
    [InlineData(402, "Rejected")]
    public async Task ClassifiesResponsesAndReadsExportInterval(int status, string outcomeName)
    {
        var outcome = Enum.Parse<ExportOutcome>(outcomeName);
        await using var receiver = await OtlpReceiver.StartAsync();
        receiver.Respond = _ => (status, "30");
        using var client = new ExportHttpClient(
            receiver.Endpoint,
            TestConfiguration.WriteToken,
            "dev",
            null
        );

        var response = await client.PostAsync(TelemetrySignal.Traces, [1], CancellationToken.None);

        Assert.Equal(new ExportResponse(outcome, status, 30), response);
    }

    [Fact]
    public async Task RetriesOnceImmediatelyAfterAConnectionError()
    {
        using var server = new LoopbackHttpServer(connection => connection > 1);
        using var client = new ExportHttpClient(
            server.Uri,
            TestConfiguration.WriteToken,
            "dev",
            null
        );

        var response = await client.PostAsync(TelemetrySignal.Logs, [1], CancellationToken.None);

        Assert.Equal(ExportOutcome.Accepted, response.Outcome);
        Assert.Equal(2, server.Connections);
    }

    [Fact]
    public async Task RepeatedConnectionErrorsAreRetryable()
    {
        using var server = new LoopbackHttpServer(_ => false);
        using var client = new ExportHttpClient(
            server.Uri,
            TestConfiguration.WriteToken,
            "dev",
            null
        );

        var response = await client.PostAsync(TelemetrySignal.Logs, [1], CancellationToken.None);

        Assert.Equal(ExportOutcome.Retryable, response.Outcome);
        Assert.Equal(2, server.Connections);
    }

    [Fact]
    public async Task SendsThroughTheConfiguredProxy()
    {
        using var proxy = new LoopbackHttpServer(_ => true);
        using var client = new ExportHttpClient(
            new Uri("http://otlp.invalid"),
            TestConfiguration.WriteToken,
            "dev",
            new WebProxy(proxy.Uri)
        );

        var response = await client.PostAsync(TelemetrySignal.Traces, [1], CancellationToken.None);

        Assert.Equal(ExportOutcome.Accepted, response.Outcome);
        Assert.Equal(
            "POST http://otlp.invalid/v1/traces HTTP/1.1",
            Assert.Single(proxy.RequestLines)
        );
    }
}
