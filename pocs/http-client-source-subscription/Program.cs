using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using OpenTelemetry;
using OpenTelemetry.Trace;

const string HostingActivityName = "Microsoft.AspNetCore.Hosting.HttpRequestIn";
const string HttpClientActivityName = "System.Net.Http.HttpRequestOut";

var variant = args.Single();
Console.WriteLine(
    $"Runtime: {RuntimeInformation.FrameworkDescription}; Environment.Version {Environment.Version}"
);
Console.WriteLine($"AspNetCore: {typeof(WebApplication).Assembly.Location}");
Console.WriteLine(
    $"DiagnosticSource: {typeof(Activity).Assembly.GetName().Version} {typeof(Activity).Assembly.Location}"
);
Console.WriteLine($"Variant: {variant}");

var failures = new List<string>();
void Check(bool condition, string description)
{
    Console.WriteLine($"{(condition ? "PASS" : "FAIL")} {description}");
    if (!condition)
        failures.Add(description);
}

var exported = new ExportedActivities();
var sampler = new RequestSampler();
var tracerProviderBuilder = Sdk.CreateTracerProviderBuilder()
    .SetSampler(sampler)
    .AddAspNetCoreInstrumentation()
    .AddHttpClientInstrumentation()
    .AddInMemoryExporter(exported);
tracerProviderBuilder = variant switch
{
    "wildcard" => tracerProviderBuilder.AddSource("*"),
    "baseline" => tracerProviderBuilder.AddSource("PocApp", "apitally.otel"),
    _ => throw new ArgumentException($"Unknown variant {variant}"),
};
var tracerProvider = tracerProviderBuilder.Build();

var appSource = new ActivitySource("PocApp");
var apitallySource = new ActivitySource("apitally.otel");
var port = FreeLoopbackPort();
var outgoingClient = new HttpClient();
var sinkTraceparents = new ConcurrentDictionary<string, string>();

var appBuilder = WebApplication.CreateBuilder(new WebApplicationOptions());
appBuilder.Logging.ClearProviders();
appBuilder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenLocalhost(port));
var app = appBuilder.Build();
app.MapGet(
    "/api",
    async (string id) =>
    {
        using (appSource.StartActivity("app-work")) { }
        using (apitallySource.StartActivity("apitally-work")) { }
        // "localhost" makes the outgoing call resolve a host name.
        await outgoingClient.GetStringAsync($"http://localhost:{port}/sink?id={id}");
        return "ok";
    }
);
app.MapGet(
    "/sink",
    (HttpRequest request, string id) =>
    {
        sinkTraceparents[id] = request.Headers["traceparent"].ToString();
        return "sink";
    }
);
await app.StartAsync();

// Driver requests use raw TCP so no client-side HttpClient activity or propagation is involved.
var remoteParentUnsampled = ActivitySpanId.CreateRandom();
var remoteTraceUnsampled = ActivityTraceId.CreateRandom();
var remoteParentSampled = ActivitySpanId.CreateRandom();
var remoteTraceSampled = ActivityTraceId.CreateRandom();
var calls = new (string Id, string? Traceparent)[]
{
    ("no-traceparent", null),
    ("remote-unsampled", $"00-{remoteTraceUnsampled}-{remoteParentUnsampled}-00"),
    ("remote-sampled", $"00-{remoteTraceSampled}-{remoteParentSampled}-01"),
};
var apiTraceIds = new Dictionary<string, ActivityTraceId>();
foreach (var (id, traceparent) in calls)
{
    var response = await RawGet(port, $"/api?id={id}", traceparent);
    Check(
        response.StartsWith("HTTP/1.1 200") && response.Split("\r\n\r\n", 2)[1].Contains("ok"),
        $"{id}: /api returned 200 with body ok"
    );
    var traceId = ActivityTraceId.CreateFromString(sinkTraceparents[id].Split('-')[1]);
    apiTraceIds[id] = traceId;
    await WaitUntil(
        () =>
            exported
                .Snapshot()
                .Count(activity =>
                    activity.TraceId == traceId && activity.Kind == ActivityKind.Server
                ) == 2,
        $"{id}: both SERVER spans exported"
    );
}

