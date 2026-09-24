using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Trace;
using TestHostSuppression;

internal static class Program
{
    private static int passed;

    public static async Task<int> Main()
    {
        try
        {
            Console.WriteLine(
                $"Runtime: {RuntimeInformation.FrameworkDescription}; {RuntimeInformation.OSArchitecture}"
            );
            foreach (
                var assembly in new[]
                {
                    typeof(WebApplication).Assembly,
                    typeof(WebApplicationFactory<>).Assembly,
                    typeof(TestServer).Assembly,
                    typeof(TracerProvider).Assembly,
                    typeof(Sdk).Assembly,
                    typeof(AspNetCoreTraceInstrumentationOptions).Assembly,
                }
            )
                Console.WriteLine($"Assembly: {assembly.GetName().Name} {Version(assembly)}");
            Check(
                !typeof(CandidateRuntime)
                    .Assembly.GetReferencedAssemblies()
                    .Any(reference =>
                        reference.Name
                            is "Microsoft.AspNetCore.TestHost"
                                or "Microsoft.AspNetCore.Mvc.Testing"
                    ),
                "fixture guard assembly has no TestHost or Mvc.Testing reference"
            );

            foreach (var requestOnly in new[] { false, true })
            {
                var options = new ProbeOptions(OmitApplicationStartedTrigger: requestOnly);
                var trigger = requestOnly ? "request-only" : "startup";
                await FactoryCase($"factory-testserver-{trigger}", options);
                await FactoryCase($"factory-user-tracing-{trigger}", options, userTracing: true);
                await DirectCase($"direct-testserver-{trigger}", options, testServer: true);
                await GenericCase($"generic-startup-testserver-{trigger}", options);
                await DirectCase(
                    $"direct-kestrel-development-{trigger}",
                    options,
                    testServer: false
                );
#if NET10_0_OR_GREATER
                await FactoryCase(
                    $"factory-kestrel-development-{trigger}",
                    options,
                    useKestrel: true
                );
#endif
            }
            await DirectCase(
                "direct-kestrel-explicitly-disabled",
                new ProbeOptions(Enabled: false),
                testServer: false
            );
            Console.WriteLine(
                $"PASS {passed} cases; guard assembly dependency check passed; no external telemetry"
            );
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL after {passed} completed cases: {exception}");
            return 1;
        }
    }

