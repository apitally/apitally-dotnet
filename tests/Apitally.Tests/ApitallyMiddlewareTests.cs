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
        // Disable request logging for this test
        _apitallyOptions.RequestLogging.Enabled = false;

        // Act - Make multiple requests
        var response = await _httpClient.GetAsync("/items");
        response.EnsureSuccessStatusCode();

        response = await _httpClient.GetAsync("/items/1");
        response.EnsureSuccessStatusCode();

        response = await _httpClient.GetAsync("/items/2");
        response.EnsureSuccessStatusCode();

        response = await _httpClient.GetAsync("/throw");
        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, response.StatusCode);

        await Task.Delay(100);

        // Assert that the requests are counted correctly
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

        // Verify reset works
        var resetRequests = _apitallyClient.RequestCounter.GetAndResetRequests();
        Assert.Empty(resetRequests);
    }
}
