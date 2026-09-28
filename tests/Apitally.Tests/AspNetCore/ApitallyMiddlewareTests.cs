using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using Apitally.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;

namespace Apitally.Tests.AspNetCore;

public sealed class ApitallyMiddlewareTests : IAsyncDisposable
{
    private readonly string file = Path.GetTempFileName();
    private OtlpReceiver receiver = null!;
    private ApplicationHost host = null!;

    [Fact]
    public async Task RequestBodyReadThroughStreamIsCapturedAndRedacted()
    {
        await StartAsync();

        await PostJsonAsync("/items", """{"id":1,"name":"a","password":"p"}""");
        await host.StopAsync();

        var attributes = receiver.Spans().Server().Attributes();
        Assert.Equal(
            """{"id":1,"name":"a","password":"[REDACTED]"}""",
            attributes["apitally.request.body"]
        );
        Assert.Equal(34L, attributes["http.request.body.size"]);
    }

    [Fact]
    public async Task RequestBodyReadThroughPipeReaderIsCaptured()
    {
        await StartAsync();

        await PostJsonAsync("/read-pipe", """{"a":1}""", chunked: true);
        await host.StopAsync();

        var attributes = receiver.Spans().Server().Attributes();
        Assert.Equal("""{"a":1}""", attributes["apitally.request.body"]);
        Assert.Equal(7L, attributes["http.request.body.size"]);
    }

    [Fact]
    public async Task UnreadRequestBodyIsNeitherCapturedNorSizedWithoutLength()
    {
        await StartAsync();

        await PostJsonAsync("/ignore-body", """{"a":1}""", chunked: true);
        await PostJsonAsync("/ignore-body", """{"a":1}""");
        await host.StopAsync();

        var servers = receiver.Spans().Select(span => span.Attributes()).ToList();
        Assert.All(
            servers,
            attributes => Assert.False(attributes.ContainsKey("apitally.request.body"))
        );
        Assert.Equal(
            [null, 7L],
            servers
                .Select(attributes => attributes.GetValueOrDefault("http.request.body.size"))
                .OrderBy(size => size)
        );
    }

    [Fact]
    public async Task DisallowedContentTypeIsSizedButNotCaptured()
    {
        await StartAsync();

        using var content = new ByteArrayContent([1, 2, 3]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        await host.Client.PostAsync("/read-pipe", content);
        await host.StopAsync();

        var attributes = receiver.Spans().Server().Attributes();
        Assert.False(attributes.ContainsKey("apitally.request.body"));
        Assert.Equal(3L, attributes["http.request.body.size"]);
    }

    [Fact]
    public async Task DeclaredOversizedRequestYieldsSentinelWithoutReading()
    {
        await StartAsync();

        await PostJsonAsync("/ignore-body", new string('x', 60_000));
        await host.StopAsync();

        var attributes = receiver.Spans().Server().Attributes();
        Assert.Equal("[BODY_TOO_LARGE]", attributes["apitally.request.body"]);
        Assert.Equal(60_000L, attributes["http.request.body.size"]);
    }

    [Fact]
    public async Task ResponseBodiesAreCapturedFromStreamAndWriter()
    {
        await StartAsync();

        await ReadAsync("/write-stream");
        await ReadAsync("/write-pipe");
        await host.StopAsync();

        var bodies = receiver
            .Spans()
            .Select(span => span.Attributes())
            .Select(attributes =>
                (attributes["apitally.response.body"], attributes["http.response.body.size"])
            )
            .OrderBy(value => value.Item1)
            .ToList();
        Assert.Equal([("pipe text", 9L), ("stream text", 11L)], bodies);
    }

    [Fact]
    public async Task OversizedStreamedResponseYieldsSentinelAndFullSize()
    {
        await StartAsync();

        await ReadAsync("/large");
        await host.StopAsync();

        var attributes = receiver.Spans().Server().Attributes();
        Assert.Equal("[BODY_TOO_LARGE]", attributes["apitally.response.body"]);
        Assert.Equal(60_000L, attributes["http.response.body.size"]);
    }

    [Fact]
    public async Task CompressedResponseIsObservedOutsideCompressionAndDecoded()
    {
        await StartAsync(compress: true);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/write-stream");
        request.Headers.AcceptEncoding.ParseAdd("gzip");
        using var response = await host.Client.SendAsync(request);
        var compressed = await response.Content.ReadAsByteArrayAsync();
        await host.StopAsync();

        Assert.Equal("gzip", response.Content.Headers.ContentEncoding.Single());
        var attributes = receiver.Spans().Server().Attributes();
        Assert.Equal("stream text", attributes["apitally.response.body"]);
        Assert.Equal((long)compressed.Length, attributes["http.response.body.size"]);
    }

    [Fact]
    public async Task ResponseFailingAfterStartIsNotCaptured()
    {
        await StartAsync();

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => ReadAsync("/fail-mid-stream"));
        await host.StopAsync();

        var attributes = receiver.Spans().Server().Attributes();
        Assert.False(attributes.ContainsKey("apitally.response.body"));
        Assert.False(attributes.ContainsKey("http.response.body.size"));
    }

    [Fact]
    public async Task AbortedResponseIsNotCaptured()
    {
        await StartAsync();

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => ReadAsync("/abort"));
        await host.StopAsync();

