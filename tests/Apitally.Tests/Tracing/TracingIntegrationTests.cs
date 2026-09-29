using System.Diagnostics;
using Apitally.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OtlpSpan = OpenTelemetry.Proto.Trace.V1.Span;

namespace Apitally.Tests.Tracing;

public class TracingIntegrationTests
{
    private static readonly ActivitySource ApplicationSource = new("TestApp.Application");

    [Fact]
    public async Task FallbackCapturesApplicationActivitiesOnlyWithinRequests()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        await using var host = await ApplicationHost.StartMinimalAsync(
            receiver,
            configureApp: app =>
                app.MapGet(
                    "/work",
                    () =>
                    {
                        using var activity = ApplicationSource.StartActivity("application-work");
                        return "OK";
                    }
                )
        );

        await host.Client.GetAsync("/work");
        using (var background = ApplicationSource.StartActivity("background-work"))
            Assert.False(background?.Recorded ?? false);
        await host.StopAsync();

        var spans = receiver.Spans();
        Assert.Equal(["application-work", "GET /work"], spans.Select(span => span.Name).Order());
        Assert.Equal(
            spans.Server().SpanId,
            spans.Single(span => span.Name == "application-work").ParentSpanId
        );
    }

    [Fact]
    public async Task OutgoingHttpCallYieldsOneClientSpan()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        using var outgoing = new HttpClient();
        await using var host = await ApplicationHost.StartMinimalAsync(
            receiver,
            configureApp: app =>
                app.MapGet(
                    "/outgoing",
                    async () =>
                    {
                        using var response = await outgoing.GetAsync(
                            new Uri(receiver.Endpoint, "/other")
                        );
                        return (int)response.StatusCode;
                    }
                )
        );

        await host.Client.GetAsync("/outgoing");
        await host.StopAsync();

        var spans = receiver.Spans();
        var client = Assert.Single(spans, span => span.Kind == OtlpSpan.Types.SpanKind.Client);
        Assert.Equal(spans.Server().TraceId, client.TraceId);
    }

    [Fact]
    public async Task FallbackCapturesRequestsWithUnsampledRemoteParent()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        await using var host = await ApplicationHost.StartMinimalAsync(receiver);
        var traceId = ActivityTraceId.CreateRandom();
        var parentId = ActivitySpanId.CreateRandom();

        var request = new HttpRequestMessage(HttpMethod.Get, "/hello");
        request.Headers.Add(
            "traceparent",
            $"00-{traceId.ToHexString()}-{parentId.ToHexString()}-00"
        );
        await host.Client.SendAsync(request);
        await host.StopAsync();

        var server = Assert.Single(receiver.Spans());
        Assert.Equal(traceId.ToHexString(), server.TraceId.Hex());
        Assert.Equal(parentId.ToHexString(), server.ParentSpanId.Hex());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ApplicationProviderKeepsItsPipelineAndSharesServerSpans(
        bool isApplicationFirst
    )
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        var applicationSpans = new List<Activity>();
        await using var host = await ApplicationHost.StartMinimalAsync(
            receiver,
            builder =>
            {
                void AddApplicationTracing() =>
                    builder
                        .Services.AddOpenTelemetry()
                        .ConfigureResource(resource => resource.AddService("orders"))
                        .WithTracing(tracing =>
                            tracing
                                .AddAspNetCoreInstrumentation()
                                .AddInMemoryExporter(applicationSpans)
                        );
                if (isApplicationFirst)
                    AddApplicationTracing();
                builder.Services.AddApitally(options =>
                {
                    options.CaptureRequestHeaders = true;
                    options.CaptureResponseBody = true;
                });
                if (!isApplicationFirst)
                    AddApplicationTracing();
            }
        );

        await host.Client.GetAsync("/items/1");
        await host.StopAsync();

        Assert.Equal(2, receiver.Spans().Count);
        Assert.Single(receiver.Spans(), span => span.Kind == OtlpSpan.Types.SpanKind.Server);
        var server = Assert.Single(
            applicationSpans,
            activity => activity.DisplayName == "GET /items/{id:int}"
        );
        Assert.Contains(applicationSpans, activity => activity.DisplayName == "load-item");
        Assert.True(receiver.Spans().Server().Attributes().ContainsKey("apitally.response.body"));
        Assert.DoesNotContain(
            server.TagObjects,
            tag => tag.Key.StartsWith("apitally.") || tag.Key.StartsWith("http.request.header.")
        );
        var resource = OtlpDecoding.Attributes(receiver.ResourceSpans()[0].Resource.Attributes);
        Assert.Equal("orders", resource["service.name"]);
        Assert.Equal("dev", resource["deployment.environment.name"]);
    }

    [Theory]
    [InlineData(SamplingDecision.Drop)]
    [InlineData(SamplingDecision.RecordOnly)]
    public async Task ApplicationSamplerGovernsExportedDetail(SamplingDecision decision)
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        await using var host = await ApplicationHost.StartMinimalAsync(
            receiver,
            builder =>
                builder
                    .Services.AddOpenTelemetry()
                    .WithTracing(tracing => tracing.SetSampler(new FixedSampler(decision)))
        );

        await host.Client.GetAsync("/items/1");
        await host.StopAsync();

        Assert.Empty(receiver.Spans());
    }

    [Fact]
    public async Task RequestsFilteredByApplicationInstrumentationAreReleased()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        await using var host = await ApplicationHost.StartMinimalAsync(
            receiver,
            builder =>
                builder
                    .Services.AddOpenTelemetry()
                    .WithTracing(tracing =>
                        tracing.AddAspNetCoreInstrumentation(options =>
                            options.Filter = context => context.Request.Path != "/items/1"
                        )
                    )
        );

        await host.Client.GetAsync("/items/1");
        await host.StopAsync();

        Assert.Empty(receiver.Spans());
    }

    [Fact]
    public async Task OtherLocalServerRootsAreNotExported()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        await using var host = await ApplicationHost.StartMinimalAsync(
            receiver,
            builder =>
                builder
                    .Services.AddOpenTelemetry()
                    .WithTracing(tracing => tracing.AddSource(ApplicationSource.Name)),
            app =>
                app.MapGet(
                    "/hub",
                    () =>
                    {
                        // Like a SignalR hub invocation: a parentless SERVER activity.
                        Activity.Current = null;
                        using var invocation = ApplicationSource.StartActivity(
                            "hub-invocation",
                            ActivityKind.Server
                        );
                        using (ApplicationSource.StartActivity("hub-child")) { }
                        return invocation?.Recorded;
                    }
                )
        );

        var recorded = await host.Client.GetStringAsync("/hub");
        await host.StopAsync();

        Assert.Equal("true", recorded);
        Assert.Equal(["GET /hub"], receiver.Spans().Select(span => span.Name));
    }

    [Fact]
    public async Task ExistingProviderInstanceIsJoinedAndNotDisposed()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        var applicationSpans = new List<Activity>();
        using var provider = Sdk.CreateTracerProviderBuilder()
            .AddAspNetCoreInstrumentation()
            .AddSource("apitally.otel", ApplicationSource.Name)
            .AddInMemoryExporter(applicationSpans)
            .Build();
        await using (
            var host = await ApplicationHost.StartMinimalAsync(
                receiver,
                builder => builder.Services.AddSingleton(provider)
            )
        )
        {
            await host.Client.GetAsync("/items/1");
        }

        using (ApplicationSource.StartActivity("after-host")) { }

        Assert.Equal(2, receiver.Spans().Count);
        Assert.Contains(applicationSpans, activity => activity.DisplayName == "after-host");
    }

    [Fact]
    public async Task ExportRequestsAreNotTracedByApplicationInstrumentation()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        var applicationSpans = new List<Activity>();
        using var outgoing = new HttpClient();
        await using var host = await ApplicationHost.StartMinimalAsync(
            receiver,
            builder =>
                builder
                    .Services.AddOpenTelemetry()
                    .WithTracing(tracing =>
                        tracing.AddHttpClientInstrumentation().AddInMemoryExporter(applicationSpans)
                    ),
            app =>
                app.MapGet(
                    "/outgoing",
                    async () =>
                        (await outgoing.GetAsync(new Uri(receiver.Endpoint, "/other"))).StatusCode
                )
        );

        await host.Client.GetAsync("/outgoing");
        await host.StopAsync();

        var urls = applicationSpans
            .Where(activity => activity.Kind == ActivityKind.Client)
            .Select(activity => activity.GetTagItem("url.full")?.ToString())
            .ToList();
        Assert.NotEmpty(receiver.Exports);
        Assert.Equal([new Uri(receiver.Endpoint, "/other").ToString()], urls);
    }

    private sealed class FixedSampler(SamplingDecision decision) : Sampler
    {
        public override SamplingResult ShouldSample(in SamplingParameters parameters) =>
            new(decision);
    }
}
