namespace Apitally.Tests;

using System.Net;
using Apitally;
using Apitally.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using Xunit;

public class ApitallyClientTests
{
    [Fact]
    public async Task BackgroundService_ShouldSyncWithHub()
    {
        // Arrange
        var mockHttpHandler = new Mock<HttpMessageHandler>();
        var hubRequests = new List<HttpRequestMessage>();
        mockHttpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK))
            .Callback<HttpRequestMessage, CancellationToken>(
                (request, _) => hubRequests.Add(request)
            );
        var client = CreateClient(mockHttpHandler.Object);

        var request = new Request
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Consumer = "tester",
            Method = "GET",
            Path = "/items",
            Url = "http://test/items",
            Headers = new[]
            {
                new Header("Content-Type", "application/json"),
                new Header("User-Agent", "test-client"),
            },
            Size = 0,
            Body = Array.Empty<byte>(),
        };
        var response = new Response
        {
            StatusCode = 200,
            ResponseTime = 0.123,
            Headers = new[] { new Header("Content-Type", "application/json") },
            Size = 13,
            Body = System.Text.Encoding.UTF8.GetBytes("{\"items\": []}"),
        };
        client.RequestLogger.LogRequest(request, response);
        client.RequestLogger.Maintain();

        var paths = new List<Path>
        {
            new() { Method = "GET", PathValue = "/items" },
        };
        var versions = new Dictionary<string, string> { { "package", "1.0.0" } };
        client.SetStartupData(paths, versions, "dotnet:test");

        // Act
        await client.StartAsync(CancellationToken.None);
        await Task.Delay(100); // Allow some time for requests to be made
        await client.StopAsync(CancellationToken.None);

        // Assert
        Assert.Contains(hubRequests, r => r.RequestUri!.AbsolutePath.Contains("/startup"));
        Assert.Contains(hubRequests, r => r.RequestUri!.AbsolutePath.Contains("/sync"));
        Assert.Contains(hubRequests, r => r.RequestUri!.AbsolutePath.Contains("/log"));
    }

    [Fact]
    public async Task BackgroundService_ShouldDisableAfter404()
    {
        // Arrange
        var mockHttpHandler = new Mock<HttpMessageHandler>();
        mockHttpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = CreateClient(mockHttpHandler.Object);

        // Act
        await client.StartAsync(CancellationToken.None);
        await Task.Delay(100);

        // Assert
        Assert.False(client.Enabled);
    }

    [Fact]
    public async Task RequestLogger_ShouldSuspendOn402()
    {
        // Arrange
        var mockHttpHandler = new Mock<HttpMessageHandler>();
        mockHttpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.PaymentRequired));
        var client = CreateClient(mockHttpHandler.Object);

        var request = new Request
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Method = "GET",
            Path = "/items",
            Url = "http://test/items",
            Headers = Array.Empty<Header>(),
            Size = 0,
            Body = Array.Empty<byte>(),
        };
        var response = new Response
        {
            StatusCode = 200,
            ResponseTime = 0.123,
            Headers = Array.Empty<Header>(),
            Size = 0,
            Body = Array.Empty<byte>(),
        };
        client.RequestLogger.LogRequest(request, response);
        client.RequestLogger.Maintain();

        // Act
        await client.StartAsync(CancellationToken.None);
        await Task.Delay(100);

        // Assert
        Assert.True(client.Enabled);
        Assert.True(client.RequestLogger.Suspended);
    }

    private static ApitallyClient CreateClient(HttpMessageHandler httpHandler)
    {
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

        // Create a mock IHttpClientFactory that returns a client with the specified handler
        var httpClient = new HttpClient(httpHandler) { BaseAddress = new Uri("http://test") };
        var mockFactory = new Mock<IHttpClientFactory>();
        mockFactory.Setup(f => f.CreateClient("Apitally")).Returns(httpClient);

        var client = new ApitallyClient(
            options,
            requestLogger,
            loggerFactory.CreateLogger<ApitallyClient>(),
            mockFactory.Object
        );
        return client;
    }
}
