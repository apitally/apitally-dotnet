using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http.Features;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace ProviderRegistration;

// An experiment, not an SDK API. Never register TracerProvider on the host's behalf.
internal static class Candidate
{
    public static void Register(
        IServiceCollection services,
        Probe probe,
        TracerProvider? external = null
    )
    {
        services.ConfigureOpenTelemetryTracerProvider(builder =>
            builder.AddAspNetCoreInstrumentation().AddSource("apitally.otel")
        );
        services.ConfigureOpenTelemetryTracerProvider(
            (_, builder) =>
            {
                probe.BuilderConfigured = true;
                builder.AddProcessor(probe);
            }
        );
        services.AddSingleton(sp => new CandidateRuntime(sp, probe, external));
        services.AddSingleton<IStartupFilter, CandidateStartupFilter>();
    }
}

internal sealed class CandidateRuntime : IDisposable
{
    public CandidateRuntime(IServiceProvider services, Probe probe, TracerProvider? external)
    {
        Probe = probe;
        Provider = external ?? services.GetService<TracerProvider>();
        OwnsProvider = Provider is null;
        if (OwnsProvider)
        {
            Provider = Sdk.CreateTracerProviderBuilder()
                .SetSampler(new AlwaysOnSampler())
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddSource("apitally.otel")
                .AddProcessor(probe)
                .Build();
        }
        else if (!probe.BuilderConfigured)
        {
            // A separately built provider cannot run the host's builder callbacks.
            Provider!.AddProcessor(probe);
        }
    }

    public Probe Probe { get; }
    public TracerProvider? Provider { get; }
    public bool OwnsProvider { get; }

    public void Dispose()
    {
        Probe.Enabled = false;
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
                    var server = context.Features.Get<IHttpActivityFeature>()?.Activity;
                    if (server is not null)
                        runtime.Probe.Associate(server);
                    await nextMiddleware(context);
                }
            );
            next(app);
        };
}

internal sealed class Probe : BaseProcessor<Activity>
{
    private readonly ConditionalWeakTable<Activity, object> requests = new();
    public readonly ConcurrentDictionary<string, bool> Starts = new();
    public readonly ConcurrentQueue<Span> Raw = new();
    public readonly ConcurrentQueue<Span> Accepted = new();
    public bool BuilderConfigured;
    public volatile bool Enabled = true;

    public void Associate(Activity server) => requests.GetValue(server, _ => new object());

    public override void OnStart(Activity activity) => Starts.TryAdd(activity.Id!, true);

    public override void OnEnd(Activity activity)
    {
        var span = Span.Copy(activity);
        Raw.Enqueue(span);
        if (!Enabled || !activity.Recorded)
            return;
        // This only establishes export association after middleware has seen the request.
        for (var current = activity; current is not null; current = current.Parent)
        {
            if (!requests.TryGetValue(current, out _))
                continue;
            Accepted.Enqueue(span);
            return;
        }
    }
}

internal sealed class MemoryExporter : BaseExporter<Activity>
{
    public readonly ConcurrentQueue<Span> Spans = new();
    public bool Disposed;

    public override ExportResult Export(in Batch<Activity> batch)
    {
        foreach (var activity in batch)
            Spans.Enqueue(Span.Copy(activity));
        return ExportResult.Success;
    }

    protected override void Dispose(bool disposing) => Disposed = true;
}

internal sealed record Span(
    string Id,
    string TraceId,
    string SpanId,
    string ParentSpanId,
    ActivityKind Kind,
    bool Recorded,
    string Name,
    string Source,
    string? Path,
    string? Route,
    string? UserTag
)
{
    public static Span Copy(Activity activity) =>
        new(
            activity.Id!,
            activity.TraceId.ToHexString(),
            activity.SpanId.ToHexString(),
            activity.ParentSpanId.ToHexString(),
            activity.Kind,
            activity.Recorded,
            activity.DisplayName,
            activity.Source.Name,
            activity.GetTagItem("url.path")?.ToString(),
            activity.GetTagItem("http.route")?.ToString(),
            activity.GetTagItem("user.enrichment")?.ToString()
        );
}

internal sealed class CountingSampler(SamplingDecision decision) : Sampler
{
    public readonly ConcurrentQueue<SamplingParameters> Calls = new();

    public override SamplingResult ShouldSample(in SamplingParameters parameters)
    {
        Calls.Enqueue(parameters);
        return new SamplingResult(decision);
    }
}
