namespace Apitally.Tests;

using System.Text;
using System.Text.Json;
using Apitally.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

public class RequestLoggerTests
{
    [Fact]
    public void LogRequest_ShouldWorkEndToEnd()
    {
        // Arrange
        var requestLogger = CreateRequestLogger(
            new RequestLoggingOptions
            {
                Enabled = true,
                IncludeQueryParams = true,
                IncludeRequestHeaders = true,
                IncludeRequestBody = true,
                IncludeResponseHeaders = true,
                IncludeResponseBody = true,
            }
        );
        var request = new Request
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Consumer = "tester",
            Method = "GET",
            Path = "/items",
            Url = "http://test/items",
            Headers = new[] { new Header("User-Agent", "Test") },
            Size = 0,
            Body = Array.Empty<byte>(),
        };
        var response = new Response
        {
            StatusCode = 200,
            ResponseTime = 0.123,
            Headers = new[] { new Header("Content-Type", "application/json") },
            Size = 13,
            Body = Encoding.UTF8.GetBytes("{\"items\": []}"),
        };

        // Act
        requestLogger.LogRequest(request, response);
        requestLogger.Maintain();
        requestLogger.RotateFile();

        // Assert
        var logFile = requestLogger.GetFile();
        Assert.NotNull(logFile);
        Assert.True(logFile.Size > 0);

        var lines = logFile.ReadDecompressedLines();
        Assert.Single(lines);

        var jsonNode = JsonDocument.Parse(lines[0]).RootElement;
        Assert.Equal("GET", jsonNode.GetProperty("request").GetProperty("method").GetString());
        Assert.Equal("/items", jsonNode.GetProperty("request").GetProperty("path").GetString());
        Assert.Equal(
            "http://test/items",
            jsonNode.GetProperty("request").GetProperty("url").GetString()
        );
        Assert.False(jsonNode.GetProperty("request").TryGetProperty("body", out _));
        Assert.Equal(200, jsonNode.GetProperty("response").GetProperty("status_code").GetInt32());
        Assert.Equal(
            0.123,
            jsonNode.GetProperty("response").GetProperty("response_time").GetDouble(),
            3
        );
        Assert.Equal(
            "{\"items\": []}",
            Encoding.UTF8.GetString(
                Convert.FromBase64String(
                    jsonNode.GetProperty("response").GetProperty("body").GetString()!
                )
            )
        );

        var requestHeadersNode = jsonNode.GetProperty("request").GetProperty("headers");
        Assert.Equal(1, requestHeadersNode.GetArrayLength());
        Assert.Equal("User-Agent", requestHeadersNode[0][0].GetString());
        Assert.Equal("Test", requestHeadersNode[0][1].GetString());

