using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Sentry;
using Sentry.AspNetCore;
using Sentry.Extensibility;
using Sentry.Protocol.Envelopes;

Console.WriteLine($"Sentry runtime={Environment.Version} Sentry.AspNetCore=6.11.1 Sentry=6.11.1");
foreach (var viaOptions in new[] { false, true })
foreach (var registerFirst in new[] { false, true })
    await Probe.Run(viaOptions, registerFirst);
await Probe.RunDirectClient();
Console.WriteLine("PASS Sentry (fake transport only)");

static class Probe
{
    public static async Task Run(bool viaOptions, bool registerFirst)
    {
        var transport = new MemoryTransport();
        var processor = new CorrelatingProcessor();
        var requests = new ConcurrentDictionary<string, PendingLink>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.GetCustomProperty("poc.pending") is PendingLink pending)
                {
                    pending.ServerEnded = true;
                    pending.Stopped.TrySetResult();
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        var builder = WebApplication.CreateEmptyBuilder(
            new WebApplicationOptions { Args = [], EnvironmentName = "Production" }
        );
        builder.Configuration.AddInMemoryCollection();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddRouting();
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<SyntheticHandler>();
        if (registerFirst)
            Register(builder.Services, processor, viaOptions);
        builder.WebHost.UseSentry(options =>
        {
            Configure(options, transport);
            options.MinimumEventLevel = LogLevel.None;
            options.MinimumBreadcrumbLevel = LogLevel.None;
            options.AutoRegisterTracing = false;
        });
        if (!registerFirst)
            Register(builder.Services, processor, viaOptions);
        await using var app = builder.Build();
        app.Use(
            async (context, next) =>
            {
                await next(context);
                if (context.Features.Get<IExceptionHandlerFeature>() is { } feature)
                    Begin((string)feature.RouteValues!["id"]!, feature.Error, processor, requests);
            }
        );
        app.UseExceptionHandler();
        app.MapGet(
            "/auto/{id}",
            (string id) => Throw(new InvalidOperationException("synthetic auto " + id))
        );
        app.MapGet(
            "/late/{id}",
            (string id) =>
            {
                Begin(
                    id,
                    new InvalidOperationException("synthetic late " + id),
                    processor,
                    requests
                );
                return Results.StatusCode(500);
            }
        );
        await app.StartAsync();
        using var http = new HttpClient
        {
            BaseAddress = new Uri(app.Urls.Single()),
            Timeout = TimeSpan.FromSeconds(15),
        };
        await Task.WhenAll(
            new[] { "a", "b" }.Select(async id =>
            {
                using var response = await http.GetAsync("/auto/" + id);
                Assert((int)response.StatusCode == 500, "handled final 500");
                await requests[id].Stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert(
                    requests[id].EventId.Length == 32,
                    "automatic handled exception event linked"
                );
                Assert(
                    requests[id].ProcessedAfterEnd == false,
                    "automatic capture before SERVER Activity end"
                );
            })
        );
        Assert(
            requests["a"].EventId != requests["b"].EventId,
            "concurrent request IDs remain distinct"
        );
        using (var response = await http.GetAsync("/late/c"))
            Assert((int)response.StatusCode == 500, "late fixture response");
        var late = requests["c"];
        await late.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert(Activity.Current == null, "late capture outside request Activity");
        var returnedId = SentrySdk.CaptureException(late.Exception);
        Assert(returnedId != SentryId.Empty, "late event accepted");
        if (viaOptions)
        {
            Assert(
                late.EventId == returnedId.ToString() && late.ProcessedAfterEnd == true,
                "options processor links event after SERVER Activity end by retained exception association"
            );
        }
        else
            Assert(
                late.EventId == "",
                "DI processor is scoped to Sentry middleware and does not cover out-of-request capture"
            );
        var callsBeforeMessage = processor.Linked;
        SentrySdk.CaptureMessage("synthetic non-exception message");
        Assert(processor.Linked == callsBeforeMessage, "messages do not enrich exception IDs");
        await SentrySdk.FlushAsync(TimeSpan.FromSeconds(10));
        Assert(
            transport.EventIds.Contains(requests["a"].EventId)
                && transport.EventIds.Contains(requests["b"].EventId),
            "fake transport received automatic events"
        );
        Assert(
            transport.EventIds.Contains(returnedId.ToString()),
            "fake transport received late event"
        );
        Console.WriteLine(
            JsonSerializer.Serialize(
                new
                {
                    viaOptions,
                    registerFirst,
                    beforeEnd = 2,
                    afterEndLinked = late.EventId.Length > 0,
                    processor.Linked,
                    fakeEvents = transport.EventIds.Count,
                    concurrentDistinct = true,
                    serverActivityKind = ActivityKind.Server.ToString(),
                }
            )
        );
        await app.StopAsync();
        SentrySdk.Close();
    }

