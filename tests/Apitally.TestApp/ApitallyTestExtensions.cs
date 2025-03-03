namespace Apitally.TestApp;

using Apitally;

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
        services.AddSingleton<ValidationErrorFilter>();
        services.AddControllers(options =>
        {
            var filter = services
                .BuildServiceProvider()
                .GetRequiredService<ValidationErrorFilter>();
            options.Filters.Add(filter);
        });
        services.AddSingleton<RequestLogger>();
        services.AddSingleton<ApitallyClient>();
        return services;
    }
}