        var responseHeadersNode = jsonNode.GetProperty("response").GetProperty("headers");
        Assert.Equal(1, responseHeadersNode.GetArrayLength());
        Assert.Equal("Content-Type", responseHeadersNode[0][0].GetString());
        Assert.Equal("application/json", responseHeadersNode[0][1].GetString());
    }

    [Fact]
    public void LogRequest_ShouldExcludeUsingCallback()
    {
        // Arrange
        var requestLogger = CreateRequestLogger(
            new RequestLoggingOptions
            {
                Enabled = true,
                ShouldExclude = (request, response) =>
                    request.Consumer?.Contains("tester") ?? false,
            }
        );
        var request = new Request
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Consumer = "tester",
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
            Size = 13,
            Body = Encoding.UTF8.GetBytes("{\"items\": []}"),
        };

        // Act
        requestLogger.LogRequest(request, response);

        // Assert
        requestLogger.Maintain();
        requestLogger.RotateFile();
        var logFile = requestLogger.GetFile();
        Assert.Null(logFile);
    }

    [Fact]
    public void LogRequest_ShouldExcludeHealthCheckPath()
    {
        // Arrange
        var requestLogger = CreateRequestLogger(new RequestLoggingOptions { Enabled = true });
        var request = new Request
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Method = "GET",
            Path = "/healthz",
            Url = "http://test/healthz",
            Headers = Array.Empty<Header>(),
            Size = 0,
            Body = Array.Empty<byte>(),
        };
        var response = new Response
        {
            StatusCode = 200,
            ResponseTime = 0.123,
            Headers = Array.Empty<Header>(),
            Size = 17,
            Body = Encoding.UTF8.GetBytes("{\"healthy\": true}"),
        };

        // Act
        requestLogger.LogRequest(request, response);

        // Assert
        requestLogger.Maintain();
        requestLogger.RotateFile();
        var logFile = requestLogger.GetFile();
        Assert.Null(logFile);
    }

    [Fact]
    public void LogRequest_ShouldExcludeHealthCheckUserAgent()
    {
        // Arrange
        var requestLogger = CreateRequestLogger(new RequestLoggingOptions { Enabled = true });
        var request = new Request
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Method = "GET",
            Path = "/",
            Url = "http://test/",
            Headers = new[] { new Header("User-Agent", "ELB-HealthChecker/2.0") },
            Size = 0,
            Body = Array.Empty<byte>(),
        };
        var response = new Response
        {
            StatusCode = 200,
            ResponseTime = 0,
            Headers = Array.Empty<Header>(),
            Size = 0,
            Body = Array.Empty<byte>(),
        };

        // Act
        requestLogger.LogRequest(request, response);

        // Assert
        requestLogger.Maintain();
        requestLogger.RotateFile();
        var logFile = requestLogger.GetFile();
        Assert.Null(logFile);
    }

    [Fact]
    public void LogRequest_ShouldMask()
    {
        // Arrange
        var requestLogger = CreateRequestLogger(
            new RequestLoggingOptions
            {
                Enabled = true,
                IncludeQueryParams = true,
                IncludeRequestHeaders = true,
                IncludeRequestBody = true,
                IncludeResponseHeaders = true,
                IncludeResponseBody = true,
                MaskRequestBody = request => null,
                MaskResponseBody = (request, response) => null,
            }
        );
        var request = new Request
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Consumer = "tester",
            Method = "POST",
            Path = "/items",
            Url = "http://test/items?token=my-secret-token",
            Headers = new[]
            {
                new Header("Authorization", "Bearer 1234567890"),
                new Header("Content-Type", "application/json"),
            },
            Size = 16,
            Body = Encoding.UTF8.GetBytes("{\"key\": \"value\"}"),
        };
        var response = new Response
        {
            StatusCode = 200,
            ResponseTime = 0.123,
            Headers = new[] { new Header("Content-Type", "application/json") },
            Size = 16,
            Body = Encoding.UTF8.GetBytes("{\"key\": \"value\"}"),
        };

        // Act
        requestLogger.LogRequest(request, response);
        requestLogger.Maintain();
        requestLogger.RotateFile();

        // Assert
        var logFile = requestLogger.GetFile();
        Assert.NotNull(logFile);
        Assert.True(logFile.Size > 0);

        var lines = logFile.ReadDecompressedLines();
        Assert.Single(lines);

        var jsonNode = JsonDocument.Parse(lines[0]).RootElement;
        Assert.Equal(
            "http://test/items?token=******",
            jsonNode.GetProperty("request").GetProperty("url").GetString()
        );
        Assert.Equal(
            "<masked>",
            Encoding.UTF8.GetString(
                Convert.FromBase64String(
                    jsonNode.GetProperty("request").GetProperty("body").GetString()!
                )
            )
        );
        Assert.Equal(
            "<masked>",
            Encoding.UTF8.GetString(
                Convert.FromBase64String(
                    jsonNode.GetProperty("response").GetProperty("body").GetString()!
                )
            )
        );

        var requestHeadersNode = jsonNode.GetProperty("request").GetProperty("headers");
        Assert.Equal(2, requestHeadersNode.GetArrayLength());
        Assert.Equal("Authorization", requestHeadersNode[0][0].GetString());
        Assert.Equal("******", requestHeadersNode[0][1].GetString());
    }

    private static RequestLogger CreateRequestLogger(RequestLoggingOptions requestLoggingOptions)
    {
        var options = new OptionsWrapper<ApitallyOptions>(
            new ApitallyOptions
            {
                ClientId = "00000000-0000-0000-0000-000000000000",
                Env = "test",
                RequestLogging = requestLoggingOptions,
            }
        );
        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        return new RequestLogger(options, loggerFactory.CreateLogger<RequestLogger>());
    }
}
