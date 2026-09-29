using System.Diagnostics;
using Apitally.Hosting;
using Apitally.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OtlpSpan = OpenTelemetry.Proto.Trace.V1.Span;

namespace Apitally.Tests.Requests;

public class RequestRegistryTests
{
    private static readonly ActivitySource ApplicationSource = new("TestApp.Requests");

    [Fact]
    public async Task NestedAndExplicitParentActivitiesAreAssociatedWithTheirRequest()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        await using var host = await ApplicationHost.StartMinimalAsync(
            receiver,
            configureApp: app =>
                app.MapGet(
                    "/nested",
                    async (IApitally apitally) =>
                    {
                        using var outer = ApplicationSource.StartActivity("outer");
                        using (ApplicationSource.StartActivity("inner"))
                            apitally.SetRequestAttribute("set.in.child", true);
                        var parent = outer!.Context;
                        Activity.Current = null;
                        await Task.Run(() =>
                        {
                            using var explicitChild = ApplicationSource.StartActivity(
                                "explicit",
                                ActivityKind.Internal,
                                parent
                            );
                        });
                        return "OK";
                    }
                )
        );

        await host.Client.GetAsync("/nested");
        await host.StopAsync();

        var spans = receiver.Spans();
        Assert.Equal(
            ["GET /nested", "explicit", "inner", "outer"],
            spans.Select(span => span.Name).Order(StringComparer.Ordinal)
        );
        var server = spans.Server();
        var outer = spans.Single(span => span.Name == "outer");
        Assert.Equal(server.SpanId, outer.ParentSpanId);
        Assert.Equal(outer.SpanId, spans.Single(span => span.Name == "explicit").ParentSpanId);
        Assert.Equal(true, server.Attributes()["set.in.child"]);
        Assert.False(
            spans.Single(span => span.Name == "inner").Attributes().ContainsKey("set.in.child")
        );
    }

    [Fact]
    public async Task ConcurrentRequestsSharingATraceStayIsolated()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        await using var host = await ApplicationHost.StartMinimalAsync(receiver);
        var traceId = ActivityTraceId.CreateRandom();

        await Task.WhenAll(
            Enumerable
                .Range(1, 20)
                .Select(async id =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, $"/items/{id}");
                    request.Headers.Add(
                        "traceparent",
                        $"00-{traceId.ToHexString()}-{ActivitySpanId.CreateRandom().ToHexString()}-01"
                    );
                    (await host.Client.SendAsync(request)).EnsureSuccessStatusCode();
                })
        );
        await host.StopAsync();

        var spans = receiver.Spans();
        var servers = spans.Where(span => span.Kind == OtlpSpan.Types.SpanKind.Server).ToList();
        Assert.Equal(20, servers.Count);
        Assert.Equal(40, spans.Count);
        foreach (var server in servers)
        {
            var id = (long)server.Attributes()["item.id"]!;
            Assert.Equal($"/items/{id}", server.Attributes()["url.path"]);
            Assert.Single(spans, span => span.ParentSpanId == server.SpanId);
        }
    }

    [Fact]
    public async Task ResponseSamplingSeesFinalStatusRouteAndRequestAttributes()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        var seen = new List<(object?, object?, object?, TimeSpan?)>();
        await using var host = await ApplicationHost.StartMinimalAsync(
            receiver,
            builder =>
                builder.Services.AddApitally(options =>
                    options.SampleOnResponse = span =>
                    {
                        seen.Add(
                            (
                                span.Attributes["http.response.status_code"],
                                span.Attributes["http.route"],
                                span.Attributes.GetValueOrDefault("item.id"),
                                span.Duration
                            )
                        );
                        return span.Attributes["http.response.status_code"] is 500L ? 1 : 0;
                    }
                )
        );

        await host.Client.GetAsync("/items/7");
        await host.Client.GetAsync("/error");
        await host.StopAsync();

        Assert.Equal((200L, "/items/{id:int}", 7L), (seen[0].Item1, seen[0].Item2, seen[0].Item3));
        Assert.NotNull(seen[0].Item4);
        Assert.Equal("/error", Assert.Single(receiver.Spans()).Attributes()["http.route"]);
    }

    [Fact]
    public async Task RequestSamplingCallbackSeesRequestStageSnapshot()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        var seen = new List<SpanSnapshot>();
        await using var host = await ApplicationHost.StartMinimalAsync(
            receiver,
            builder =>
                builder.Services.AddApitally(options =>
                    options.SampleOnRequest = span =>
                    {
                        seen.Add(span);
                        return span.Attributes["url.path"] is "/hello" ? 0 : null;
                    }
                )
        );

        await host.Client.GetAsync("/hello");
        await host.Client.GetAsync("/items/1");
        await host.Client.GetAsync("/healthz");
        await host.StopAsync();

        Assert.Equal(2, seen.Count);
        Assert.Null(seen[0].Duration);
        Assert.Equal(ActivityKind.Server, seen[0].Kind);
        Assert.Equal("GET", seen[0].Attributes["http.request.method"]);
        Assert.Equal("/items/{id:int}", receiver.Spans().Server().Attributes()["http.route"]);
    }

    [Fact]
    public async Task ThrowingSamplingCallbacksKeepTheRequest()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        await using var host = await ApplicationHost.StartMinimalAsync(
            receiver,
            builder =>
                builder.Services.AddApitally(options =>
                {
                    options.SampleRate = 0;
                    options.SampleOnRequest = _ => throw new InvalidOperationException();
                    options.SampleOnResponse = _ => 2.5;
                })
        );

        await host.Client.GetAsync("/hello");
        await host.StopAsync();

        Assert.Single(receiver.Spans());
    }

    [Fact]
    public async Task PerRequestSpanLimitKeepsFirstStartedChildrenAndTheirLogs()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        await using var host = await ApplicationHost.StartMinimalAsync(
            receiver,
            configureApp: app =>
                app.MapGet(
                    "/many",
                    (TelemetryRuntime runtime, ILoggerFactory loggerFactory) =>
                    {
                        var logger = loggerFactory.CreateLogger("TestApp.Requests");
                        using (var first = ApplicationSource.StartActivity("child"))
                        {
                            first!.SetTag("index", 0);
                            for (var i = 1; i < 1_001; i++)
                            {
                                using var activity = ApplicationSource.StartActivity("child");
                                activity!.SetTag("index", i);
                                Assert.Equal(
                                    i < 1_000,
                                    runtime.Registry!.TryGet(
                                        activity.TraceId,
                                        activity.SpanId,
                                        out _
                                    )
                                );
                                if (i is 999 or 1_000)
                                    logger.LogWarning("Child {Index}", i);
                            }
                            logger.LogWarning("Retained child");
                        }
                        logger.LogWarning("Request");
                        return "OK";
                    }
                )
        );

        await host.Client.GetAsync("/many");
        await host.StopAsync();

        var spans = receiver.Spans();
        Assert.Equal(1_001, spans.Count);
        var server = spans.Server();
        Assert.Equal(
            Enumerable.Range(0, 1_000).Select(i => (long)i),
            spans
                .Where(span => span.Name == "child")
                .Select(span => (long)span.Attributes()["index"]!)
                .Order()
        );
        var logs = receiver.ApplicationLogs();
        Assert.Equal(
            ["Child 999", "Retained child", "Request"],
            logs.Select(log => log.Body.StringValue)
        );
        Assert.Equal(server.SpanId, logs[2].SpanId);
    }

    [Fact]
    public async Task SpansEndingAfterReleaseAreDropped()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        var lateSpanEnded = new TaskCompletionSource();
        await using var host = await ApplicationHost.StartMinimalAsync(
            receiver,
            configureApp: app =>
                app.MapGet(
                    "/fire-and-forget",
                    () =>
                    {
                        _ = Task.Run(async () =>
                        {
                            using (ApplicationSource.StartActivity("late"))
                                await Task.Delay(300);
                            lateSpanEnded.SetResult();
                        });
                        return "OK";
                    }
                )
        );

        await host.Client.GetAsync("/fire-and-forget");
        await lateSpanEnded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await host.StopAsync();

        Assert.Equal(["GET /fire-and-forget"], receiver.Spans().Select(span => span.Name));
    }

    [Fact]
    public async Task UnfinishedRequestsAtShutdownAreDiscarded()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        var requestStarted = new TaskCompletionSource();
        var releaseRequest = new TaskCompletionSource();
        await using var host = await ApplicationHost.StartMinimalAsync(
            receiver,
            configureApp: app =>
                app.MapGet(
                    "/slow",
                    async () =>
                    {
                        requestStarted.SetResult();
                        await releaseRequest.Task;
                        return "OK";
                    }
                )
        );

        await host.Client.GetAsync("/hello");
        var slowRequest = host.Client.GetAsync("/slow");
        await requestStarted.Task;
        await host
            .Services.GetRequiredService<TelemetryRuntime>()
            .ShutdownAsync(CancellationToken.None);
        releaseRequest.SetResult();
        await slowRequest;

        Assert.Equal(["GET /hello"], receiver.Spans().Select(span => span.Name));
    }
}
