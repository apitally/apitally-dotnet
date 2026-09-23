using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace ProviderRegistration;

internal static class Program
{
    public static async Task<int> Main()
    {
        Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
        foreach (
            var assembly in new[]
            {
                typeof(WebApplication).Assembly,
                typeof(Sdk).Assembly,
                typeof(Activity).Assembly,
            }
        )
            Console.WriteLine(
                $"{assembly.GetName().Name}: {assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion}"
            );
        try
        {
            RegistrationEvidence();
            await NaiveOwnershipFailure();
            await UserDefaultSampler();
            await OwnedAndFirstRequest();
            foreach (var appFirst in new[] { true, false })
            foreach (
                var decision in new[] { SamplingDecision.Drop, SamplingDecision.RecordAndSample }
            )
                await UserRegistration(appFirst, decision);
            await ConfigureOnlyLimitation();
            await ExternalProvider();
            await ExternalMissingSource();
            await OutboundDefaults();
            await TwoOwnedHosts();
            foreach (var userFirst in new[] { true, false })
                await ConflictingHostSamplers(userFirst);
            Console.WriteLine(
                "PASS all checks, including explicitly reproduced negative hypotheses"
            );
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL " + exception);
            return 1;
        }
        finally
        {
            LocalHost.TestClient.Dispose();
            LocalHost.Manual.Dispose();
        }
    }

    public static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void RegistrationEvidence()
    {
        var services = new ServiceCollection();
        var called = false;
        services.ConfigureOpenTelemetryTracerProvider(builder =>
            builder.AddSource("registration-probe")
        );
        services.ConfigureOpenTelemetryTracerProvider((_, _) => called = true);
        Check(
            !services.Any(s => s.ServiceType == typeof(TracerProvider)),
            "Configure does not enable provider"
        );
        using (var sp = services.BuildServiceProvider())
            Check(sp.GetService<TracerProvider>() is null && !called, "Configure alone is inert");
        services.AddOpenTelemetry();
        Check(
            !services.Any(s => s.ServiceType == typeof(TracerProvider)),
            "AddOpenTelemetry alone does not enable tracing"
        );
        services.AddOpenTelemetry().WithTracing();
        var descriptor = services.Single(s => s.ServiceType == typeof(TracerProvider));
        services.AddOpenTelemetry().WithTracing(b => b.SetSampler(new AlwaysOffSampler()));
        Check(
            ReferenceEquals(
                descriptor,
                services.Single(s => s.ServiceType == typeof(TracerProvider))
            ),
            "WithTracing calls keep one identical public provider descriptor"
        );
        using (var sp = services.BuildServiceProvider())
        {
            var provider = sp.GetRequiredService<TracerProvider>();
            Check(
                ReferenceEquals(provider, sp.GetRequiredService<TracerProvider>()) && called,
                "callbacks run on singleton construction"
            );
        }
        Console.WriteLine(
            "PASS registration evidence: Configure=0, AddOpenTelemetry=0, repeated WithTracing=1 identical descriptor; no caller provenance"
        );
    }

    private static async Task NaiveOwnershipFailure()
    {
        foreach (var appFirst in new[] { true, false })
        {
            var user = new MemoryExporter();
            await using var host = await LocalHost.Start(services =>
            {
                void App() =>
                    services
                        .AddOpenTelemetry()
                        .WithTracing(b =>
                            b.AddAspNetCoreInstrumentation()
                                .AddProcessor(new SimpleActivityExportProcessor(user))
                        );
                void Naive()
                {
                    var hadProvider = services.Any(s => s.ServiceType == typeof(TracerProvider));
                    services
                        .AddOpenTelemetry()
                        .WithTracing(b =>
                        {
                            b.AddAspNetCoreInstrumentation();
                            if (!hadProvider)
                                b.SetSampler(new AlwaysOnSampler());
                        });
                }
                if (appFirst)
                {
                    App();
                    Naive();
                }
                else
                {
                    Naive();
                    App();
                }
            });
            await host.Request("/plain/naive", unsampledParent: true);
            await Settle();
            Equal(
                user.Spans.Count,
                appFirst ? 0 : 1,
                "naive registration-order-dependent default sampling"
            );
        }
        Console.WriteLine(
            "EXPECTED FAILURE naive ownership: user default sampler drops remote unsampled parent only when app registers first; Apitally-first changes user export 0 -> 1"
        );
    }

