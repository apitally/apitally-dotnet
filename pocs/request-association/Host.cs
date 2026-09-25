using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace RequestAssociation;

internal sealed class Fixture(Probe probe)
{
    public Probe Probe { get; } = probe;
    public ConcurrentDictionary<string, WorkGate> Gates { get; } = new();
    public ConcurrentDictionary<string, LateWork> Late { get; } = new();
    public ConcurrentQueue<ExpectedSpan> Started { get; } = new();
    public ConcurrentQueue<ExpectedLog> Logs { get; } = new();
    public ConcurrentQueue<bool> BeforeMiddleware { get; } = new();
    public ConcurrentQueue<bool> ExplicitParentsNull { get; } = new();
    public string Address { get; set; } = "";
    public bool EarlyBeforeStarted;
    public bool RemoteUnsampled;
    public bool Outgoing = true;

    public void Record(Activity? activity, string request)
    {
        if (activity is not null)
            Started.Enqueue(
                new ExpectedSpan(
                    request,
                    activity.DisplayName,
                    activity.TraceId,
                    activity.SpanId,
                    activity.ParentSpanId,
                    activity.Recorded
                )
            );
    }

    public void RecordClient(Activity activity, HttpRequestMessage request)
    {
        var id = request.RequestUri!.AbsolutePath.Split('/').Last();
        if (id.EndsWith("-sink"))
            Record(activity, id[..^5]);
    }

    public void Log(ILogger logger, string name)
    {
        Logs.Enqueue(
            new ExpectedLog(
                name,
                Activity.Current?.TraceId ?? default,
                Activity.Current?.SpanId ?? default
            )
        );
        logger.LogInformation("event {event} secret {secret}", name, "private-value");
    }
}

internal sealed record ExpectedSpan(
    string Request,
    string Name,
    ActivityTraceId TraceId,
    ActivitySpanId SpanId,
    ActivitySpanId ParentSpanId,
    bool Recorded
);

internal sealed record ExpectedLog(string Name, ActivityTraceId TraceId, ActivitySpanId SpanId);

