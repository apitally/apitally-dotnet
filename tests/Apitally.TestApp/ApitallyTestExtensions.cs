using Apitally;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

public static class ApitallyTestExtensions
{
    public static IServiceCollection AddApitallyWithoutBackgroundServices(
        this IServiceCollection services,
        Action<ApitallyOptions>? configureOptions = null
    )
    {
        services.AddOptions<ApitallyOptions>();
        if (configureOptions != null)
        {
            services.PostConfigure(configureOptions);
        }
        services.AddControllers();
        services.AddSingleton<RequestLogger>();
        services.AddSingleton<ApitallyClient>();
        return services;
    }
}