// Work outside any request: a PocApp root activity and an HttpClient call with no current activity.
var (backgroundDescription, backgroundRecorded) = await Task.Run(() =>
{
    Activity.Current = null;
    using var background = appSource.StartActivity("background");
    return (
        background is null
            ? "null"
            : $"Recorded={background.Recorded} IsAllDataRequested={background.IsAllDataRequested}",
        background?.Recorded ?? false
    );
});
Console.WriteLine($"Outside-request PocApp root activity: {backgroundDescription}");
Check(!backgroundRecorded, "outside request: PocApp root activity is null or not recorded");
await Task.Run(async () =>
{
    Activity.Current = null;
    await outgoingClient.GetStringAsync($"http://localhost:{port}/sink?id=outside");
});
var outsideTraceparent = sinkTraceparents["outside"];
Console.WriteLine($"Outside-request call: sink received traceparent '{outsideTraceparent}'");
Check(
    outsideTraceparent.Length == 0 || outsideTraceparent.EndsWith("-00"),
    "outside request: propagated context (if any) is unsampled"
);
bool IsOutsideSink(Activity activity) =>
    activity.Kind == ActivityKind.Server
    && (string?)activity.GetTagItem("url.path") == "/sink"
    && !apiTraceIds.ContainsValue(activity.TraceId);
await WaitUntil(
    () => exported.Snapshot().Any(IsOutsideSink),
    "outside request: sink SERVER span exported"
);

// Dispose everything first so late-ending activities (for example connection lifetimes) are exported.
await app.StopAsync();
await app.DisposeAsync();
outgoingClient.Dispose();
tracerProvider.Dispose();
var spans = exported.Snapshot();

Console.WriteLine();
Console.WriteLine("Sampler calls (name | kind | parent | decision): count");
foreach (var group in sampler.Calls.GroupBy(call => call).OrderBy(group => group.Key.ToString()))
    Console.WriteLine($"  {group.Key}: {group.Count()}");

// Assumption 1: sampling-time name of every SERVER activity.
var serverSamplerCalls = sampler.Calls.Where(call => call.Kind == ActivityKind.Server).ToList();
Check(
    serverSamplerCalls.Count == 7
        && serverSamplerCalls.All(call => call.Name == HostingActivityName),
    $"A1: all {serverSamplerCalls.Count} SERVER sampler calls (expected 7) use Name {HostingActivityName}"
);
Check(
    spans
        .Where(span => span.Kind == ActivityKind.Server)
        .All(span => span.OperationName == HostingActivityName),
    "A1: all exported SERVER spans have OperationName " + HostingActivityName
);

Console.WriteLine();
foreach (var (id, traceparent) in calls)
{
    var traceSpans = spans.Where(span => span.TraceId == apiTraceIds[id]).ToList();
    var apiServers = traceSpans.Where(span => IsServer(span, "/api")).ToList();
    var sinkServers = traceSpans.Where(span => IsServer(span, "/sink")).ToList();
    var httpClients = traceSpans
        .Where(span =>
            span.Kind == ActivityKind.Client && span.OperationName == HttpClientActivityName
        )
        .ToList();
    var clientKind = traceSpans.Count(span => span.Kind == ActivityKind.Client);
    var systemNetHttp = traceSpans.Count(span => span.Source.Name == "System.Net.Http");
    Console.WriteLine(
        $"CALL {id}: http_client_spans={httpClients.Count} client_kind_spans={clientKind} "
            + $"system_net_http_source_spans={systemNetHttp} total_spans={traceSpans.Count}"
    );
    foreach (var span in traceSpans.OrderBy(span => span.StartTimeUtc))
        Console.WriteLine(
            $"  span source={span.Source.Name} name={span.OperationName} display={span.DisplayName} "
                + $"kind={span.Kind} id={span.SpanId} parent={span.ParentSpanId} recorded={span.Recorded}"
        );

    Check(
        apiServers.Count == 1 && sinkServers.Count == 1,
        $"A5 {id}: exactly one SERVER span per incoming request"
    );
    Check(httpClients.Count == 1, $"Q {id}: exactly one HTTP CLIENT span for the outgoing call");
    Check(clientKind == 1, $"Q {id}: exactly one Client-kind span in the trace");
    if (apiServers.Count != 1)
        continue;
    var server = apiServers[0];
    Check(server.Recorded, $"A2 {id}: SERVER activity recorded");
    if (traceparent is not null)
        Check(
            server.HasRemoteParent
                && server.ParentSpanId
                    == (id == "remote-unsampled" ? remoteParentUnsampled : remoteParentSampled),
            $"A2 {id}: SERVER parented to the remote traceparent"
        );
    foreach (
        var (source, name) in new[] { ("PocApp", "app-work"), ("apitally.otel", "apitally-work") }
    )
    {
        var children = traceSpans
            .Where(span => span.Source.Name == source && span.OperationName == name)
            .ToList();
        Check(
            children.Count == 1
                && children[0].Recorded
                && children[0].ParentSpanId == server.SpanId,
            $"A3 {id}: {source} child recorded and parented to SERVER"
        );
    }
    if (httpClients.Count == 1 && sinkServers.Count == 1)
        Check(
            httpClients[0].ParentSpanId == server.SpanId
                && sinkServers[0].ParentSpanId == httpClients[0].SpanId,
            $"Q {id}: SERVER -> CLIENT -> sink SERVER parentage"
        );
    var extras = traceSpans
        .Except(apiServers)
        .Except(sinkServers)
        .Except(httpClients)
        .Where(span => span.Source.Name is not ("PocApp" or "apitally.otel"))
        .GroupBy(span => $"{span.Source.Name}/{span.OperationName}/{span.Kind}")
        .Select(group => $"{group.Key} x{group.Count()}");
    Console.WriteLine($"EXTRA {id}: [{string.Join(", ", extras)}]");
}

