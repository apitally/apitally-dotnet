using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using OpenTelemetry;

namespace RequestAssociation;

internal sealed class Probe : BaseProcessor<Activity>
{
    public const string ServerLink = "apitally.request.server_span_id";
    private readonly ConcurrentDictionary<
        (ActivityTraceId, ActivitySpanId),
        RequestState
    > associations = new();
    private readonly ConcurrentDictionary<RequestState, byte> requests = new();
    private readonly object admission = new();
    public IHttpContextAccessor Accessor { get; set; } = null!;
    public OwnedExporter Exporter { get; } = new();
    public OwnedBatch Batch { get; }
    public ConcurrentQueue<Exception> Failures { get; } = new();
    public ConcurrentDictionary<string, Observation> Observations { get; } = new();
    public ConcurrentQueue<Task> Handoffs { get; } = new();
    public ManualClock Clock { get; } = new();
    public bool BuilderConfigured;
    public volatile bool Enabled = true;
    public string HandoffMode { get; set; } = "native";
    public int MapCount => associations.Count;
    public int RetainedPayload => requests.Keys.Sum(request => request.PayloadCount);
    public int SpanCap { get; set; } = 16;
    public int LogCap { get; set; } = 16;
    private bool stopped;

    public Probe() => Batch = new OwnedBatch(Exporter);

    public override void OnStart(Activity activity) =>
        Guard(() =>
        {
            if (!Enabled)
                return;
            if (
                activity.Kind == ActivityKind.Server
                && activity.Source.Name == "Microsoft.AspNetCore"
            )
            {
                var context = Accessor.HttpContext;
                if (context is null)
                    throw new InvalidOperationException(
                        "HttpContext unavailable at native SERVER OnStart"
                    );
                Ensure(context, activity);
            }
            else if (
                activity.Recorded
                && TryGet(activity.TraceId, activity.ParentSpanId, out var request)
            )
                Associate(activity, request);
        });

    public override void OnEnd(Activity activity) =>
        Guard(() =>
        {
            if (!Enabled || !TryGet(activity.TraceId, activity.SpanId, out var request))
                return;
            if (activity.SpanId == request.ServerId)
                request.CaptureServer(Owned.Span(activity));
            else if (activity.Recorded)
                request.Add(Owned.Span(activity));
        });

    public RequestState Ensure(HttpContext context, Activity? activity = null)
    {
        lock (admission)
        {
            var existing = context.Features.Get<RequestState>();
            if (existing is not null)
                return existing;
            activity ??= context.Features.Get<IHttpActivityFeature>()?.Activity;
            var path = context.Request.Path.Value!;
            var id = path.Split('/').Last();
            var observation = Observations.GetOrAdd(id, _ => new Observation(id));
            var request = new RequestState(
                this,
                observation,
                activity,
                context.Request.Method,
                path,
                context.Request.Headers.UserAgent.ToString()
            );
            context.Features.Set(request);
            requests.TryAdd(request, 0);
            if (Enabled && activity?.Recorded == true)
                Associate(activity, request);
            return request;
        }
    }

    private void Associate(Activity activity, RequestState request)
    {
        lock (admission)
            if (Enabled && requests.ContainsKey(request))
                associations.TryAdd((activity.TraceId, activity.SpanId), request);
    }

    public bool TryGet(ActivityTraceId trace, ActivitySpanId span, out RequestState request) =>
        associations.TryGetValue((trace, span), out request!);

    public void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception error)
        {
            Failures.Enqueue(error);
        }
    }

    public void Cutoff()
    {
        lock (admission)
        {
            Enabled = false;
            foreach (var request in requests.Keys)
                request.Cutoff();
            associations.Clear();
        }
    }

    public void Cleanup()
    {
        lock (admission)
        {
            foreach (var request in requests.Keys)
            {
                if (!request.Expired(Clock.GetUtcNow()))
                    continue;
                foreach (var pair in associations)
                    if (ReferenceEquals(pair.Value, request))
                        associations.TryRemove(pair.Key, out _);
                requests.TryRemove(request, out _);
            }
        }
    }

    public async Task Drain()
    {
        await Task.WhenAll(Handoffs.ToArray()).WaitAsync(TimeSpan.FromSeconds(5));
        Handoffs.Clear();
        CheckFailures();
        Program.Check(Batch.ForceFlush(5000), "owned batch force flush returned");
        Exporter.Wait(Batch.Submitted);
        CheckFailures();
    }

    public void CheckFailures()
    {
        if (!Failures.IsEmpty)
            throw new AggregateException(Failures);
    }

    public void Stop()
    {
        if (stopped)
            return;
        stopped = true;
        Cutoff();
        Program.Check(Batch.Shutdown(5000), "owned batch shutdown joined worker");
        Exporter.Wait(Batch.Submitted);
        Batch.Dispose();
        requests.Clear();
        CheckFailures();
    }

    protected override void Dispose(bool disposing) { }
}

internal sealed class RequestHelpers(IHttpContextAccessor accessor)
{
    public void SetConsumer(string consumer) => Set("apitally.consumer", consumer);