internal sealed class WorkGate
{
    public TaskCompletionSource Entered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Continue { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class LateWork
{
    public TaskCompletionSource Started { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Continue { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Task { get; set; } = Task.CompletedTask;
}

internal sealed class LocalHost : IAsyncDisposable
{
    public const string SourceName = "RequestAssociation.Manual";
    public static readonly ActivitySource Manual = new(SourceName);
    private readonly WebApplication app;
    private readonly HttpClient client;
    private readonly HttpClient outgoing;
    private bool disposed;

    private LocalHost(WebApplication app, HttpClient outgoing)
    {
        this.app = app;
        this.outgoing = outgoing;
        client = new HttpClient(
            new SocketsHttpHandler
            {
                UseProxy = false,
                ActivityHeadersPropagator = null,
                MaxConnectionsPerServer = 1,
            }
        )
        {
            Timeout = TimeSpan.FromSeconds(5),
        };
    }

    public IServiceProvider Services => app.Services;
    public string Address =>
        app
            .Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.Single();

    public static async Task<LocalHost> Start(
        Probe probe,
        Fixture fixture,
        AppExporter appExporter,
        AppLogs appLogs,
        string registration,
        SamplingDecision sampling = SamplingDecision.RecordAndSample,
        TracerProvider? existing = null
    )
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.Configure<HostOptions>(options =>
            options.ShutdownTimeout = TimeSpan.FromSeconds(5)
        );
        builder.Services.AddSingleton(fixture);
        builder.Services.AddHostedService<EarlyRequest>();
        if (registration == "user-first")
            RegisterUser(builder.Services, fixture, appExporter, sampling);
        if (registration == "existing")
            builder.Services.AddSingleton<TracerProvider>(existing!);
        Candidate.Register(builder.Services, probe);
        if (registration == "candidate-first")
            RegisterUser(builder.Services, fixture, appExporter, sampling);
        builder.Logging.AddProvider(appLogs);
        var outgoing = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(5),
        };
        var app = builder.Build();
        app.Use(
            async (context, next) =>
            {
                context.Items["middleware"] = true;
                await next(context);
            }
        );
        app.MapGet(
            "/{mode}/{id}",
            async (
                HttpContext context,
                RequestHelpers helpers,
                ILoggerFactory factory,
                string mode,
                string id
            ) =>
            {
                var logger = factory.CreateLogger(CaptureAdapter.Category);
                if (mode == "sink")
                    return Results.Text("ok");
                var server = Activity.Current;
                using (var child = Manual.StartActivity("child-" + id))
                {
                    fixture.Record(child, id);
                    if (fixture.Gates.TryGetValue(id, out var gate))
                    {
                        gate.Entered.TrySetResult();
                        await gate.Continue.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    await Task.Yield();
                    using (var nested = Manual.StartActivity("nested-" + id))
                    {
                        fixture.Record(nested, id);
                        helpers.SetConsumer("consumer-" + id);
                        helpers.SetAttribute("id", id);
                        helpers.CaptureException(new InvalidOperationException("first-" + id));
                        helpers.CaptureException(new ApplicationException("second-" + id));
                        fixture.Log(logger, "nested-" + id);
                    }
                    using (
                        var explicitChild = Manual.StartActivity(
                            "ids-" + id,
                            ActivityKind.Internal,
                            server?.Context ?? default
                        )
                    )
                    {
                        fixture.Record(explicitChild, id);
                        if (explicitChild is not null)
                            fixture.ExplicitParentsNull.Enqueue(explicitChild.Parent is null);
                        fixture.Log(logger, "ids-" + id);
                    }
                    if (fixture.Outgoing)
                    {
                        var address = context.Request.Scheme + "://" + context.Request.Host;
                        Program.Check(
                            await outgoing.GetStringAsync(address + "/sink/" + id + "-sink")
                                == "ok",
                            "outgoing instrumented loopback request"
                        );
                    }
                    if (fixture.Late.TryGetValue(id, out var late))
                    {
                        late.Task = Task.Run(async () =>
                        {
                            using var activity = Manual.StartActivity(
                                "late-" + id,
                                ActivityKind.Internal,
                                server?.Context ?? default
                            );
                            fixture.Record(activity, id);
                            fixture.Log(logger, "late-live-" + id);
                            late.Started.TrySetResult();
                            await late.Continue.Task.WaitAsync(TimeSpan.FromSeconds(5));
                            fixture.Log(logger, "late-after-" + id);
                        });
                        await late.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    fixture.Log(logger, "child-" + id);
                }
                fixture.Log(logger, "server-" + id);
                return Results.Text("ok", statusCode: 202);
            }
        );
        var host = new LocalHost(app, outgoing);
        try
        {
            await app.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
            fixture.Address = host.Address;
            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    public static TracerProvider BuildExisting(Fixture fixture, AppExporter exporter) =>
        ConfigureUser(
                Sdk.CreateTracerProviderBuilder(),
                fixture,
                exporter,
                SamplingDecision.RecordAndSample
            )
            .Build();

    private static void RegisterUser(
        IServiceCollection services,
        Fixture fixture,
        AppExporter exporter,
        SamplingDecision sampling
    ) =>
        services
            .AddOpenTelemetry()
            .WithTracing(builder => ConfigureUser(builder, fixture, exporter, sampling));

    private static TracerProviderBuilder ConfigureUser(
        TracerProviderBuilder builder,
        Fixture fixture,
        AppExporter exporter,
        SamplingDecision sampling
    ) =>
        builder
            .SetResourceBuilder(
                ResourceBuilder
                    .CreateEmpty()
                    .AddService("independent-app")
                    .AddAttributes([new KeyValuePair<string, object>("app.resource", "untouched")])
            )
            .SetSampler(
                fixture.RemoteUnsampled ? new ParentBasedSampler(new AlwaysOnSampler())
                : sampling == SamplingDecision.Drop ? new AlwaysOffSampler()
                : new FixedSampler(sampling)
            )
            .AddAspNetCoreInstrumentation(options => options.EnrichWithHttpRequest = Enrich)
            .AddHttpClientInstrumentation(options =>
                options.EnrichWithHttpRequestMessage = fixture.RecordClient
            )
            .AddSource(SourceName)
            .AddProcessor(new SimpleActivityExportProcessor(exporter));

    public static void Enrich(Activity activity, HttpRequest request)
    {
        var fixture = request.HttpContext.RequestServices.GetRequiredService<Fixture>();
        fixture.Probe.Guard(() =>
        {
            activity.SetTag("user.enrichment", "untouched");
            if (request.Path.StartsWithSegments("/sink"))
                return;
            var id = request.Path.Value!.Split('/').Last();
            fixture.BeforeMiddleware.Enqueue(!request.HttpContext.Items.ContainsKey("middleware"));
            using var child = Manual.StartActivity("before-" + id);
            fixture.Record(child, id);
            fixture.Log(
                request
                    .HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger(CaptureAdapter.Category),
                "before-" + id
            );
        });
    }

    public async Task Request(string path, string? trace = null)
    {
        await Send(client, Address + path, trace).WaitAsync(TimeSpan.FromSeconds(7));
    }

    public static async Task Send(HttpClient client, string address, string? trace)
    {
        using var suppression = SuppressInstrumentationScope.Begin();
        using var request = new HttpRequestMessage(HttpMethod.Get, address)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        request.Headers.UserAgent.ParseAdd("request-association-poc/1");
        if (trace is not null)
            request.Headers.Add("traceparent", trace);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        Program.Check(response.Version == HttpVersion.Version11, "HTTP/1.1 response");
        Program.Check(await response.Content.ReadAsStringAsync() == "ok", "response body");
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        disposed = true;
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await app.StopAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(7));
        }
        finally
        {
            await app.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            client.Dispose();
            outgoing.Dispose();
        }
    }
}

internal sealed class EarlyRequest(
    IServer server,
    IHostApplicationLifetime lifetime,
    Fixture fixture
) : IHostedLifecycleService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StartedAsync(CancellationToken cancellationToken)
    {
        fixture.EarlyBeforeStarted = !lifetime.ApplicationStarted.IsCancellationRequested;
        using var client = new HttpClient(
            new SocketsHttpHandler { UseProxy = false, ActivityHeadersPropagator = null }
        )
        {
            Timeout = TimeSpan.FromSeconds(5),
        };
        var address = server.Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        await LocalHost
            .Send(
                client,
                address + "/keep/first",
                fixture.RemoteUnsampled
                    ? "00-11111111111111111111111111111111-2222222222222222-00"
                    : null
            )
            .WaitAsync(TimeSpan.FromSeconds(7), cancellationToken);
    }
}

internal sealed class FixedSampler(SamplingDecision decision) : Sampler
{
    public override SamplingResult ShouldSample(in SamplingParameters samplingParameters) =>
        new(decision);
}
