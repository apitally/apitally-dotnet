using System.Net.WebSockets;
using Apitally.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OpenTelemetry.Proto.Trace.V1;

namespace Apitally.Tests.Integration;

// Scenarios spanning request capture through OTLP delivery to a loopback receiver.
public class RequestTelemetryTests
{
    [Fact]
    public async Task RequestExportsServerSpanWithFinalHttpAttributesAndChildSpan()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        await using var host = await ApplicationHost.StartMinimalAsync(receiver);

        var response = await host.Client.GetAsync("/items/5?token=secret&page=1");
        await response.Content.ReadAsStringAsync();
        await host.StopAsync();

        var spans = receiver.Spans();
        Assert.Equal(2, spans.Count);
        var server = spans.Server();
        var attributes = server.Attributes();
        Assert.Equal("GET", attributes["http.request.method"]);
        Assert.Equal("/items/{id:int}", attributes["http.route"]);
        Assert.Equal(200L, attributes["http.response.status_code"]);
        Assert.Equal("/items/5", attributes["url.path"]);
        Assert.Equal("token=[REDACTED]&page=1", attributes["url.query"]);
        Assert.Equal("http", attributes["url.scheme"]);
        Assert.Equal(5L, attributes["item.id"]);
        var child = Assert.Single(spans, span => span.Kind == Span.Types.SpanKind.Internal);
        Assert.Equal("load-item", child.Name);
        Assert.Equal(server.SpanId, child.ParentSpanId);
        var resource = OtlpDecoding.Attributes(receiver.ResourceSpans()[0].Resource.Attributes);
        Assert.Equal("prod", resource["deployment.environment.name"]);
    }

    [Fact]
    public async Task GroupRoutesIncludeThePrefixWithoutTrailingSlash()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        await using var host = await ApplicationHost.StartMinimalAsync(receiver);

        await host.Client.GetAsync("/api/v1/");
        await host.Client.GetAsync("/api/v1/orders/7");
        await host.StopAsync();

        Assert.Equal(
            ["/api/v1", "/api/v1/orders/{orderId}"],
            receiver.Spans().Select(span => span.Attributes()["http.route"]).Order()
        );
    }

    [Fact]
    public async Task UnmatchedRequestExportsServerSpanWithoutRoute()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        await using var host = await ApplicationHost.StartMinimalAsync(receiver);

        var response = await host.Client.GetAsync("/missing");
        await host.StopAsync();

        Assert.Equal(404, (int)response.StatusCode);
        var attributes = Assert.Single(receiver.Spans()).Attributes();
        Assert.False(attributes.ContainsKey("http.route"));
        Assert.Equal(404L, attributes["http.response.status_code"]);
    }

    [Fact]
    public async Task ExceptionIsRecordedOnceOnServerSpan()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        await using var host = await ApplicationHost.StartMinimalAsync(receiver);

        var response = await host.Client.GetAsync("/error");
        await host.StopAsync();

        Assert.Equal(500, (int)response.StatusCode);
        var server = Assert.Single(receiver.Spans());
        var exceptionEvent = Assert.Single(server.Events);
        Assert.Equal("exception", exceptionEvent.Name);
        var eventAttributes = OtlpDecoding.Attributes(exceptionEvent.Attributes);
        Assert.Equal("System.InvalidOperationException", eventAttributes["exception.type"]);
        Assert.Equal("Test error", eventAttributes["exception.message"]);
        Assert.Contains("Test error", (string)eventAttributes["exception.stacktrace"]!);
        Assert.Equal("/error", server.Attributes()["http.route"]);
    }

    [Fact]
    public async Task ExcludedAndOptionsRequestsExportNoSpans()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        await using var host = await ApplicationHost.StartMinimalAsync(
            receiver,
            builder => builder.Services.AddApitally(options => options.ExcludePaths = ["^/hello$"])
        );

        await host.Client.GetAsync("/hello");
        await host.Client.SendAsync(new HttpRequestMessage(HttpMethod.Options, "/items/1"));
        await host.Client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/items/1")
            {
                Headers = { { "User-Agent", "kube-probe/1.29" } },
            }
        );
        await host.Client.GetAsync("/healthz");
        await host.StopAsync();

        Assert.Empty(receiver.Spans());
    }

    [Fact]
    public async Task WebSocketRequestsProduceNoRequestTelemetry()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        await using var host = await ApplicationHost.StartMinimalAsync(
            receiver,
            configureApp: app =>
            {
                app.UseWebSockets();
                app.Map(
                    "/ws",
                    async (HttpContext context, IApitally apitally) =>
                    {
                        apitally.SetConsumer("socket-consumer", name: "Socket");
                        using var socket = await context.WebSockets.AcceptWebSocketAsync();
                        await socket.ReceiveAsync(new byte[16], default);
                        await socket.CloseOutputAsync(
                            WebSocketCloseStatus.NormalClosure,
                            null,
                            default
                        );
                    }
                );
            }
        );

        using (var client = new ClientWebSocket())
        {
            await client.ConnectAsync(
                new Uri(host.Client.BaseAddress!, "/ws").ToWebSocketUri(),
                default
            );
            await client.CloseAsync(WebSocketCloseStatus.NormalClosure, null, default);
        }
        await host.StopAsync();

        Assert.Empty(receiver.Spans());
        Assert.Empty(receiver.Events("apitally.consumer.update"));
        Assert.Empty(receiver.Metrics("http.server.request.duration"));
    }

    [Fact]
    public async Task StartupHostExportsRequestSpans()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        await using var host = await ApplicationHost.StartStartupAsync(receiver);

        await host.Client.GetAsync("/controller/items/3");
        await host.StopAsync();

        var attributes = Assert.Single(receiver.Spans()).Attributes();
        Assert.Equal("/controller/items/{id:int}", attributes["http.route"]);
    }
}

internal static class UriExtensions
{
    public static Uri ToWebSocketUri(this Uri uri) =>
        new UriBuilder(uri) { Scheme = uri.Scheme == "https" ? "wss" : "ws" }.Uri;
}