var outsideSpans = spans.Where(span => !apiTraceIds.ContainsValue(span.TraceId)).ToList();
foreach (var span in outsideSpans)
    Console.WriteLine(
        $"  outside span source={span.Source.Name} name={span.OperationName} kind={span.Kind} parent={span.ParentSpanId}"
    );
Check(
    outsideSpans.Count == 1 && IsOutsideSink(outsideSpans[0]),
    "A4: outside requests the only exported span is the sink's own incoming SERVER span"
);
Check(
    outsideSpans.Count(IsOutsideSink) == 1,
    "A2/A5 outside: sink SERVER recorded once despite unsampled parent"
);

Console.WriteLine(
    failures.Count == 0
        ? "RESULT PASS"
        : $"RESULT FAIL ({failures.Count}): {string.Join("; ", failures)}"
);
return failures.Count == 0 ? 0 : 1;

static bool IsServer(Activity span, string path) =>
    span.Kind == ActivityKind.Server && (string?)span.GetTagItem("url.path") == path;

static int FreeLoopbackPort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

static async Task<string> RawGet(int port, string path, string? traceparent)
{
    using var tcp = new TcpClient();
    await tcp.ConnectAsync(IPAddress.Loopback, port);
    var stream = tcp.GetStream();
    var request =
        $"GET {path} HTTP/1.1\r\nHost: localhost:{port}\r\nConnection: close\r\n"
        + (traceparent is null ? "" : $"traceparent: {traceparent}\r\n")
        + "\r\n";
    await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
    using var reader = new StreamReader(stream);
    return await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10));
}

static async Task WaitUntil(Func<bool> condition, string description)
{
    var deadline = DateTime.UtcNow.AddSeconds(10);
    while (!condition())
    {
        if (DateTime.UtcNow > deadline)
            throw new TimeoutException(description);
        await Task.Delay(10);
    }
}

sealed record SamplerCall(string Name, ActivityKind Kind, string Parent, SamplingDecision Decision);

sealed class RequestSampler : Sampler
{
    public readonly ConcurrentQueue<SamplerCall> Calls = new();

    public override SamplingResult ShouldSample(in SamplingParameters parameters)
    {
        var parent = parameters.ParentContext;
        var sample =
            parameters.Name == "Microsoft.AspNetCore.Hosting.HttpRequestIn"
            || (!parent.IsRemote && parent.TraceFlags.HasFlag(ActivityTraceFlags.Recorded));
        var decision = sample ? SamplingDecision.RecordAndSample : SamplingDecision.Drop;
        var parentDescription =
            parent.TraceId == default ? "root"
            : parent.IsRemote ? $"remote/{parent.TraceFlags}"
            : $"local/{parent.TraceFlags}";
        Calls.Enqueue(
            new SamplerCall(parameters.Name, parameters.Kind, parentDescription, decision)
        );
        return new SamplingResult(decision);
    }
}

sealed class ExportedActivities : Collection<Activity>
{
    protected override void InsertItem(int index, Activity item)
    {
        lock (this)
            base.InsertItem(index, item);
    }

    public List<Activity> Snapshot()
    {
        lock (this)
            return this.ToList();
    }
}
