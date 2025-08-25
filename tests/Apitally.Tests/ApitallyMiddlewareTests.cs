namespace Apitally.Tests;

using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

public class ApitallyMiddlewareTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _httpClient;
    private readonly ApitallyClient _apitallyClient;
    private readonly ApitallyOptions _apitallyOptions;

    public ApitallyMiddlewareTests(WebApplicationFactory<Program> factory)
    {
        _httpClient = factory.CreateClient();
        _apitallyClient = factory.Services.GetRequiredService<ApitallyClient>();
        _apitallyOptions = factory.Services.GetRequiredService<IOptions<ApitallyOptions>>().Value;
    }

    [Fact]
    public async Task RequestCounter_ShouldCountRequests()
    {
        // Arrange
        _apitallyOptions.RequestLogging.Enabled = false;
        _apitallyClient.RequestCounter.Clear();

        // Act
        var response = await _httpClient.GetAsync("/controller/items");
        response.EnsureSuccessStatusCode();

        response = await _httpClient.GetAsync("/items");
        response.EnsureSuccessStatusCode();

        response = await _httpClient.GetAsync("/items/1");
        response.EnsureSuccessStatusCode();

        response = await _httpClient.GetAsync("/items/2");
        response.EnsureSuccessStatusCode();

        response = await _httpClient.GetAsync("/throw");
        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, response.StatusCode);

        await Task.Delay(100);

        // Assert
        var requests = _apitallyClient.RequestCounter.GetAndResetRequests();
        Assert.Equal(4, requests.Count);

        Assert.Single(
            requests,
            r =>
                r.Method == "GET"
                && r.Path == "/controller/items"
                && r.StatusCode == 200
                && r.RequestCount == 1
                && r.ResponseSizeSum > 0
        );
        Assert.Single(
            requests,
            r =>
                r.Method == "GET"
                && r.Path == "/items"
                && r.StatusCode == 200
                && r.RequestCount == 1
                && r.ResponseSizeSum > 0
        );
        Assert.Single(
            requests,
            r =>
                r.Method == "GET"
                && r.Path == "/items/{id:min(1)}"
                && r.StatusCode == 200
                && r.RequestCount == 2
        );
        Assert.Single(
            requests,
            r =>
                r.Method == "GET"
                && r.Path == "/throw"
                && r.StatusCode == 500
                && r.RequestCount == 1
                && r.ResponseSizeSum == 0
        );

        var resetRequests = _apitallyClient.RequestCounter.GetAndResetRequests();
        Assert.Empty(resetRequests);
    }

    [Fact]
    public async Task ValidationErrorCounter_ShouldCountErrors()
    {
        // Arrange
        _apitallyClient.ValidationErrorCounter.Clear();

        // Act
        var response = await _httpClient.PostAsync(
            "/controller/items",
            new StringContent("{\"id\": 1001}", System.Text.Encoding.UTF8, "application/json")
        );
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);

        await Task.Delay(100);

        // Assert
        var errors = _apitallyClient.ValidationErrorCounter.GetAndResetValidationErrors();
        Assert.Equal(2, errors.Count);
        Assert.Single(
            errors,
            e =>
                e.Location.SequenceEqual(new[] { "Id" })
                && e.Message == "The field Id must be between 1 and 1000."
        );
        Assert.Single(
            errors,
            e =>
                e.Location.SequenceEqual(new[] { "Name" })
                && e.Message == "The Name field is required."
        );
    }

    [Fact]
    public async Task ServerErrorCounter_ShouldCountErrors()
    {
        // Arrange
        _apitallyClient.ServerErrorCounter.Clear();

        // Act
        var response = await _httpClient.GetAsync("/throw");
        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, response.StatusCode);

        await Task.Delay(100);

        // Assert
        var serverErrors = _apitallyClient.ServerErrorCounter.GetAndResetServerErrors();
        Assert.Single(serverErrors);
        var error = Assert.Single(serverErrors);
        Assert.Equal("TestException", error.Type);
        Assert.Equal("an expected error occurred", error.Message);
        Assert.True(error.StackTrace.Length > 100);
        Assert.Equal(1, error.ErrorCount);

        var resetErrors = _apitallyClient.ServerErrorCounter.GetAndResetServerErrors();
        Assert.Empty(resetErrors);
    }

    [Fact]
    public async Task ConsumerRegistry_ShouldTrackConsumers()
    {
        // Arrange
        _apitallyClient.ConsumerRegistry.Clear();

        // Act
        await _httpClient.GetAsync("/items");
        await _httpClient.GetAsync("/items");

        await Task.Delay(100);

        // Assert
        var consumers = _apitallyClient.ConsumerRegistry.GetAndResetConsumers();
        Assert.Single(consumers);
        var consumer = Assert.Single(consumers);
        Assert.Equal("tester", consumer.Identifier);
        Assert.Equal("Tester", consumer.Name);
        Assert.Equal("Test Group", consumer.Group);

        var resetConsumers = _apitallyClient.ConsumerRegistry.GetAndResetConsumers();
        Assert.Empty(resetConsumers);
    }

    [Fact]
    public async Task RequestLogger_ShouldLogRequests()
    {
        // Arrange
        _apitallyOptions.RequestLogging.Enabled = true;
        _apitallyOptions.RequestLogging.IncludeRequestBody = true;
        _apitallyOptions.RequestLogging.IncludeResponseBody = true;
        _apitallyClient.RequestLogger.Clear();

        // Act
        var response = await _httpClient.GetAsync("/items");
        response.EnsureSuccessStatusCode();

        response = await _httpClient.PostAsync(
            "/items",
            new StringContent(
                "{\"id\": 1, \"name\": \"bob\"}",
                System.Text.Encoding.UTF8,
                "application/json"
            )
        );
        response.EnsureSuccessStatusCode();

        // Assert
        _apitallyClient.RequestLogger.Maintain();
        _apitallyClient.RequestLogger.RotateFile();
        var logFile = _apitallyClient.RequestLogger.GetFile();
        Assert.NotNull(logFile);
        Assert.True(logFile.Size > 0);

        var lines = logFile.ReadDecompressedLines();
        Assert.Equal(2, lines.Count);

        var jsonNode = JsonDocument.Parse(lines[0]).RootElement;
        Assert.Equal("GET", jsonNode.GetProperty("request").GetProperty("method").GetString());
        Assert.Equal(
            "http://localhost/items",
            jsonNode.GetProperty("request").GetProperty("url").GetString()
        );
        Assert.Equal(200, jsonNode.GetProperty("response").GetProperty("status_code").GetInt32());
        Assert.Contains(
            "alice",
            Encoding.UTF8.GetString(
                Convert.FromBase64String(
                    jsonNode.GetProperty("response").GetProperty("body").GetString()!
                )
            )
        );

        jsonNode = JsonDocument.Parse(lines[1]).RootElement;
        Assert.Equal("POST", jsonNode.GetProperty("request").GetProperty("method").GetString());
        Assert.Equal(
            "http://localhost/items",
            jsonNode.GetProperty("request").GetProperty("url").GetString()
        );
        Assert.Contains(
            "bob",
            Encoding.UTF8.GetString(
                Convert.FromBase64String(
                    jsonNode.GetProperty("request").GetProperty("body").GetString()!
                )
            )
        );

        logFile.Delete();
    }

    [Fact]
    public async Task RequestLogger_ShouldCaptureApplicationLogs()
    {
        // Arrange
        _apitallyOptions.RequestLogging.Enabled = true;
        _apitallyOptions.RequestLogging.CaptureLogs = true;
        _apitallyClient.RequestLogger.Clear();

        // Act - Make request to endpoint that has logging
        var response = await _httpClient.GetAsync("/items?name=test");
        response.EnsureSuccessStatusCode();

        // Assert
        _apitallyClient.RequestLogger.Maintain();
        _apitallyClient.RequestLogger.RotateFile();
        var logFile = _apitallyClient.RequestLogger.GetFile();
        Assert.NotNull(logFile);
        Assert.True(logFile.Size > 0);

        var lines = logFile.ReadDecompressedLines();
        Assert.Single(lines);

        var jsonNode = JsonDocument.Parse(lines[0]).RootElement;

        // Check that logs property exists and contains captured logs
        Assert.True(jsonNode.TryGetProperty("logs", out var logsProperty));
        Assert.True(logsProperty.GetArrayLength() > 0);

        // Verify log structure
        var firstLog = logsProperty[0];
        Assert.True(firstLog.TryGetProperty("timestamp", out _));
        Assert.True(firstLog.TryGetProperty("logger", out _));
        Assert.True(firstLog.TryGetProperty("level", out _));
        Assert.True(firstLog.TryGetProperty("message", out _));

        // The logs should contain our application logs from the endpoint
        bool hasRetrievingItemsLog = false;
        for (int i = 0; i < logsProperty.GetArrayLength(); i++)
        {
            var log = logsProperty[i];
            var message = log.GetProperty("message").GetString();
            if (message != null && message.Contains("Retrieving items"))
            {
                hasRetrievingItemsLog = true;
                Assert.Equal("Information", log.GetProperty("level").GetString());
                break;
            }
        }
        Assert.True(hasRetrievingItemsLog, "Expected to find the 'Retrieving items' log message");

        logFile.Delete();
    }
}
