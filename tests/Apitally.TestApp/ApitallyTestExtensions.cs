namespace Apitally.TestApp;

using Apitally;
using Microsoft.AspNetCore.Mvc;

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

        services.AddHttpClient(
            "Apitally",
            client =>
            {
                client.BaseAddress = new Uri("http://test");
                client.Timeout = TimeSpan.FromSeconds(1);
            }
        );

        services.AddSingleton<ApitallyLoggerProvider>();
        services.AddSingleton<ILoggerProvider>(sp =>
            sp.GetRequiredService<ApitallyLoggerProvider>()
        );

        services.Configure<MvcOptions>(options =>
        {
            options.Filters.Add<ValidationErrorFilter>(ValidationErrorFilter.FilterOrder);
        });
        services.AddSingleton<RequestLogger>();
        services.AddSingleton<ApitallyClient>();
        return services;
    }
}