    private static async Task UserDefaultSampler()
    {
        foreach (var appFirst in new[] { true, false })
        {
            var probe = new Probe();
            var user = new MemoryExporter();
            await using var host = await LocalHost.Start(services =>
            {
                void App() =>
                    services
                        .AddOpenTelemetry()
                        .WithTracing(b =>
                            b.AddAspNetCoreInstrumentation()
                                .AddProcessor(new SimpleActivityExportProcessor(user))
                        );
                if (appFirst)
                {
                    App();
                    Candidate.Register(services, probe);
                }
                else
                {
                    Candidate.Register(services, probe);
                    App();
                }
            });
            await host.Request("/plain/default-unsampled", unsampledParent: true);
            await Settle();
            Equal(user.Spans.Count, 0, "user default sampler drops remote unsampled parent");
            Equal(probe.Accepted.Count, 0, "candidate does not override user default sampler");
            await host.Request("/plain/default-root");
            await Until(() => user.Spans.Count == 1 && probe.Accepted.Count == 1);
        }
        Console.WriteLine(
            "PASS candidate preserves implicit user parent-based default in both registration orders: remote unsampled=0, new root=1"
        );
    }

    private static async Task OwnedAndFirstRequest()
    {
        var oldSampler = Environment.GetEnvironmentVariable("OTEL_TRACES_SAMPLER");
        Environment.SetEnvironmentVariable("OTEL_TRACES_SAMPLER", "always_off");
        try
        {
            var probe = new Probe();
            await using var host = await LocalHost.Start(
                s => Candidate.Register(s, probe),
                earlyRequest: true
            );
            var runtime = host.Services.GetRequiredService<CandidateRuntime>();
            Check(
                runtime.OwnsProvider && host.Services.GetService<TracerProvider>() is null,
                "owned provider stays out of DI tracing slot"
            );
            await Until(() => probe.Accepted.Count == 2);
            var server = probe.Accepted.Single(s => s.Kind == ActivityKind.Server);
            var child = probe.Accepted.Single(s => s.Kind == ActivityKind.Internal);
            Check(
                probe.Starts.ContainsKey(server.Id),
                "first SERVER observed at OnStart, not attached mid-request"
            );
            Check(
                server.TraceId == new string('1', 32) && server.ParentSpanId == new string('2', 16),
                "remote context preserved"
            );
            Check(
                server.Recorded && child.Recorded && child.ParentSpanId == server.SpanId,
                "owned always-on records unsampled remote parent and descendant"
            );
            Equal(server.Route, "/work/{id}", "parameterized route");
            await host.Request("/work/second");
            await Until(() => probe.Accepted.Count == 4);
            Equal(
                probe.Accepted.Count(s => s.Kind == ActivityKind.Server),
                2,
                "one SERVER per request"
            );
            Check(
                probe
                    .Accepted.Where(s => s.Kind == ActivityKind.Server)
                    .Select(s => s.TraceId)
                    .Distinct()
                    .Count() == 2,
                "keep-alive next request does not inherit first request context"
            );
            Console.WriteLine(
                "PASS owned: first request before ApplicationStarted, remote unsampled parent + child recorded, sampler env ignored, keep-alive roots separate"
            );
        }
        finally
        {
            Environment.SetEnvironmentVariable("OTEL_TRACES_SAMPLER", oldSampler);
        }
    }