    public void SetAttribute(string name, string value) => Set("apitally.custom." + name, value);

    public void CaptureException(Exception exception) =>
        accessor.HttpContext?.Features.Get<RequestState>()?.CaptureException(exception);

    private void Set(string name, string value) =>
        accessor.HttpContext?.Features.Get<RequestState>()?.Set(name, value);
}

internal sealed class RequestState
{
    private readonly object sync = new();
    private readonly Probe probe;
    private readonly Observation observation;
    private readonly CompletionHandoff handoff;
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly bool eligible;
    private readonly bool requestDrop;
    private string? method;
    private string? path;
    private string? userAgent;
    private Dictionary<string, object?> helpers = new();
    private readonly List<Owned> pending = [];
    private Owned? server;
    private Transport? transport;
    private bool transportComplete;
    private bool serverComplete;
    private bool finalized;
    private bool keep;
    private bool cutoff;
    private int spans;
    private int logs;
    private DateTimeOffset? finalizedAt;

    public RequestState(
        Probe probe,
        Observation observation,
        Activity? activity,
        string method,
        string path,
        string userAgent
    )
    {
        this.probe = probe;
        this.observation = observation;
        this.method = method;
        this.path = path;
        this.userAgent = userAgent;
        eligible = activity?.Recorded == true;
        cutoff = !probe.Enabled;
        requestDrop = path.StartsWith("/request-drop/") || path.StartsWith("/sink/");
        ServerId = activity?.SpanId ?? default;
        TraceId = activity?.TraceId ?? default;
        observation.ServerId = ServerId;
        observation.TraceId = TraceId;
        observation.Sampled = eligible;
        observation.EarlyMethod = method;
        observation.EarlyPath = path;
        observation.EarlyUserAgent = userAgent;
        observation.StartTagsAbsent =
            activity?.GetTagItem("http.request.method") is null
            && activity?.GetTagItem("url.path") is null
            && activity?.GetTagItem("http.route") is null;
        handoff = new CompletionHandoff(probe, probe.HandoffMode);
    }

    public ActivitySpanId ServerId { get; }
    public ActivityTraceId TraceId { get; }
    public int PayloadCount
    {
        get
        {
            lock (sync)
                return pending.Count
                    + helpers.Count
                    + (server is null ? 0 : 1)
                    + (transport is null ? 0 : 1)
                    + (path is null ? 0 : 1)
                    + (userAgent is null ? 0 : 1)
                    + (method is null ? 0 : 1);
        }
    }

    public void Set(string name, string value)
    {
        lock (sync)
            if (!finalized && !transportComplete)
                helpers[name] = value;
    }

    public void CaptureException(Exception exception)
    {
        lock (sync)
        {
            if (finalized || transportComplete || helpers.ContainsKey("exception.type"))
                return;
            helpers["exception.type"] = exception.GetType().FullName;
            helpers["exception.message"] = exception.Message;
        }
    }

    public void Add(Owned item)
    {
        lock (sync)
        {
            if (!probe.Enabled || cutoff || !eligible || requestDrop || (finalized && !keep))
                return;
            if (item.Signal == "span")
            {
                if (spans >= probe.SpanCap)
                    return;
                spans++;
            }
            else
            {
                if (logs >= probe.LogCap)
                    return;
                logs++;
            }
            item.Attributes[Probe.ServerLink] = ServerId.ToHexString();
            if (finalized)
                probe.Batch.OnEnd(item);
            else
                pending.Add(item);
        }
    }

    public void CaptureServer(Owned snapshot)
    {
        observation.Native.Enqueue("server");
        observation.NativeServer.TrySetResult();
        handoff.Submit("server", () => CompleteServer(snapshot));
    }

