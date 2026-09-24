using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace TestHostSuppression;

// These public types belong only to this executable probe, not the SDK API.
public static class Candidate
{
    public static void Register(IServiceCollection services)
    {
        services.TryAddSingleton(new ProbeOptions());
        var serverRegistration = services.LastOrDefault(service =>
            service.ServiceType == typeof(IServer)
        );
        services.AddSingleton(
            new Observation
            {
                ServerAtRegistration =
                    serverRegistration?.ImplementationType?.FullName
                    ?? serverRegistration?.ImplementationInstance?.GetType().FullName
                    ?? (serverRegistration is null ? "<none>" : "<factory>"),
            }
        );
        services.AddSingleton<CandidateRuntime>();
        services.AddSingleton<IStartupFilter, CandidateStartupFilter>();
        services.Configure<HostOptions>(options =>
        {
            options.StartupTimeout = TimeSpan.FromSeconds(10);
            options.ShutdownTimeout = TimeSpan.FromSeconds(5);
        });
    }
}

public sealed record ProbeOptions(bool Enabled = true, bool OmitApplicationStartedTrigger = false);

public sealed class CandidateRuntime(ProbeOptions options, Observation observation) : IDisposable
{
    private TracerProvider? provider;
    private bool ownsProvider;
    private CancellationTokenRegistration startedRegistration;

    public void Initialize(IServiceProvider services, IServer server)
    {
        var serverType = server.GetType();
        observation.ResolvedServer = serverType.FullName;
        observation.ResolvedServerAssembly = serverType.Assembly.GetName().Name;
        observation.Suppressed =
            serverType.FullName == "Microsoft.AspNetCore.TestHost.TestServer"
            && serverType.Assembly.GetName().Name == "Microsoft.AspNetCore.TestHost";

        // Resolve or construct tracing only after the actual server decision is final.
        if (options.Enabled && !observation.Suppressed)
        {
            provider = services.GetService<TracerProvider>();
            var exporter = new MemoryExporter(() => observation.Active);
            observation.CandidateExporter = exporter;
            var processor = new SimpleActivityExportProcessor(exporter);
            if (provider is null)
            {
                observation.FallbackConstructions++;
                provider = Sdk.CreateTracerProviderBuilder()
                    .SetSampler(new AlwaysOnSampler())
                    .AddAspNetCoreInstrumentation()
                    .AddProcessor(processor)
                    .Build();
                ownsProvider = true;
            }
            else
            {
                provider.AddProcessor(processor);
            }
        }

        if (!options.OmitApplicationStartedTrigger)
        {
            startedRegistration = services
                .GetRequiredService<IHostApplicationLifetime>()
                .ApplicationStarted.Register(() =>
                {
                    Interlocked.Increment(ref observation.StartupSignals);
                    Activate();
                });
        }
    }

    public void OnRequest()
    {
        Interlocked.Increment(ref observation.RequestSignals);
        Activate();
    }

    public void Dispose()
    {
        startedRegistration.Dispose();
        observation.Active = false;
        if (ownsProvider)
            provider!.Dispose();
        observation.Disposal.TrySetResult();
    }

    private void Activate()
    {
        if (!options.Enabled || observation.Suppressed)
            return;
        if (Interlocked.CompareExchange(ref observation.Activations, 1, 0) == 0)
            observation.Active = true;
    }
}

internal sealed class CandidateStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
        app =>
        {
            var services = app.ApplicationServices;
            var runtime = services.GetRequiredService<CandidateRuntime>();
            runtime.Initialize(services, services.GetRequiredService<IServer>());
            app.Use(
                async (context, nextMiddleware) =>
                {
                    runtime.OnRequest();
                    await nextMiddleware(context);
                }
            );
            next(app);
        };
}

public sealed class Observation
{
    public readonly TaskCompletionSource Disposal = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    public string? ServerAtRegistration;
    public string? ResolvedServer;
    public string? ResolvedServerAssembly;
    public bool Suppressed;
    public int FallbackConstructions;
    public int StartupSignals;
    public int RequestSignals;
    public int Activations;
    public volatile bool Active;
    public MemoryExporter? CandidateExporter;
}