    private static async Task UserRegistration(bool appFirst, SamplingDecision decision)
    {
        var probe = new Probe();
        var user = new MemoryExporter();
        var sampler = new CountingSampler(decision);
        var enrichments = 0;
        await using var host = await LocalHost.Start(services =>
        {
            void App() =>
                services
                    .AddOpenTelemetry()
                    .WithTracing(b =>
                        b.SetSampler(sampler)
                            .SetResourceBuilder(
                                ResourceBuilder.CreateEmpty().AddService("user-service")
                            )
                            .AddSource("user.background")
                            .AddAspNetCoreInstrumentation(options =>
                                options.EnrichWithHttpRequest = (activity, _) =>
                                {
                                    Interlocked.Increment(ref enrichments);
                                    activity.SetTag("user.enrichment", "preserved");
                                }
                            )
                            .AddProcessor(new SimpleActivityExportProcessor(user))
                    );
            if (appFirst)
            {
                App();
                Candidate.Register(services, probe);
            }
            else
            {
                Candidate.Register(services, probe);
                App();
            }
            services.ConfigureOpenTelemetryTracerProvider(b => b.AddAspNetCoreInstrumentation());
        });
        var runtime = host.Services.GetRequiredService<CandidateRuntime>();
        Check(
            !runtime.OwnsProvider && probe.BuilderConfigured,
            "candidate reuses enabled DI provider"
        );
        Check(
            ReferenceEquals(runtime.Provider, host.Services.GetRequiredService<TracerProvider>()),
            "same provider identity"
        );
        Check(
            runtime
                .Provider!.GetResource()
                .Attributes.Any(kv =>
                    kv.Key == "service.name" && (string?)kv.Value == "user-service"
                ),
            "user resource preserved"
        );
        await host.Request("/work/user", unsampledParent: true);
        await Settle();
        var kept = decision == SamplingDecision.RecordAndSample;
        Equal(user.Spans.Count, kept ? 2 : 0, "user sampler/exporter behavior");
        Equal(probe.Accepted.Count, kept ? 2 : 0, "candidate respects user sampling");
        Equal(enrichments, kept ? 1 : 0, "three ASP.NET registrations enrich at most once");
        Check(
            sampler.Calls.Any(p =>
                p.Kind == ActivityKind.Server
                && p.ParentContext.IsRemote
                && !p.ParentContext.TraceFlags.HasFlag(ActivityTraceFlags.Recorded)
            ),
            "original sampler sees remote unsampled parent"
        );
        if (kept)
        {
            Equal(user.Spans.Count(s => s.Kind == ActivityKind.Server), 1, "no duplicate SERVER");
            Equal(
                user.Spans.Single(s => s.Kind == ActivityKind.Server).UserTag,
                "preserved",
                "user enrichment retained"
            );
            Check(
                user.Spans.Select(s => s.Id)
                    .ToHashSet()
                    .SetEquals(probe.Accepted.Select(s => s.Id)),
                "same activity IDs for both paths"
            );
            using var source = new ActivitySource("user.background");
            using (source.StartActivity("background")) { }
            Equal(user.Spans.Count, 3, "user background exporter unaffected");
            Equal(probe.Accepted.Count, 2, "candidate drops unrelated background root");
        }
        Console.WriteLine(
            $"PASS user registration appFirst={appFirst}, sampler={decision}: one provider, no sampler replacement, exporter/resource/enrichment preserved, triple ASP.NET registration"
        );
    }

    private static async Task ConfigureOnlyLimitation()
    {
        var callbackRan = false;
        var probe = new Probe();
        await using var host = await LocalHost.Start(services =>
        {
            services.ConfigureOpenTelemetryTracerProvider(b =>
                b.SetSampler(new AlwaysOffSampler())
            );
            services.ConfigureOpenTelemetryTracerProvider((_, _) => callbackRan = true);
            Candidate.Register(services, probe);
        });
        await host.Request("/plain/config-only", unsampledParent: true);
        await Until(() => probe.Accepted.Count == 1);
        Check(
            host.Services.GetRequiredService<CandidateRuntime>().OwnsProvider && !callbackRan,
            "configuration without enabled DI provider cannot be treated as enabled user tracing"
        );
        Console.WriteLine(
            "LIMITATION Configure-only user sampler is not applied to private owned fallback: no enabled TracerProvider, callbacks never run; user intent cannot be inferred"
        );
    }

