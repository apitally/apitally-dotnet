namespace Apitally.Tests;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public class ApitallyUtilsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ApitallyUtilsTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public void GetPaths_ReturnsAllPaths()
    {
        // Act
        var paths = ApitallyUtils.GetPaths(_factory.Services.GetServices<EndpointDataSource>());

        // Assert
        Assert.Equal(8, paths.Count);
        Assert.Single(paths, p => p.Method == "GET" && p.PathValue == "/items");
        Assert.Single(paths, p => p.Method == "GET" && p.PathValue == "/items/{id:min(1)}");
        Assert.Single(paths, p => p.Method == "POST" && p.PathValue == "/items");
        Assert.Single(paths, p => p.Method == "PUT" && p.PathValue == "/items/{id:min(1)}");
        Assert.Single(paths, p => p.Method == "DELETE" && p.PathValue == "/items/{id:min(1)}");
        Assert.Single(paths, p => p.Method == "GET" && p.PathValue == "/throw");
        Assert.Single(paths, p => p.Method == "GET" && p.PathValue == "/controller/items");
        Assert.Single(paths, p => p.Method == "POST" && p.PathValue == "/controller/items");
    }

    [Fact]
    public void GetVersions_ReturnsCorrectVersions()
    {
        // Act
        var versions = ApitallyUtils.GetVersions();

        // Assert
        Assert.Matches(@"^\d+\.\d+\.\d+$", versions["dotnet"]);
        Assert.Matches(@"^\d+\.\d+\.\d+(?:\.\d+)?$", versions["aspnetcore"]);
        Assert.Matches(@"^\d+\.\d+\.\d+", versions["apitally"]);
    }

    [Theory]
    [InlineData("X-Forwarded-Proto", "https", true)]
    [InlineData("X-Forwarded-Proto", "https, http", true)]
    [InlineData("X-Forwarded-Proto", "http", false)]
    [InlineData("X-Forwarded-Protocol", "https", true)]
    [InlineData("X-Forwarded-Scheme", "https", true)]
    [InlineData("X-Url-Scheme", "https", true)]
    [InlineData("X-Scheme", "https", true)]
    [InlineData("Forwarded", "for=192.0.2.1;proto=https;host=example.com", true)]
    [InlineData("Forwarded", "proto=\"https\"", true)]
    [InlineData("Forwarded", "for=192.0.2.1;proto=http", false)]
    [InlineData("Forwarded", "for=192.0.2.1;proto=http, for=192.0.2.2;proto=https", false)]
    [InlineData("Front-End-Https", "on", true)]
    [InlineData("X-Forwarded-Ssl", "on", true)]
    [InlineData(null, null, false)]
    public void IsHttps_DetectsProxyHeaders(string? header, string? value, bool expected)
    {
        var headers = new HeaderDictionary();
        if (header is not null)
            headers[header] = value;
        Assert.Equal(expected, ApitallyUtils.IsHttps(headers));
    }
}
