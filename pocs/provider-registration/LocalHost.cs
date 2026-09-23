using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using OpenTelemetry;

namespace ProviderRegistration;

internal sealed class LocalHost : IAsyncDisposable
{
    public static readonly ActivitySource Manual = new("apitally.otel");
    public static readonly HttpClient TestClient = new(
        new SocketsHttpHandler { ActivityHeadersPropagator = null, UseProxy = false }
    )
    {
        Timeout = TimeSpan.FromSeconds(5),
    };

    private readonly WebApplication app;
    private bool disposed;

    private LocalHost(WebApplication app) => this.app = app;

    public IServiceProvider Services => app.Services;
    public string Address =>
        app
            .Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.Single();

    public static async Task<LocalHost> Start(
        Action<IServiceCollection>? register = null,
        string? downstream = null,
        bool earlyRequest = false,
        Func<Task>? overlap = null
    )
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.Configure<HostOptions>(options =>
            options.ShutdownTimeout = TimeSpan.FromSeconds(5)
        );
        register?.Invoke(builder.Services);
        if (earlyRequest)
            builder.Services.AddHostedService<EarlyRequest>();
        var app = builder.Build();
        app.MapGet(
            "/work/{id}",
            async () =>
            {
                using var child = Manual.StartActivity("manual-child");
                if (overlap is not null)
                    await overlap().WaitAsync(TimeSpan.FromSeconds(5));
                return "ok";
            }
        );
        app.MapGet("/plain/{id}", () => "ok");
        app.MapGet("/sink", () => "ok");
        app.MapGet(
            "/outgoing",
            async () =>
            {
                using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
                {
                    Timeout = TimeSpan.FromSeconds(5),
                };
                return await client.GetStringAsync(downstream + "/sink");
            }
        );
        var host = new LocalHost(app);
        try
        {
            await app.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    public Task Request(string path, bool unsampledParent = false) =>
        Send(Address + path, unsampledParent);

    public static async Task Send(string url, bool unsampledParent = false)
    {
        using var suppression = SuppressInstrumentationScope.Begin();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (unsampledParent)
            request.Headers.Add(
                "traceparent",
                "00-11111111111111111111111111111111-2222222222222222-00"
            );
        using var response = await TestClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        Program.Check(await response.Content.ReadAsStringAsync() == "ok", "response completed");
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        disposed = true;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await app.StopAsync(timeout.Token);
        }
        finally
        {
            await app.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}

// StartedAsync runs after Kestrel starts and before ApplicationStarted is signaled.
internal sealed class EarlyRequest(IServer server, IHostApplicationLifetime lifetime)
    : IHostedLifecycleService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StartedAsync(CancellationToken cancellationToken)
    {
        Program.Check(
            !lifetime.ApplicationStarted.IsCancellationRequested,
            "early request precedes ApplicationStarted"
        );
        var address = server.Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        await LocalHost.Send(address + "/work/first", unsampledParent: true);
    }
}