    private static async Task ExternalProvider()
    {
        var user = new MemoryExporter();
        var sampler = new CountingSampler(SamplingDecision.RecordAndSample);
        using var provider = Sdk.CreateTracerProviderBuilder()
            .SetSampler(sampler)
            .AddAspNetCoreInstrumentation()
            .AddSource("apitally.otel", "user.background")
            .AddProcessor(new SimpleActivityExportProcessor(user))
            .Build();
        var probe = new Probe();
        await using (var host = await LocalHost.Start(s => Candidate.Register(s, probe, provider)))
        {
            Check(
                !host.Services.GetRequiredService<CandidateRuntime>().OwnsProvider
                    && !probe.BuilderConfigured,
                "external attachment uses public post-build AddProcessor"
            );
            await host.Request("/work/external", unsampledParent: true);
            await Until(() => user.Spans.Count == 2 && probe.Accepted.Count == 2);
        }
        Check(!user.Disposed, "host does not dispose external provider or exporter");
        Check(
            sampler.Calls.Any(p => p.Kind == ActivityKind.Server && p.ParentContext.IsRemote),
            "external sampler remains in use"
        );
        using var source = new ActivitySource("user.background");
        using (source.StartActivity("after-host-disposal")) { }
        Equal(user.Spans.Count, 3, "external user exporter remains active after host disposal");
        Equal(probe.Accepted.Count, 2, "candidate export disabled after host disposal");
        Check(!probe.Enabled, "external attached processor disabled, not removed");
        Console.WriteLine(
            "PASS external AddProcessor: first request and child; host disposal leaves user sampler/exporter active (processor cannot be detached)"
        );
    }

    private static async Task ExternalMissingSource()
    {
        using var provider = Sdk.CreateTracerProviderBuilder().AddSource("user.background").Build();
        var probe = new Probe();
        await using var host = await LocalHost.Start(s => Candidate.Register(s, probe, provider));
        await host.Request("/plain/no-server-source");
        await Settle();
        Equal(
            probe.Raw.Count,
            0,
            "post-build AddProcessor does not subscribe to missing ASP.NET source"
        );
        Console.WriteLine(
            "EXPECTED FAILURE external provider without ASP.NET source: AddProcessor succeeds but cannot supply missing instrumentation/source subscription"
        );
    }

    private static async Task OutboundDefaults()
    {
        await using var receiver = await LocalHost.Start();
        foreach (var userOwned in new[] { false, true })
        {
            var probe = new Probe();
            await using var host = await LocalHost.Start(
                services =>
                {
                    if (userOwned)
                        services
                            .AddOpenTelemetry()
                            .WithTracing(b =>
                                b.SetSampler(new AlwaysOnSampler()).AddAspNetCoreInstrumentation()
                            );
                    Candidate.Register(services, probe);
                },
                downstream: receiver.Address
            );
            await host.Request("/outgoing");
            await Until(() => probe.Accepted.Count(s => s.Kind == ActivityKind.Server) == 1);
            await Settle();
            Equal(
                probe.Accepted.Count(s => s.Kind == ActivityKind.Client),
                userOwned ? 0 : 1,
                "HTTP client default scoped to owned tracing"
            );
            Equal(
                probe.Raw.Count(s => s.Kind == ActivityKind.Server),
                2,
                "process-wide observation sees unmonitored receiver"
            );
            Equal(
                probe.Accepted.Count(s => s.Kind == ActivityKind.Server),
                1,
                "host filter rejects unmonitored receiver"
            );
        }
        Console.WriteLine(
            "PASS outbound: HttpClient enabled only for owned fallback; suppressed harness client absent; unmonitored receiver filtered"
        );
    }