        Assert.False(receiver.Spans().Server().Attributes().ContainsKey("apitally.response.body"));
    }

    [Fact]
    public async Task HandledErrorResponseBodyIsCaptured()
    {
        await StartAsync(useExceptionHandler: true);

        await ReadAsync("/error");
        await host.StopAsync();

        var attributes = receiver.Spans().Server().Attributes();
        Assert.Equal(500L, attributes["http.response.status_code"]);
        Assert.Equal("/error", attributes["http.route"]);
        Assert.Equal("""{"error":"handled"}""", attributes["apitally.response.body"]);
    }

    [Fact]
    public async Task NativeFileSendsAreDeliveredUnchangedWithoutCapture()
    {
        await File.WriteAllTextAsync(file, "file text");
        await StartAsync();

        var fileBody = await ReadAsync("/file");
        var mixedBody = await ReadAsync("/mixed-file");
        await host.StopAsync();

        Assert.Equal("file text", fileBody);
        Assert.Equal("prefix:file text", mixedBody);
        var servers = receiver.Spans().Select(span => span.Attributes()).ToList();
        Assert.All(
            servers,
            attributes => Assert.False(attributes.ContainsKey("apitally.response.body"))
        );
        // The file's length is not read from disk when the send does not state it.
        Assert.Equal(
            [null, 9L],
            servers
                .Select(attributes => attributes.GetValueOrDefault("http.response.body.size"))
                .OrderBy(size => size)
        );
    }

    [Fact]
    public async Task HeadersAreCapturedPerDirectionAndRedacted()
    {
        await StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/write-stream");
        request.Headers.Add("X-Api-Key", "secret");
        request.Headers.Add("X-Trace", "1");
        await host.Client.SendAsync(request);
        await host.StopAsync();

        var attributes = receiver.Spans().Server().Attributes();
        Assert.Equal(new object?[] { "[REDACTED]" }, attributes["http.request.header.x-api-key"]);
        Assert.Equal(new object?[] { "1" }, attributes["http.request.header.x-trace"]);
        Assert.Equal(
            new object?[] { "text/plain" },
            attributes["http.response.header.content-type"]
        );
    }

    public async ValueTask DisposeAsync()
    {
        await host.DisposeAsync();
        await receiver.DisposeAsync();
        File.Delete(file);
    }

    private async Task StartAsync(bool compress = false, bool useExceptionHandler = false)
    {
        receiver = await OtlpReceiver.StartAsync();
        host = await ApplicationHost.StartMinimalAsync(
            receiver,
            builder =>
            {
                builder.Services.AddApitally(options =>
                {
                    options.CaptureRequestBody = true;
                    options.CaptureResponseBody = true;
                    options.CaptureRequestHeaders = true;
                });
                if (compress)
                    builder.Services.AddResponseCompression(options =>
                    {
                        options.MimeTypes = ["text/plain"];
                        options.Providers.Add<GzipCompressionProvider>();
                    });
            },
            app =>
            {
                if (compress)
                    app.UseResponseCompression();
                if (useExceptionHandler)
                    app.UseExceptionHandler(error =>
                        error.Run(context =>
                        {
                            context.Response.ContentType = "application/json";
                            return context.Response.WriteAsync("""{"error":"handled"}""");
                        })
                    );
                MapRoutes(app);
            }
        );
    }

    private void MapRoutes(WebApplication app)
    {
        app.MapPost(
            "/read-pipe",
            async (HttpContext context) =>
            {
                while (true)
                {
                    var result = await context.Request.BodyReader.ReadAsync();
                    context.Request.BodyReader.AdvanceTo(result.Buffer.End);
                    if (result.IsCompleted)
                        return "OK";
                }
            }
        );
        app.MapPost("/ignore-body", () => "OK");
        app.MapGet(
            "/write-stream",
            async (HttpContext context) =>
            {
                context.Response.ContentType = "text/plain";
                await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("stream text"));
            }
        );
        app.MapGet(
            "/write-pipe",
            async (HttpContext context) =>
            {
                context.Response.ContentType = "text/plain";
                var bytes = Encoding.UTF8.GetBytes("pipe text");
                bytes.CopyTo(context.Response.BodyWriter.GetSpan(bytes.Length));
                context.Response.BodyWriter.Advance(bytes.Length);
                await context.Response.BodyWriter.FlushAsync();
            }
        );
        app.MapGet(
            "/large",
            async (HttpContext context) =>
            {
                context.Response.ContentType = "text/plain";
                for (var i = 0; i < 6; i++)
                    await context.Response.WriteAsync(new string('x', 10_000));
            }
        );
        app.MapGet(
            "/fail-mid-stream",
            async (HttpContext context) =>
            {
                context.Response.ContentType = "text/plain";
                await context.Response.WriteAsync("partial");
                await context.Response.Body.FlushAsync();
                throw new InvalidOperationException("Failed after start");
            }
        );
        app.MapGet(
            "/abort",
            async (HttpContext context) =>
            {
                context.Response.ContentType = "text/plain";
                context.Response.ContentLength = 100;
                await context.Response.WriteAsync("partial");
                context.Abort();
            }
        );
        app.MapGet("/file", () => Results.File(file, "text/plain"));
        app.MapGet(
            "/mixed-file",
            async (HttpContext context) =>
            {
                context.Response.ContentType = "text/plain";
                await context.Response.WriteAsync("prefix:");
                await context.Response.SendFileAsync(file);
            }
        );
    }

    private async Task PostJsonAsync(string path, string json, bool chunked = false)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        if (chunked)
            content.Headers.ContentLength = null;
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        request.Headers.TransferEncodingChunked = chunked;
        using var response = await host.Client.SendAsync(request);
        await response.Content.ReadAsStringAsync();
    }

    private async Task<string> ReadAsync(string path)
    {
        using var response = await host.Client.GetAsync(path);
        return await response.Content.ReadAsStringAsync();
    }
}
