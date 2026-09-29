using Apitally.AspNetCore;
using Apitally.Hosting;
using Apitally.Logging;
using Apitally.Requests;
using Apitally.Tracing;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Apitally;

/// <summary>Extension methods for adding Apitally to an application.</summary>
public static class ApitallyExtensions
{
    /// <summary>
    /// Adds Apitally monitoring to an ASP.NET Core application. Options are read from the
    /// <c>Apitally</c> configuration section; <paramref name="configure"/> runs afterwards and
    /// takes precedence. Calling this more than once registers Apitally only once.
    /// </summary>
    public static IServiceCollection AddApitally(
        this IServiceCollection services,
        Action<ApitallyOptions>? configure = null
    )
    {
        var isFirstCall = !services.Any(service => service.ServiceType == typeof(TelemetryRuntime));
        services.AddOptions();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IConfigureOptions<ApitallyOptions>,
                BaseOptionsConfiguration
            >()
        );
        if (configure is not null)
            services.PostConfigure(configure);
        services.TryAddSingleton<TelemetryRuntime>();
        services.AddHttpContextAccessor();
        services.TryAddSingleton<IApitally, RequestHelpers>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IDeveloperPageExceptionFilter,
                DeveloperPageExceptionCapture
            >()
        );
        services.TryAddSingleton<ApitallyLoggerProvider>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ILoggerProvider, ApitallyLoggerProvider>(serviceProvider =>
                serviceProvider.GetRequiredService<ApitallyLoggerProvider>()
            )
        );
        if (isFirstCall)
        {
            TracingIntegration.Register(services);
            ValidationCapture.Register(services);
        }
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IStartupFilter, ApitallyStartupFilter>()
        );
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, ApitallyHostedService>()
        );
        return services;
    }
}
