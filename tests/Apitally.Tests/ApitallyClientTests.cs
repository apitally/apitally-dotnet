namespace Apitally.Tests;

using System.Net;
using Apitally;
using Apitally.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using Xunit;

public class ApitallyClientTests : IDisposable
{
    private readonly ApitallyClient _client;
    private readonly Mock<HttpMessageHandler> _mockHttpHandler;
    private readonly List<HttpRequestMessage> _requests;

    public ApitallyClientTests()
    {
        _requests = new List<HttpRequestMessage>();
        _mockHttpHandler = new Mock<HttpMessageHandler>();
        _mockHttpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK))
            .Callback<HttpRequestMessage, CancellationToken>(
                (request, _) =>
                {
                    _requests.Add(request);
                }
            );

        var options = new OptionsWrapper<ApitallyOptions>(
            new ApitallyOptions
            {
                ClientId = "00000000-0000-0000-0000-000000000000",
                Env = "test",
                RequestLogging = new RequestLoggingOptions { Enabled = true },
            }
        );
        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        var requestLogger = new RequestLogger(options, loggerFactory.CreateLogger<RequestLogger>());
        var httpClient = new HttpClient(_mockHttpHandler.Object)
        {
            BaseAddress = new Uri("https://hub.apitally.io"),
        };
        _client = new ApitallyClient(
            options,
            requestLogger,
            loggerFactory.CreateLogger<ApitallyClient>(),
            httpClient
        );
    }

    public void Dispose()
    {
        _client.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task BackgroundService_ShouldSyncWithHub()
    {
        // Arrange
        var requestHeaders = new[]
        {
            new Header("Content-Type", "application/json"),
            new Header("User-Agent", "test-client"),
        };
        var responseHeaders = new[] { new Header("Content-Type", "application/json") };
        var request = new Request
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Consumer = "tester",
            Method = "GET",
            Path = "/items",
            Url = "http://test/items",
            Headers = requestHeaders,
            Size = 0,
            Body = Array.Empty<byte>(),
        };
        var response = new Response
        {
            StatusCode = 200,
            ResponseTime = 0.123,
            Headers = responseHeaders,
            Size = 13,
            Body = System.Text.Encoding.UTF8.GetBytes("{\"items\": []}"),
        };
        _client.RequestLogger.LogRequest(request, response);
        _client.RequestLogger.Maintain();

        var paths = new List<Path>
        {
            new() { Method = "GET", PathValue = "/items" },
        };
        var versions = new Dictionary<string, string> { { "package", "1.0.0" } };
        _client.SetStartupData(paths, versions, "dotnet:test");

        // Act
        await _client.StartAsync(CancellationToken.None);
        await Task.Delay(100); // Allow some time for requests to be made
        await _client.StopAsync(CancellationToken.None);

        // Assert
        Assert.Contains(_requests, r => r.RequestUri!.AbsolutePath.Contains("/startup"));
        Assert.Contains(_requests, r => r.RequestUri!.AbsolutePath.Contains("/sync"));
        Assert.Contains(_requests, r => r.RequestUri!.AbsolutePath.Contains("/log"));
    }
}
