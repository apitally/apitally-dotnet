using OpenTelemetry;
using OpenTelemetry.Trace;

namespace RequestAssociation;

internal static class Candidate
{
    public static void Register(IServiceCollection services, Probe probe)
    {
        services.AddHttpContextAccessor();
        services.AddSingleton<RequestHelpers>();
        services.ConfigureOpenTelemetryTracerProvider(builder =>
            builder
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddSource(LocalHost.SourceName)
        );
        services.ConfigureOpenTelemetryTracerProvider(
            (services, builder) =>
            {
                probe.Accessor = services.GetRequiredService<IHttpContextAccessor>();
                probe.BuilderConfigured = true;
                builder.AddProcessor(probe);
            }
        );
        services.AddSingleton(services => new CandidateRuntime(services, probe));
        services.AddSingleton<IStartupFilter, CandidateStartupFilter>();
        services.AddSingleton<ILoggerProvider>(_ => new CaptureAdapter(probe));
    }
}

internal sealed class CandidateRuntime : IDisposable
{
    public CandidateRuntime(IServiceProvider services, Probe probe)
    {
        Probe = probe;
        probe.Accessor = services.GetRequiredService<IHttpContextAccessor>();
        Provider = services.GetService<TracerProvider>();
        OwnsProvider = Provider is null;
        if (OwnsProvider)
            Provider = Sdk.CreateTracerProviderBuilder()
                .SetSampler(new AlwaysOnSampler())
                .AddAspNetCoreInstrumentation(options =>
                    options.EnrichWithHttpRequest = LocalHost.Enrich
                )
                .AddHttpClientInstrumentation(options =>
                    options.EnrichWithHttpRequestMessage = services
                        .GetRequiredService<Fixture>()
                        .RecordClient
                )
                .AddSource(LocalHost.SourceName)
                .AddProcessor(probe)
                .Build();
        else if (!probe.BuilderConfigured)
            Provider!.AddProcessor(probe);
    }

    public Probe Probe { get; }
    public TracerProvider? Provider { get; }
    public bool OwnsProvider { get; }

    public void Dispose()
    {
        Probe.Cutoff();
        if (OwnsProvider)
            Provider!.Dispose();
    }
}

internal sealed class CandidateStartupFilter(CandidateRuntime runtime) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
        app =>
        {
            app.Use(
                async (context, nextMiddleware) =>
                {
                    var request = runtime.Probe.Ensure(context);
                    context.Response.OnCompleted(() =>
                    {
                        runtime.Probe.Guard(() => request.CaptureTransport(context));
                        return Task.CompletedTask;
                    });
                    await nextMiddleware(context);
                }
            );
            next(app);
        };
}
