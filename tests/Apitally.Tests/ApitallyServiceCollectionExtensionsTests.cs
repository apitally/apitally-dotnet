using Apitally.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Apitally.Tests;

public class ApitallyServiceCollectionExtensionsTests
{
    [Fact]
    public void ConfigurationSectionOverridesEnvironmentFallbacks()
    {
        using var environment = new EnvironmentVariables(
            ("APITALLY_WRITE_TOKEN", "apt_fromEnvironment000000000"),
            ("APITALLY_ENV", "staging")
        );

        var options = ResolveOptions(
            new() { ["Apitally:Env"] = "prod", ["Apitally:SampleRate"] = "0.5" }
        );

        Assert.Equal("apt_fromEnvironment000000000", options.WriteToken);
        Assert.Equal("prod", options.Env);
        Assert.Equal(0.5, options.SampleRate);
        Assert.True(options.CaptureResponseHeaders);
    }

    [Fact]
    public void CodeCallbacksOverrideConfigurationInRegistrationOrder()
    {
        using var environment = new EnvironmentVariables();

        var options = ResolveOptions(
            new()
            {
                ["Apitally:Env"] = "prod",
                ["Apitally:CaptureLogs"] = "false",
                ["Apitally:ExcludePaths:0"] = "/a",
            },
            services =>
            {
                services.AddApitally(options =>
                {
                    options.Env = "first";
                    options.ExcludePaths.Add("/b");
                });
                services.AddApitally(options =>
                {
                    Assert.Equal("first", options.Env);
                    options.Env = "second";
                });
            }
        );

        Assert.Equal("second", options.Env);
        Assert.False(options.CaptureLogs);
        Assert.Equal(["/a", "/b"], options.ExcludePaths);
    }

    [Fact]
    public void DirectConfigureFollowsStandardOrdering()
    {
        using var environment = new EnvironmentVariables();

        var options = ResolveOptions(
            new() { ["Apitally:Env"] = "prod" },
            services =>
            {
                services.Configure<ApitallyOptions>(options => options.AppVersion = "before");
                services.AddApitally();
                services.Configure<ApitallyOptions>(options => options.Env = "after");
            }
        );

        Assert.Equal("before", options.AppVersion);
        Assert.Equal("after", options.Env);
    }

    private static ApitallyOptions ResolveOptions(
        Dictionary<string, string?> configuration,
        Action<IServiceCollection>? register = null
    )
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder().AddInMemoryCollection(configuration).Build()
        );
        if (register is null)
            services.AddApitally();
        else
            register(services);
        return services
            .BuildServiceProvider()
            .GetRequiredService<IOptions<ApitallyOptions>>()
            .Value;
    }
}