    public void CaptureTransport(HttpContext context)
    {
        Transport snapshot;
        lock (sync)
            snapshot = new Transport(
                method ?? context.Request.Method,
                path ?? context.Request.Path.Value!,
                context.Response.StatusCode,
                (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText,
                context.Connection.Id,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                context.Request.ContentLength,
                context.Response.ContentLength,
                new Dictionary<string, object?>(helpers)
            );
        observation.Transport = snapshot;
        observation.Native.Enqueue("transport");
        observation.NativeTransport.TrySetResult();
        if (!eligible)
            CompleteTransport(snapshot);
        else
            handoff.Submit("transport", () => CompleteTransport(snapshot));
    }

    private void CompleteServer(Owned snapshot)
    {
        lock (sync)
        {
            if (serverComplete)
                return;
            serverComplete = true;
            observation.Processed.Enqueue("server");
            if (!cutoff)
                server = snapshot;
            FinalizeIfReady();
        }
    }

    private void CompleteTransport(Transport snapshot)
    {
        lock (sync)
        {
            if (transportComplete)
                return;
            transportComplete = true;
            observation.Processed.Enqueue("transport");
            if (!cutoff)
                transport = snapshot;
            FinalizeIfReady();
        }
    }

    private void FinalizeIfReady()
    {
        if (finalized || !transportComplete || (eligible && !serverComplete && !cutoff))
            return;
        finalized = true;
        finalizedAt = probe.Clock.GetUtcNow();
        if (!cutoff)
        {
            if (server is not null)
            {
                server.Attributes["http.request.method"] = transport!.Method;
                server.Attributes["http.response.status_code"] = transport.Status;
                if (transport.Route is { } route)
                    server.Attributes["http.route"] = route;
                else
                    server.Attributes.Remove("http.route");
                if (transport.RequestBytes is { } requestBytes)
                    server.Attributes["http.request.body.size"] = requestBytes;
                else
                    server.Attributes.Remove("http.request.body.size");
                if (transport.ResponseBytes is { } responseBytes)
                    server.Attributes["http.response.body.size"] = responseBytes;
                else
                    server.Attributes.Remove("http.response.body.size");
                foreach (var pair in transport.Helpers)
                    server.Attributes[pair.Key] = pair.Value;
                server.Attributes[Probe.ServerLink] = ServerId.ToHexString();
                server.Attributes["apitally.request.method"] = method;
                server.Attributes["apitally.request.path"] = path;
                server.Attributes["apitally.request.user_agent"] = userAgent;
            }
            var decision =
                transport!.Path.StartsWith("/response-drop/") ? "drop"
                : transport.Path.StartsWith("/abstain/") ? "abstain"
                : "keep";
            observation.Decision = new Decision(decision, transport, server);
            observation.DecisionCount++;
            keep = eligible && !requestDrop && decision != "drop";
            if (keep)
            {
                foreach (var item in pending.Where(item => item.Signal == "span"))
                    probe.Batch.OnEnd(item);
                probe.Batch.OnEnd(server!);
                foreach (var item in pending.Where(item => item.Signal == "log"))
                    probe.Batch.OnEnd(item);
                observation.ReleaseCount++;
                observation.CompletionsAtRelease = observation.Processed.Count;
            }
        }
        ClearPayload();
        observation.Done.TrySetResult();
    }

    public void Cutoff()
    {
        lock (sync)
        {
            cutoff = true;
            keep = false;
            pending.Clear();
            server = null;
            if (transportComplete)
                ClearPayload();
        }
    }

    public bool Expired(DateTimeOffset now)
    {
        lock (sync)
            // POC-only retention: evict all IDs one minute after finalization, even live children.
            return finalizedAt is { } time && now - time >= TimeSpan.FromMinutes(1);
    }

    private void ClearPayload()
    {
        pending.Clear();
        server = null;
        transport = null;
        helpers.Clear();
        path = null;
        userAgent = null;
        method = null;
    }
}

internal sealed record Transport(
    string Method,
    string Path,
    int Status,
    string? Route,
    string ConnectionId,
    double DurationMilliseconds,
    long? RequestBytes,
    long? ResponseBytes,
    Dictionary<string, object?> Helpers
);

internal sealed record Decision(string Verdict, Transport Transport, Owned? Server);

// This read-only test ledger is not used to discover associations or make decisions.
internal sealed class Observation(string id)
{
    public string Id { get; } = id;
    public ActivityTraceId TraceId;
    public ActivitySpanId ServerId;
    public bool Sampled;
    public string? EarlyMethod;
    public string? EarlyPath;
    public string? EarlyUserAgent;
    public bool StartTagsAbsent;
    public Transport? Transport;
    public Decision? Decision;
    public int DecisionCount;
    public int ReleaseCount;
    public int CompletionsAtRelease;
    public ConcurrentQueue<string> Native { get; } = new();
    public ConcurrentQueue<string> Processed { get; } = new();
    public TaskCompletionSource NativeServer { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource NativeTransport { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Done { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

// TEST ONLY: delays processing immutable data captured from real native completion events.
internal sealed class CompletionHandoff(Probe probe, string mode)
{
    private readonly object sync = new();
    private Action? server;
    private Action? transport;

    public void Submit(string kind, Action process)
    {
        if (mode == "native")
        {
            process();
            return;
        }
        lock (sync)
        {
            if (kind == "server")
                server = process;
            else
                transport = process;
            if (server is null || transport is null)
                return;
            var serverAction = server;
            var transportAction = transport;
            server = null;
            transport = null;
            if (mode == "reverse")
            {
                serverAction();
                transportAction();
            }
            else
            {
                var gate = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously
                );
                var ready = 0;
                var tasks = new[] { serverAction, transportAction }
                    .Select(action =>
                        Task.Run(async () =>
                        {
                            if (Interlocked.Increment(ref ready) == 2)
                                gate.TrySetResult();
                            await gate.Task.WaitAsync(TimeSpan.FromSeconds(5));
                            probe.Guard(action);
                        })
                    )
                    .ToArray();
                probe.Handoffs.Enqueue(Task.WhenAll(tasks));
            }
        }
    }
}

internal sealed class ManualClock : TimeProvider
{
    private long ticks = DateTimeOffset.UtcNow.Ticks;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);

    public void Advance(TimeSpan value) => Interlocked.Add(ref ticks, value.Ticks);
}
