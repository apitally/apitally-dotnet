using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Apitally.Hosting;
using Apitally.TestApp;
using Apitally.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace Apitally.Tests.Hosting;

public class TelemetryRuntimeTests
{
    [Fact]
    public async Task StartedHostDeliversStartupEventAndProcessMetrics()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        await using var host = await ApplicationHost.StartMinimalAsync(
            receiver,
            builder => builder.Services.AddApitally(options => options.AppVersion = "1.2.3")
        );

        await host.StopAsync();

        var startup = Assert.Single(receiver.Events("apitally.app.startup"));
        Assert.True(startup.TraceId.IsEmpty);
        var payload = JsonDocument.Parse(startup.Body.StringValue).RootElement;
        Assert.Equal("aspnetcore", payload.GetProperty("framework").GetString());
        Assert.Equal("1.2.3", payload.GetProperty("versions").GetProperty("app").GetString());
        Assert.Equal(
            Environment.Version.ToString(),
            payload.GetProperty("versions").GetProperty("dotnet").GetString()
        );
        Assert.False(payload.TryGetProperty("openapi", out _));
        var paths = payload
            .GetProperty("paths")
            .EnumerateArray()
            .Select(path =>
                (path.GetProperty("method").GetString(), path.GetProperty("path").GetString())
            )
            .ToList();
        Assert.Contains(("GET", "/items/{id:int}"), paths);
        Assert.Contains(("POST", "/items"), paths);
        Assert.Contains(("GET", "/api/v1/orders/{orderId}"), paths);
        Assert.Contains(("GET", "/api/v1"), paths);
        Assert.Contains(("GET", "/controller/items/{id:int}"), paths);
        var hello = payload
            .GetProperty("paths")
            .EnumerateArray()
            .Single(p => p.GetProperty("path").GetString() == "/hello");
        Assert.Equal("Says hello", hello.GetProperty("description").GetString());
        Assert.Single(receiver.Metrics("process.uptime"));
        Assert.Single(receiver.Metrics("process.memory.usage"));
        Assert.Single(receiver.Metrics("process.cpu.utilization"));
        var resource = OtlpDecoding.Attributes(receiver.ResourceMetrics()[0].Resource.Attributes);
        Assert.Equal("dev", resource["deployment.environment.name"]);
        Assert.Equal("apitally-dotnet", resource["telemetry.distro.name"]);
        Assert.All(receiver.Exports, export => Assert.Equal("dev", export.Env));
    }

    [Fact]
    public async Task StartupEventConfigOmitsCredentialsAndMarksCallbacks()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        await using var host = await ApplicationHost.StartMinimalAsync(
            receiver,
            builder =>
                builder.Services.AddApitally(options =>
                {
                    options.Env = "prod";
                    options.SampleOnResponse = _ => null;
                    options.MaskHeaders = ["x-internal", "(?-i:X-Case)"];
                })
        );

        await host.StopAsync();

        var startup = Assert.Single(receiver.Events("apitally.app.startup"));
        var config = JsonDocument.Parse(startup.Body.StringValue).RootElement.GetProperty("config");
        var keys = config.EnumerateObject().Select(property => property.Name).ToList();
        Assert.DoesNotContain("WriteToken", keys);
        Assert.DoesNotContain("Env", keys);
        Assert.DoesNotContain("Disabled", keys);
        Assert.DoesNotContain("AppVersion", keys);
        Assert.DoesNotContain("SampleOnRequest", keys);
        Assert.True(config.GetProperty("SampleOnResponse").GetBoolean());
        Assert.True(config.GetProperty("CaptureLogs").GetBoolean());
        Assert.Equal(1.0, config.GetProperty("SampleRate").GetDouble());
        Assert.Equal(
            ["(?i)x-internal", "(?i)(?-i:X-Case)"],
            config.GetProperty("MaskHeaders").EnumerateArray().Select(value => value.GetString())
        );
        Assert.All(receiver.Exports, export => Assert.Equal("prod", export.Env));
    }

    [Fact]
    public async Task StartupHostDeliversStartupEvent()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        await using var host = await ApplicationHost.StartStartupAsync(receiver);

        await host.StopAsync();

        var startup = Assert.Single(receiver.Events("apitally.app.startup"));
        Assert.Contains("/controller/items/{id:int}", startup.Body.StringValue);
        Assert.Single(receiver.Metrics("process.uptime"));
    }

    [Fact]
    public async Task DisabledHostExportsNothing()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        await using var host = await ApplicationHost.StartMinimalAsync(
            receiver,
            builder => builder.Services.AddApitally(options => options.Disabled = true)
        );

        (await host.Client.GetAsync("/hello")).EnsureSuccessStatusCode();
        await host.StopAsync();

        Assert.Empty(receiver.Exports);
    }

    [Fact]
    public async Task BuiltButUnstartedHostNeverActivates()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        using var environment = new EnvironmentVariables(
            ("APITALLY_OTLP_ENDPOINT", receiver.Endpoint.ToString())
        );

        await using (var app = Program.CreateMinimalApp(ApplicationHost.Arguments()))
        {
            Assert.False(app.Services.GetRequiredService<TelemetryRuntime>().IsPrepared);
        }

        Assert.Empty(receiver.Exports);
    }

    [Fact]
    public async Task TestServerHostsAreSuppressed()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        using var environment = new EnvironmentVariables(
            ("APITALLY_OTLP_ENDPOINT", receiver.Endpoint.ToString())
        );
        await using var app = Program.CreateMinimalApp(
            ApplicationHost.Arguments(),
            builder => builder.WebHost.UseTestServer()
        );

        await app.StartAsync();
        (await app.GetTestClient().GetAsync("/hello")).EnsureSuccessStatusCode();
        await app.StopAsync();

        Assert.False(app.Services.GetRequiredService<TelemetryRuntime>().IsPrepared);
        Assert.Empty(receiver.Exports);
    }

    [Fact]
    public async Task ContainerDisposalStopsWorkerAfterFailedServerBind()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        using var environment = new EnvironmentVariables(
            ("APITALLY_OTLP_ENDPOINT", receiver.Endpoint.ToString())
        );
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        var port = ((IPEndPoint)occupied.LocalEndpoint).Port;
        var timeProvider = new FakeTimeProvider();
        var app = Program.CreateMinimalApp(
            [
                $"--urls=http://127.0.0.1:{port}",
                $"--Apitally:WriteToken={TestConfiguration.WriteToken}",
            ],
            builder => builder.Services.AddSingleton<TimeProvider>(timeProvider)
        );

        await Assert.ThrowsAnyAsync<IOException>(() => app.StartAsync());
        Assert.True(app.Services.GetRequiredService<TelemetryRuntime>().IsPrepared);
        await app.DisposeAsync();
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        await Task.Delay(200);

        Assert.Empty(receiver.Exports);
    }

    [Fact]
    public async Task ShutdownWithCanceledTokenReturnsPromptly()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        receiver.Respond = _ =>
        {
            Thread.Sleep(2_000);
            return (200, null);
        };
        await using var host = await ApplicationHost.StartMinimalAsync(receiver);
        var runtime = host.Services.GetRequiredService<TelemetryRuntime>();

        var stopwatch = Stopwatch.StartNew();
        await runtime.ShutdownAsync(new CancellationToken(canceled: true));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
    }
}
