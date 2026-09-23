using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace TransportLifecycle;

internal static class Registration
{
    public static void AddTransportPoc(this WebApplicationBuilder builder, ProbeState state) =>
        Add(builder.Services, state);

    public static IHostBuilder AddTransportPoc(this IHostBuilder builder, ProbeState state) =>
        builder.ConfigureServices(services => Add(services, state));

    private static void Add(IServiceCollection services, ProbeState state)
    {
        services.AddSingleton(state);
        services.AddSingleton<IStartupFilter, TransportFilter>();
        services.AddHostedService<LifecycleProbe>();
        // Test instrumentation only: expose the public server start/stop boundaries.
        var descriptor = services.Last(d => d.ServiceType == typeof(IServer));
        services.Remove(descriptor);
        services.AddSingleton<IServer>(provider => new RecordingServer(
            (IServer)(
                descriptor.ImplementationInstance
                ?? descriptor.ImplementationFactory?.Invoke(provider)
                ?? ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!)
            ),
            state
        ));
    }
}

internal sealed class LifecycleProbe : IHostedLifecycleService
{
    private readonly ProbeState state;

    public LifecycleProbe(ProbeState state, IHostApplicationLifetime lifetime)
    {
        this.state = state;
        lifetime.ApplicationStarted.Register(() =>
        {
            state.Note("host.started");
            state.Activate("host");
        });
        lifetime.ApplicationStopping.Register(() => state.Note("host.stopping"));
        lifetime.ApplicationStopped.Register(() => state.Note("host.stopped"));
    }

    public Task StartingAsync(CancellationToken cancellationToken)
    {
        state.Note("lifecycle.starting");
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        state.Note("lifecycle.start");
        return Task.CompletedTask;
    }

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        state.Note("lifecycle.started");
        return Task.CompletedTask;
    }

    public Task StoppingAsync(CancellationToken cancellationToken)
    {
        state.Note("lifecycle.stopping");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        state.Note("lifecycle.stop");
        return Task.CompletedTask;
    }

    public async Task StoppedAsync(CancellationToken cancellationToken)
    {
        state.Note("lifecycle.stopped.enter");
        state.FinalBudgetCancelled = cancellationToken.IsCancellationRequested;
        state.UnresolvedAtFinalDrain = state.Records.Count(r => !r.Released);
        state.ReleasedAtFinalDrain = state.Released;
        if (state.DelayFinalDrain)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                state.FinalBudgetCancelled = true;
                state.Note("final-drain.cancelled");
            }
        }
        state.FinalDrains++;
        state.Note("lifecycle.stopped.exit");
    }
}

internal sealed class RecordingServer(IServer inner, ProbeState state) : IServer
{
    public IFeatureCollection Features => inner.Features;

    public async Task StartAsync<TContext>(
        IHttpApplication<TContext> application,
        CancellationToken cancellationToken
    )
        where TContext : notnull
    {
        state.Note("server.start.enter");
        await inner.StartAsync(application, cancellationToken);
        state.Note("server.start.exit");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        state.Note("server.stop.enter");
        state.ServerStopping.TrySetResult();
        await inner.StopAsync(cancellationToken);
        state.Note("server.stop.exit");
    }

    public void Dispose()
    {
        state.Note("server.dispose");
        inner.Dispose();
    }
}

