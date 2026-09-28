using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Apitally.Tracing;

// Joins the application's tracer provider when one is enabled, or builds a private fallback
// provider. Apitally never registers a TracerProvider service on the application's behalf.
internal sealed class TracingIntegration
{
    public const string ManualSourceName = "apitally.otel";

    private readonly ForwardingProcessor processor = new();
    private bool isAttachedThroughBuilder;
    private TracerProvider? ownedProvider;

    public static readonly ActivitySource ManualSource = new(
        ManualSourceName,
        Export.OtlpEncoder.DistroVersion
    );

    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<TracingIntegration>();
        // Configure-only contributions never enable a provider themselves, so the application's
        // sampler, resource and exporters stay in effect.
        services.ConfigureOpenTelemetryTracerProvider(builder =>
            builder
                .AddAspNetCoreInstrumentation()
                .AddSource(ManualSourceName)
                .AddProcessor(serviceProvider =>
                {
                    var integration = serviceProvider.GetRequiredService<TracingIntegration>();
                    integration.isAttachedThroughBuilder = true;
                    return integration.processor;
                })
        );
    }

    // Resolves the application's provider or builds the fallback before the server starts.
    public void Prepare(IServiceProvider services, Resource resource)
    {
        var provider = services.GetService<TracerProvider>();
        if (provider is null)
            ownedProvider = Sdk.CreateTracerProviderBuilder()
                .SetResourceBuilder(
                    ResourceBuilder.CreateEmpty().AddAttributes(resource.Attributes)
                )
                .SetSampler(new RequestSampler())
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddSource("*")
                .AddProcessor(processor)
                .Build();
        // An existing-instance registration never ran the builder callback. Its owner keeps
        // disposal responsibility.
        else if (!isAttachedThroughBuilder)
            provider.AddProcessor(processor);
    }

    public void Attach(ApitallySpanProcessor target) => processor.Target = target;

    // A retained forwarding processor in an external provider holds no Apitally state.
    public void Detach() => processor.Target = null;

    public void DisposeOwnedProvider()
    {
        ownedProvider?.Dispose();
        ownedProvider = null;
    }

    private sealed class ForwardingProcessor : BaseProcessor<Activity>
    {
        private volatile ApitallySpanProcessor? target;

        public ApitallySpanProcessor? Target
        {
            set => target = value;
        }

        public override void OnStart(Activity data) => target?.OnStart(data, ParentProvider);

        public override void OnEnd(Activity data) => target?.OnEnd(data, ParentProvider);
    }

    // Records the ASP.NET Core hosting activity regardless of any remote parent, and local
    // children of recorded activities. Background work and other roots are never recorded.
    private sealed class RequestSampler : Sampler
    {
        private static readonly SamplingResult Record = new(SamplingDecision.RecordAndSample);
        private static readonly SamplingResult Drop = new(SamplingDecision.Drop);

        public override SamplingResult ShouldSample(in SamplingParameters parameters)
        {
            var parent = parameters.ParentContext;
            return
                parameters.Name == ApitallySpanProcessor.HostingOperationName
                || (!parent.IsRemote && parent.TraceFlags.HasFlag(ActivityTraceFlags.Recorded))
                ? Record
                : Drop;
        }
    }
}
