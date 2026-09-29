using Apitally.Hosting;
using Apitally.TestApp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace Apitally.Tests.Support;

// A real Kestrel host of the test application on a loopback port, exporting to a receiver.
internal sealed class ApplicationHost : IAsyncDisposable
{
    private readonly IHost host;
    private readonly EnvironmentVariables environment;
    private bool stopped;

    private ApplicationHost(IHost host, EnvironmentVariables environment, FakeLogCollector logs)
    {
        this.host = host;
        this.environment = environment;
        Logs = logs;
    }

    public HttpClient Client { get; private set; } = null!;

    // Application logs, including Apitally's own diagnostics.
    public FakeLogCollector Logs { get; }
    public IServiceProvider Services => host.Services;

    // Default logging providers stay registered, but silent, alongside the in-memory one.
    public static string[] Arguments(params string[] extra) =>
        [
            "--urls=http://127.0.0.1:0",
            "--environment=Production",
            $"--Apitally:WriteToken={TestConfiguration.WriteToken}",
            "--Logging:Console:LogLevel:Default=None",
            "--Logging:Debug:LogLevel:Default=None",
            "--Logging:EventSource:LogLevel:Default=None",
            "--Logging:EventLog:LogLevel:Default=None",
            .. extra,
        ];

    public static Task<ApplicationHost> StartMinimalAsync(
        OtlpReceiver receiver,
        Action<WebApplicationBuilder>? configure = null,
        Action<WebApplication>? configureApp = null,
        params string[] arguments
    )
    {
        var environment = Environment(receiver);
        var logs = new FakeLogCollector();
        var app = Program.CreateMinimalApp(
            Arguments(arguments),
            builder =>
            {
                builder.Logging.AddProvider(new FakeLoggerProvider(logs));
                configure?.Invoke(builder);
            }
        );
        configureApp?.Invoke(app);
        return StartAsync(new ApplicationHost(app, environment, logs));
    }

    public static Task<ApplicationHost> StartStartupAsync(
        OtlpReceiver receiver,
        Action<IHostBuilder>? configure = null
    )
    {
        var environment = Environment(receiver);
        var logs = new FakeLogCollector();
        var builder = Program
            .CreateStartupHostBuilder(Arguments())
            .ConfigureLogging(logging => logging.AddProvider(new FakeLoggerProvider(logs)));
        configure?.Invoke(builder);
        return StartAsync(new ApplicationHost(builder.Build(), environment, logs));
    }

    // Stopping runs Apitally's final export cycle, so all telemetry has been delivered after it.
    // Stopping the server first drains requests, which must all have been released before
    // Apitally's shutdown discards any that remain.
    public async Task StopAsync()
    {
        if (stopped)
            return;
        stopped = true;
        await host.Services.GetRequiredService<IServer>().StopAsync(CancellationToken.None);
        var registry = host.Services.GetService<TelemetryRuntime>()?.Registry;
        Assert.True(registry?.IsEmpty ?? true, "Requests were not released from the registry.");
        await host.StopAsync();
        Client.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        host.Dispose();
        environment.Dispose();
    }

    private static async Task<ApplicationHost> StartAsync(ApplicationHost applicationHost)
    {
        await applicationHost.host.StartAsync();
        var address = applicationHost
            .host.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.First();
        // Test requests carry no trace context unless a test sets traceparent explicitly.
        applicationHost.Client = new HttpClient(
            new SocketsHttpHandler { ActivityHeadersPropagator = null }
        )
        {
            BaseAddress = new Uri(address),
        };
        return applicationHost;
    }

    private static EnvironmentVariables Environment(OtlpReceiver receiver) =>
        new(("APITALLY_OTLP_ENDPOINT", receiver.Endpoint.ToString()));
}