    public static async Task RunDirectClient()
    {
        var transport = new MemoryTransport();
        var processor = new CorrelatingProcessor();
        var options = new SentryOptions();
        Configure(options, transport);
        options.AddEventProcessor(processor);
        options.SetBeforeSend(@event => null);
        using var client = new SentryClient(options);
        var exception = new InvalidOperationException("synthetic dropped event");
        var pending = new PendingLink(exception, "no-span") { ServerEnded = true };
        processor.Register(exception, pending);
        var returnedId = client.CaptureEvent(new SentryEvent(exception));
        await client.FlushAsync(TimeSpan.FromSeconds(10));
        Assert(
            pending.EventId.Length == 32
                && returnedId == SentryId.Empty
                && transport.EventIds.Count == 0,
            "event processor runs before BeforeSend: ID observation is not delivery confirmation"
        );
        Console.WriteLine(
            "NEGATIVE processor observed an ID for an event dropped later by BeforeSend; fake transport events=0"
        );
    }

    private static void Register(
        IServiceCollection services,
        CorrelatingProcessor processor,
        bool viaOptions
    )
    {
        if (viaOptions)
            services.PostConfigure<SentryAspNetCoreOptions>(options =>
                options.AddEventProcessor(processor)
            );
        else
            services.AddSingleton<ISentryEventProcessor>(processor);
    }

    private static void Configure(SentryOptions options, MemoryTransport transport)
    {
        options.Dsn = "https://public@example.invalid/1";
        options.Transport = transport;
        options.CacheDirectoryPath = null;
        options.Environment = "poc";
        options.Release = "poc@1.0.0";
        options.ServerName = "synthetic";
        options.SendDefaultPii = false;
        options.IsEnvironmentUser = false;
        options.AutoSessionTracking = false;
        options.TracesSampleRate = 0;
        options.DisableSentryHttpMessageHandler = true;
        options.DisableAppDomainUnhandledExceptionCapture();
        options.DisableUnobservedTaskExceptionCapture();
        options.DisableDiagnosticSourceIntegration();
        options.DisableAppDomainProcessExitFlush();
    }

    private static void Begin(
        string id,
        Exception exception,
        CorrelatingProcessor processor,
        ConcurrentDictionary<string, PendingLink> requests
    )
    {
        var server = Activity.Current;
        while (server != null && server.Kind != ActivityKind.Server)
            server = server.Parent;
        Assert(server != null, "real Kestrel SERVER Activity available");
        var pending = new PendingLink(exception, server!.SpanId.ToString());
        requests[id] = pending;
        processor.Register(exception, pending);
        server.SetCustomProperty("poc.pending", pending);
    }

    private static IResult Throw(Exception exception) => throw exception;

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("FAIL: " + message);
    }
}

public sealed class SyntheticHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context,
        Exception exception,
        CancellationToken cancellationToken
    )
    {
        context.Response.StatusCode = 500;
        await context.Response.WriteAsync("synthetic response", cancellationToken);
        return true;
    }
}

public sealed class CorrelatingProcessor : ISentryEventProcessor
{
    private readonly ConditionalWeakTable<Exception, PendingLink> pending = new();
    public int Linked;

    public void Register(Exception exception, PendingLink link) => pending.Add(exception, link);

    public SentryEvent? Process(SentryEvent @event)
    {
        if (@event.Exception != null && pending.TryGetValue(@event.Exception, out var link))
        {
            link.EventId = @event.EventId.ToString();
            link.ProcessedAfterEnd = link.ServerEnded;
            Interlocked.Increment(ref Linked);
        }
        return @event;
    }
}

public sealed class PendingLink(Exception exception, string spanId)
{
    public Exception Exception { get; } = exception;
    public string SpanId { get; } = spanId;
    public volatile bool ServerEnded;
    public string EventId = "";
    public bool? ProcessedAfterEnd;
    public TaskCompletionSource Stopped { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class MemoryTransport : ITransport
{
    public ConcurrentBag<string> EventIds { get; } = [];

    public Task SendEnvelopeAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        if (envelope.Header.TryGetValue("event_id", out var id) && id != null)
            EventIds.Add(id.ToString()!);
        return Task.CompletedTask;
    }
}