    private static async Task FactoryCase(
        string name,
        ProbeOptions options,
        bool userTracing = false,
        bool useKestrel = false
    )
    {
        var userExporter = userTracing ? new MemoryExporter() : null;
        var factory = new WebApplicationFactory<FixtureProgram>().WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(AppContext.BaseDirectory);
            builder.ConfigureServices(services => ConfigureCase(services, options, userExporter));
        });
        Observation observation;
        try
        {
#if NET10_0_OR_GREATER
            if (useKestrel)
                factory.UseKestrel(0);
#endif
            using var client = useKestrel
                ? KestrelClient(factory.Services)
                : factory.CreateClient(
                    new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }
                );
            observation = await VerifyRunning(
                name,
                factory.Services,
                client,
                options,
                !useKestrel,
                userExporter
            );
            Check(
                observation.ServerAtRegistration?.Contains("Kestrel", StringComparison.Ordinal)
                    == true,
                "fixture registers candidate before factory replaces Kestrel"
            );
        }
        finally
        {
            await factory.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }
        await VerifyDisposed(name, observation, userExporter);
    }

    private static async Task DirectCase(string name, ProbeOptions options, bool testServer)
    {
        var builder = FixtureProgram.CreateBuilder();
        ConfigureCase(builder.Services, options);
        if (testServer)
            builder.WebHost.UseTestServer();
        var app = builder.Build();
        app.MapGet("/probe", () => "synthetic-response");
        Observation observation;
        try
        {
            using var startTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await app.StartAsync(startTimeout.Token);
            using var client = testServer ? app.GetTestClient() : KestrelClient(app.Services);
            observation = await VerifyRunning(name, app.Services, client, options, testServer);
        }
        finally
        {
            await StopHost(app);
        }
        await VerifyDisposed(name, observation);
    }

    private static async Task GenericCase(string name, ProbeOptions options)
    {
        var host = new HostBuilder()
            .UseEnvironment(Environments.Development)
            .ConfigureAppConfiguration(configuration => configuration.Sources.Clear())
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services => ConfigureCase(services, options))
            .ConfigureWebHost(web =>
                web.UseEnvironment(Environments.Development)
                    .UseContentRoot(AppContext.BaseDirectory)
                    .ConfigureAppConfiguration(configuration => configuration.Sources.Clear())
                    .UseStartup<FixtureStartup>()
                    .UseTestServer()
            )
            .Build();
        Observation observation;
        try
        {
            using var startTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await host.StartAsync(startTimeout.Token);
            using var client = host.GetTestClient();
            observation = await VerifyRunning(
                name,
                host.Services,
                client,
                options,
                testServer: true
            );
        }
        finally
        {
            await StopHost(host);
        }
        await VerifyDisposed(name, observation);
    }

    private static async Task<Observation> VerifyRunning(
        string name,
        IServiceProvider services,
        HttpClient client,
        ProbeOptions options,
        bool testServer,
        MemoryExporter? userExporter = null
    )
    {
        client.Timeout = TimeSpan.FromSeconds(5);
        var observation = services.GetRequiredService<Observation>();
        var runtime = services.GetRequiredService<CandidateRuntime>();
        var userProvider = services.GetService<TracerProvider>();
        var allowed = options.Enabled && !testServer;
        Check(
            services.GetRequiredService<IHostEnvironment>().IsDevelopment(),
            "Development is explicit"
        );
        Check(
            services
                .GetRequiredService<IHostApplicationLifetime>()
                .ApplicationStarted.IsCancellationRequested,
            "host has started, including request-only cases"
        );
        Check(
            typeof(TestServer).Assembly.GetName().Name == "Microsoft.AspNetCore.TestHost",
            "TestHost is loaded"
        );
        Check(observation.Suppressed == testServer, "suppression matches actual server");
        Check(
            observation.ResolvedServer == services.GetRequiredService<IServer>().GetType().FullName,
            "pipeline observed actual resolved server"
        );
        if (testServer)
        {
            Check(services.GetRequiredService<IServer>() is TestServer, "real TestServer instance");
            Check(
                observation.ResolvedServer == "Microsoft.AspNetCore.TestHost.TestServer"
                    && observation.ResolvedServerAssembly == "Microsoft.AspNetCore.TestHost",
                "exact TestServer identity"
            );
        }
        else
        {
            Check(
                observation.ResolvedServerAssembly == "Microsoft.AspNetCore.Server.Kestrel.Core",
                "real Kestrel instance"
            );
            var address = services
                .GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!
                .Addresses.Single();
            Check(
                new Uri(address).Host == "127.0.0.1" && new Uri(address).Port > 0,
                "loopback dynamic Kestrel address"
            );
        }
        Check(
            observation.StartupSignals == (options.OmitApplicationStartedTrigger ? 0 : 1),
            "startup signal count"
        );
        Check(observation.RequestSignals == 0, "no requests before assertion");
        Check(
            observation.FallbackConstructions == (allowed ? 1 : 0),
            "fallback creation decided during pipeline construction"
        );
        Check(
            observation.Active == (allowed && !options.OmitApplicationStartedTrigger),
            "startup activation decision"
        );
        Check(observation.Activations == (observation.Active ? 1 : 0), "startup activation count");
        Check(
            (userProvider is not null) == (userExporter is not null),
            "private fallback does not register a DI provider"
        );

        for (var index = 0; index < 2; index++)
        {
            if (index == 1 && userExporter is not null)
            {
                runtime.Dispose();
                Check(
                    userExporter.DisposeCount == 0,
                    "candidate disposal leaves user provider alive"
                );
                Check(
                    ReferenceEquals(userProvider, services.GetRequiredService<TracerProvider>()),
                    "user provider identity preserved"
                );
            }
            var traceId = ActivityTraceId.CreateRandom().ToHexString();
            using var request = new HttpRequestMessage(HttpMethod.Get, "/probe");
            request.Headers.Add("traceparent", $"00-{traceId}-1111111111111111-01");
            using var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            Check(
                await response.Content.ReadAsStringAsync() == "synthetic-response",
                "synthetic response completed"
            );
            var exporter = userExporter ?? observation.CandidateExporter;
            if (exporter is not null)
            {
                var span = await exporter.WaitForServerSpan(traceId);
                Check(
                    span.Kind == ActivityKind.Server
                        && span.Recorded
                        && span.Duration > TimeSpan.Zero,
                    "completed sampled server span observed by exporter"
                );
                Check(
                    span.Route == "/probe" && span.StatusCode == "200",
                    "request route and status exported"
                );
            }
            Check(observation.Active == allowed, "request activation decision");
            Check(
                observation.Activations == (allowed ? 1 : 0),
                "activation is suppressed or occurs once"
            );
            Check(observation.RequestSignals == index + 1, "middleware request trigger ran");
        }
        if (!allowed)
        {
            Check(
                observation.FallbackConstructions == 0 && observation.CandidateExporter is null,
                "no candidate provider, processor, exporter, or activation when suppressed/disabled"
            );
        }
        else
        {
            Check(
                observation.CandidateExporter!.Spans.Count == 2
                    && observation.CandidateExporter.ExportCalls == 2,
                "two actual private fallback exports completed"
            );
        }
        if (userExporter is not null)
            Check(
                userExporter.Spans.Count == 2 && userExporter.ExportCalls == 2,
                "two actual user exports completed"
            );
        Console.WriteLine(
            $"OBSERVE {name}: registered={observation.ServerAtRegistration ?? "<none>"}; resolved={observation.ResolvedServer}; suppressed={observation.Suppressed}; startup={observation.StartupSignals}; requests={observation.RequestSignals}; fallback={observation.FallbackConstructions}; activations={observation.Activations}; privateExports={observation.CandidateExporter?.Spans.Count ?? 0}; userExports={userExporter?.Spans.Count ?? 0}"
        );
        return observation;
    }

    private static void ConfigureCase(
        IServiceCollection services,
        ProbeOptions options,
        MemoryExporter? userExporter = null
    )
    {
        services.AddSingleton(options);
        if (userExporter is not null)
            services
                .AddOpenTelemetry()
                .WithTracing(builder =>
                    builder
                        .SetSampler(new AlwaysOnSampler())
                        .AddAspNetCoreInstrumentation()
                        .AddProcessor(new SimpleActivityExportProcessor(userExporter))
                );
    }

    private static HttpClient KestrelClient(IServiceProvider services) =>
        new(new SocketsHttpHandler { UseProxy = false, ActivityHeadersPropagator = null })
        {
            BaseAddress = new Uri(
                services
                    .GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()!
                    .Addresses.Single()
            ),
        };

    private static async Task StopHost(IHost host)
    {
        try
        {
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await host.StopAsync(stopTimeout.Token);
        }
        finally
        {
            if (host is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            else
                host.Dispose();
        }
    }

    private static async Task VerifyDisposed(
        string name,
        Observation observation,
        MemoryExporter? userExporter = null
    )
    {
        await observation.Disposal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!observation.Active, "candidate inactive after disposal");
        if (observation.CandidateExporter is not null)
        {
            await observation.CandidateExporter.WaitForDisposal();
            Check(
                observation.CandidateExporter.DisposeCount == 1,
                "owned fallback exporter disposed once"
            );
        }
        if (userExporter is not null)
        {
            await userExporter.WaitForDisposal();
            Check(userExporter.DisposeCount == 1, "host disposes user exporter once");
        }
        passed++;
        Console.WriteLine($"PASS {name} (including resource disposal)");
    }

    private static string Version(Assembly assembly) =>
        assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion.Split('+')[0];

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