internal sealed class ProbeState : IDisposable
{
    private readonly object sync = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly List<Mark> marks = [];
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RequestRecord>> requests =
        new();
    private readonly ConcurrentDictionary<string, RequestRecord> activities = new();
    private readonly ActivityListener listener;
    public TaskCompletionSource ServerStopping { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Activations { get; private set; }
    public string? ActivationTrigger { get; private set; }
    public int Released { get; private set; }
    public int FinalDrains { get; set; }
    public int UnresolvedAtFinalDrain { get; set; }
    public int ReleasedAtFinalDrain { get; set; }
    public bool FinalBudgetCancelled { get; set; }
    public bool DelayFinalDrain { get; set; }
    public string FilePath { get; set; } = "";
    public IEnumerable<RequestRecord> Records =>
        requests.Values.Where(t => t.Task.IsCompletedSuccessfully).Select(t => t.Task.Result);

    public ProbeState()
    {
        listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activities.TryGetValue(activity.Id!, out var record))
                {
                    record.Note("activity.stopped");
                    record.ActivityEnded = true;
                    TryRelease(record);
                    record.ActivityStopped.TrySetResult();
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
    }

    public void Activate(string trigger)
    {
        lock (sync)
        {
            if (Activations != 0)
                return;
            Activations++;
            ActivationTrigger = trigger;
            Note("activate." + trigger);
        }
    }

    public RequestRecord Begin(HttpContext context)
    {
        var record = new RequestRecord(context, this);
        context.Features.Set(record);
        if (!activities.TryAdd(Activity.Current!.Id!, record))
            throw new InvalidOperationException("Duplicate SERVER activity");
        record.Note("observer.enter");
        if (!Slot(record.Path).TrySetResult(record))
            throw new InvalidOperationException("Duplicate probe path");
        return record;
    }

    public Task<RequestRecord> Wait(string path) =>
        Slot(path).Task.WaitAsync(TimeSpan.FromSeconds(8));

    public void TryRelease(RequestRecord record)
    {
        lock (sync)
        {
            if (!record.TransportEnded || !record.ActivityEnded || record.Released)
                return;
            record.Released = true;
            Released++;
            record.Note("joined");
        }
    }

    public void Note(string name)
    {
        lock (sync)
            marks.Add(new Mark(marks.Count, clock.Elapsed.TotalMilliseconds, name));
    }

    public int Order(string name)
    {
        lock (sync)
            return marks.Single(m => m.Name == name).Order;
    }

    public bool Has(string name)
    {
        lock (sync)
            return marks.Any(m => m.Name == name);
    }

    public void Print(string label, params string[] paths)
    {
        Console.WriteLine($"TIMELINE {label}");
        lock (sync)
            foreach (
                var mark in marks.Where(m =>
                    !m.Name.StartsWith('/') || paths.Any(p => m.Name.StartsWith(p + " "))
                )
            )
                Console.WriteLine($"  {mark.Order, 3} {mark.Milliseconds, 9:F2}ms {mark.Name}");
    }

    public void Dispose() => listener.Dispose();

    private TaskCompletionSource<RequestRecord> Slot(string path) =>
        requests.GetOrAdd(
            path,
            _ => new TaskCompletionSource<RequestRecord>(
                TaskCreationOptions.RunContinuationsAsynchronously
            )
        );

    private sealed record Mark(int Order, double Milliseconds, string Name);
}

internal sealed class RequestRecord
{
    private readonly ProbeState state;
    public string Path { get; }
    public string? EntryRoute { get; }
    public string? FinalEndpointRoute { get; private set; }
    public string? OriginalRoute { get; private set; }
    public string? OriginalPath { get; private set; }
    public int Status { get; private set; }
    public string? Encoding { get; private set; }
    public long? ResponseLength { get; private set; }
    public long? RequestLength { get; }
    public BodyObservation Request { get; }
    public BodyObservation Response { get; }
    public bool Aborted;
    public bool AbortTokenAtCompletion;
    public bool Escaped;
    public bool ResponseComplete;
    public bool TransportEnded;
    public bool ActivityEnded;
    public bool Released;
    public TaskCompletionSource Gate { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Waiting { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Completed { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ActivityStopped { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RequestRecord(HttpContext context, ProbeState state)
    {
        this.state = state;
        Path = context.Request.Path;
        EntryRoute = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        RequestLength = context.Request.ContentLength;
        var requestType = context.Request.ContentType;
        Request = new BodyObservation(() => (requestType, RequestLength));
        Request.Observe([]);
        Response = new BodyObservation(() =>
            (context.Response.ContentType, context.Response.ContentLength)
        );
    }

    public void ReadFinalMetadata(HttpContext context)
    {
        FinalEndpointRoute = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        var error = context.Features.Get<IExceptionHandlerPathFeature>();
        OriginalRoute =
            (error?.Endpoint as RouteEndpoint)?.RoutePattern.RawText ?? FinalEndpointRoute;
        OriginalPath = error?.Path;
        Status = context.Response.StatusCode;
        Encoding = context.Response.Headers.ContentEncoding;
        ResponseLength = context.Response.ContentLength;
    }

    public void Note(string name) => state.Note(Path + " " + name);

    public async Task Finish() =>
        await Task.WhenAll(Completed.Task, ActivityStopped.Task).WaitAsync(TimeSpan.FromSeconds(8));
}
