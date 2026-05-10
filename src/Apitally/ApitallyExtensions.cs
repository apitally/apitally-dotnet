namespace Apitally;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
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
        services.AddHttpClient(
            "Apitally",
            client =>
            {
                client.BaseAddress = new Uri(
                    Environment.GetEnvironmentVariable("APITALLY_HUB_BASE_URL")
                        ?? "https://hub.apitally.io"
                );
                client.Timeout = TimeSpan.FromSeconds(10);
            }
        );

        // Register custom logger provider for log capture
        services.AddSingleton<ApitallyLoggerProvider>();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(sp =>
            sp.GetRequiredService<ApitallyLoggerProvider>()
        );

        // Inert unless the user calls AddControllers() — keeps minimal-API apps free of MVC services.
        services.Configure<MvcOptions>(options =>
        {
            var filter = (TypeFilterAttribute)
                options.Filters.Add<ValidationErrorFilter>(ValidationErrorFilter.FilterOrder);
            filter.IsReusable = true;
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
