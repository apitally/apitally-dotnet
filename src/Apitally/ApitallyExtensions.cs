namespace Apitally;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;

public static class ApitallyExtensions
{
    public static IServiceCollection AddApitally(this IServiceCollection services)
    {
        return services.AddApitally(null);
    }

    public static IServiceCollection AddApitally(this IServiceCollection services, Action<ApitallyOptions>? configureOptions)
    {
        // Configure options from appsettings.json first
        services.AddOptions<ApitallyOptions>()
            .Configure<IConfiguration>((options, configuration) =>
            {
                configuration.GetSection("Apitally").Bind(options);
            })
            .ValidateDataAnnotations();

        // If options are provided directly, they take precedence
        if (configureOptions != null)
        {
            services.PostConfigure(configureOptions);
        }

        services.AddHostedService<ApitallyClient>();
        services.AddSingleton(sp => sp.GetRequiredService<IHostedService>() as ApitallyClient ??
            throw new InvalidOperationException("Failed to resolve ApitallyClient"));

        return services;
    }

    public static IApplicationBuilder UseApitally(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<ApitallyMiddleware>();
    }
}
