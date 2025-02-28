namespace Apitally.Tests;

using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

public class ApitallyMiddlewareTests : IClassFixture<WebApplicationFactory<TestApp>>
{
    private readonly WebApplicationFactory<TestApp> _factory;
    private readonly HttpClient _client;

    public ApitallyMiddlewareTests(WebApplicationFactory<TestApp> factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Get_EndpointsReturnSuccessAndCorrectContentType()
    {
        // Act
        var response = await _client.GetAsync("/items");

        // Assert
        response.EnsureSuccessStatusCode();
        Assert.Equal(
            "application/json; charset=utf-8",
            response.Content.Headers.ContentType?.ToString()
        );
    }
}
