namespace Apitally.Tests;

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
        var response = await _httpClient.GetAsync("/items");
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
        Assert.Equal(3, requests.Count);

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
}
