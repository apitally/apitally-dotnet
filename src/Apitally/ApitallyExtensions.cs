namespace Apitally;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

public static class ApitallyExtensions
{
    public static IServiceCollection AddApitally(this IServiceCollection services)
    {
        return services.AddApitally(null);
    }

    public static IServiceCollection AddApitally(
        this IServiceCollection services,
        Action<ApitallyOptions>? configureOptions
    )
    {
        // Configure options from appsettings.json first
        services
            .AddOptions<ApitallyOptions>()
            .Configure<IConfiguration>(
                (options, configuration) =>
                {
                    configuration.GetSection("Apitally").Bind(options);
                }
            )
            .ValidateDataAnnotations();

        // If options are provided directly, they take precedence
        if (configureOptions != null)
        {
            services.PostConfigure(configureOptions);
        }

        // Register IHttpClientFactory which is required by ApitallyClient
        services.AddHttpClient();

        // Ensure MVC services are registered and add global filter
        services.AddSingleton<ValidationErrorFilter>();
        services.AddControllers(options =>
        {
            var filter = services
                .BuildServiceProvider()
                .GetRequiredService<ValidationErrorFilter>();
            options.Filters.Add(filter);
        });

        // Register RequestLogger and ApitallyClient
        services.AddSingleton<RequestLogger>();
        services.AddSingleton<ApitallyClient>();
        services.AddHostedService(sp => sp.GetRequiredService<ApitallyClient>());
        services.AddHostedService(sp => sp.GetRequiredService<RequestLogger>());

        return services;
    }

    public static IApplicationBuilder UseApitally(this IApplicationBuilder builder)
    {
        // Defer path collection until after application has fully started
        builder
            .ApplicationServices.GetRequiredService<IHostApplicationLifetime>()
            .ApplicationStarted.Register(() =>
            {
                var client = builder.ApplicationServices.GetRequiredService<ApitallyClient>();
                var endpointSources = builder.ApplicationServices.GetServices<EndpointDataSource>();
                var paths = ApitallyUtils.GetPaths(endpointSources);
                var versions = ApitallyUtils.GetVersions();

                client.SetStartupData(
                    paths: paths,
                    versions: versions,
                    client: "dotnet:aspnetcore"
                );
            });

        return builder.UseMiddleware<ApitallyMiddleware>();
    }
}
