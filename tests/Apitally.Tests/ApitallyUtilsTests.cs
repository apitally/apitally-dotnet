namespace Apitally.Tests;

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
        var paths = ApitallyUtils.GetPaths(
            _factory.Services.GetRequiredService<IActionDescriptorCollectionProvider>(),
            _factory.Services.GetServices<EndpointDataSource>()
        );

        // Assert
        Assert.Equal(6, paths.Count);
        Assert.Single(paths, p => p.Method == "GET" && p.PathValue == "/items");
        Assert.Single(paths, p => p.Method == "GET" && p.PathValue == "/items/{id:min(1)}");
        Assert.Single(paths, p => p.Method == "POST" && p.PathValue == "/items");
        Assert.Single(paths, p => p.Method == "PUT" && p.PathValue == "/items/{id:min(1)}");
        Assert.Single(paths, p => p.Method == "DELETE" && p.PathValue == "/items/{id:min(1)}");
        Assert.Single(paths, p => p.Method == "GET" && p.PathValue == "/throw");
    }

    [Fact]
    public void GetVersions_ReturnsCorrectVersions()
    {
        // Act
        var versions = ApitallyUtils.GetVersions();

        // Assert
        Assert.Matches(@"^\d+\.\d+\.\d+$", versions["dotnet"]);
        Assert.Matches(@"^\d+\.\d+\.\d+(?:\.\d+)?$", versions["aspnetcore"]);
    }
}