    private static async Task TwoOwnedHosts()
    {
        var a = new Probe();
        var b = new Probe();
        var entered = 0;
        var bothEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        Task Overlap()
        {
            if (Interlocked.Increment(ref entered) == 2)
                bothEntered.TrySetResult();
            return bothEntered.Task;
        }
        await using var hostA = await LocalHost.Start(
            s => Candidate.Register(s, a),
            overlap: Overlap
        );
        await using var hostB = await LocalHost.Start(
            s => Candidate.Register(s, b),
            overlap: Overlap
        );
        await Task.WhenAll(hostA.Request("/work/a"), hostB.Request("/work/b"));
        await Until(() => a.Raw.Count == 4 && b.Raw.Count == 4);
        Equal(a.Accepted.Count, 2, "A filters B server and child");
        Equal(b.Accepted.Count, 2, "B filters A server and child");
        Check(
            a.Accepted.Single(s => s.Kind == ActivityKind.Server).Path == "/work/a",
            "A association"
        );
        Check(
            b.Accepted.Single(s => s.Kind == ActivityKind.Server).Path == "/work/b",
            "B association"
        );
        Check(
            !a.Accepted.Select(s => s.Id).Intersect(b.Accepted.Select(s => s.Id)).Any(),
            "no accepted cross-host spans"
        );
        await hostA.DisposeAsync();
        await hostB.Request("/plain/b-after-a-stop");
        await Until(() => b.Accepted.Count == 3);
        Equal(a.Raw.Count, 4, "stopped owned provider listener removed");
        Console.WriteLine(
            "PASS two overlapping owned hosts: each raw=4, accepted=2; after A disposal B accepted=3, A unchanged"
        );
    }

    private static async Task ConflictingHostSamplers(bool userFirst)
    {
        var user = new MemoryExporter();
        var dropped = new Probe();
        var owned = new Probe();
        var sampler = new CountingSampler(SamplingDecision.Drop);
        Task<LocalHost> UserHost() =>
            LocalHost.Start(s =>
            {
                s.AddOpenTelemetry()
                    .WithTracing(b =>
                        b.SetSampler(sampler)
                            .AddAspNetCoreInstrumentation()
                            .AddProcessor(new SimpleActivityExportProcessor(user))
                    );
                Candidate.Register(s, dropped);
            });
        LocalHost? hostUser = null;
        LocalHost? hostOwned = null;
        try
        {
            if (userFirst)
            {
                hostUser = await UserHost();
                await hostUser.Request("/plain/alone");
                await Settle();
                Equal(user.Spans.Count, 0, "user drop sampler alone exports nothing");
                hostOwned = await LocalHost.Start(s => Candidate.Register(s, owned));
            }
            else
            {
                hostOwned = await LocalHost.Start(s => Candidate.Register(s, owned));
                hostUser = await UserHost();
            }
            await Task.WhenAll(hostUser.Request("/plain/user"), hostOwned.Request("/plain/owned"));
            await Until(() =>
                user.Spans.Count == 2 && dropped.Accepted.Count == 1 && owned.Accepted.Count == 1
            );
            Equal(
                sampler.Calls.Count(p => p.Kind == ActivityKind.Server),
                userFirst ? 3 : 2,
                "drop sampler still runs for all hosts"
            );
            Check(
                user.Spans.All(s => s.Recorded),
                "other listener promotes activities despite user Drop decision"
            );
            Equal(
                dropped.Accepted.Single().Path,
                "/plain/user",
                "filter associates only user host"
            );
            Equal(
                owned.Accepted.Single().Path,
                "/plain/owned",
                "filter associates only owned host"
            );
            await hostOwned.DisposeAsync();
            await hostUser.Request("/plain/after-owned-stop");
            await Settle();
            Equal(user.Spans.Count, 2, "removing always-on listener restores user drop behavior");
            Equal(dropped.Accepted.Count, 1, "user host continues but no longer records");
            Console.WriteLine(
                $"EXPECTED FAILURE multi-host sampler isolation userFirst={userFirst}: user Drop unchanged but exporter gets 2 SERVERs; filter accepts own 1 incorrectly sampled request; stopping owned restores Drop"
            );
        }
        finally
        {
            if (hostOwned is not null)
                await hostOwned.DisposeAsync();
            if (hostUser is not null)
                await hostUser.DisposeAsync();
        }
    }

    private static void Equal<T>(T actual, T expected, string message) =>
        Check(
            EqualityComparer<T>.Default.Equals(actual, expected),
            $"{message}: expected {expected}, got {actual}"
        );

    private static Task Settle() => Task.Delay(100);

    private static async Task Until(Func<bool> ready)
    {
        var timeout = Stopwatch.StartNew();
        while (!ready())
        {
            Check(timeout.Elapsed < TimeSpan.FromSeconds(5), "bounded telemetry wait timed out");
            await Task.Delay(10);
        }
    }
}
