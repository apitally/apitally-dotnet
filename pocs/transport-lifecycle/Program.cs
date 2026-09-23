using System.Buffers;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.ResponseCompression;
using TransportLifecycle;

Console.WriteLine(
    $"Runtime {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}; ASP.NET {typeof(WebApplication).Assembly.GetName().Version}"
);
foreach (var modern in new[] { true, false })
{
    await Scenarios.TransportAndGracefulShutdown(modern);
    await Scenarios.ActiveRequestExceedsBudget(modern);
    await Scenarios.FinalDrainUsesBudget(modern);
}
Console.WriteLine($"PASS: {Check.Assertions} assertions");

internal static class Scenarios
{
    public static async Task TransportAndGracefulShutdown(bool modern)
    {
        await using var fixture = await Fixture.Start(modern, TimeSpan.FromSeconds(5));
        var state = fixture.State;
        foreach (
            var (path, expected) in new[]
            {
                ("/body", "body"),
                ("/sync", "sync"),
                ("/writer", "writer"),
                ("/unflushed", "unflushed"),
                ("/controller/42", "controller:42"),
            }
        )
        {
            Check.Equal(expected, await fixture.Client.GetStringAsync(path), path + " client body");
            var record = await fixture.Finished(path);
            Check.Equal(
                expected,
                Text(record.Response.Captured(record.ResponseComplete)),
                path + " captured body"
            );
            Check.Equal((long)expected.Length, record.Response.Count, path + " byte count");
            Check.True(record.EntryRoute == null, path + " startup-filter precedes routing");
            Check.Equal(
                path.StartsWith("/controller") ? "controller/{id:int}" : "/{mode}",
                record.OriginalRoute,
                path + " parameterized final route"
            );
        }
        Check.Before(state, "/body app.starting[/body]", "/body observer.starting");
        Check.Before(state, "/body app.finally[/body]", "/body observer.finally");
        Check.Before(state, "/body observer.finally", "/body app.completed[/body]");
        Check.Before(state, "/body app.completed[/body]", "/body observer.completed");
        Check.Before(state, "/body observer.completed", "/body activity.stopped");
        Check.Before(state, "/unflushed observer.finally", "/unflushed observer.starting");
        Check.True(
            state.Has("/writer feature.start")
                && state.Has("/writer writer.flush")
                && state.Has("/writer feature.complete.exit"),
            "feature paths exercised"
        );

        using (var response = await fixture.Client.GetAsync("/fail/42"))
        {
            Check.Equal(500, (int)response.StatusCode, "exception final status");
            Check.Equal(
                "{\"error\":\"handled\"}",
                await response.Content.ReadAsStringAsync(),
                "exception final body"
            );
        }
        var failed = await fixture.Finished("/fail/42");
        Check.Equal("/fail/{id:int}", failed.OriginalRoute, "original route survives re-execution");
        Check.Equal("/error", failed.FinalEndpointRoute, "error route selected");
        Check.Equal("/fail/42", failed.OriginalPath, "original path survives re-execution");
        Check.Equal(
            "{\"error\":\"handled\"}",
            Text(failed.Response.Captured(failed.ResponseComplete)),
            "observer captures final handler body"
        );
        Check.True(!failed.Escaped, "outer catch does not see handled exception");
        using (var response = await fixture.Client.GetAsync("/unmatched/extra/path"))
            Check.Equal(404, (int)response.StatusCode, "unmatched status");
        Check.True(
            (await fixture.Finished("/unmatched/extra/path")).OriginalRoute == null,
            "unmatched has no route"
        );

        foreach (var mode in new[] { "body", "writer", "compressed" })
            await Streaming(fixture, mode);

        Check.Equal("2345", await fixture.Client.GetStringAsync("/file"), "native file range");
        var file = await fixture.Finished("/file");
        Check.Equal(4L, file.Response.Count, "file count");
        Check.True(
            file.Response.Missing && file.Response.Captured(file.ResponseComplete) == null,
            "file content deliberately not captured"
        );
        foreach (var (path, length) in new[] { ("/large", 60_000), ("/cross-cap", 65_536) })
        {
            Check.Equal(
                length,
                (await fixture.Client.GetByteArrayAsync(path)).Length,
                path + " delivered"
            );
            var record = await fixture.Finished(path);
            Check.Equal((long)length, record.Response.Count, path + " complete count");
            Check.True(
                record.Response.TooLarge && record.Response.Retained == 0,
                path + " sentinel discards bytes"
            );
            Check.True(record.Response.PeakRetained <= BodyObservation.Limit, path + " bounded");
            if (path == "/large")
                Check.Equal(0, record.Response.PeakRetained, "declared oversized skips buffering");
        }
        await RequestBodies(fixture);
        await AbortedResponse(fixture, "/aborted", 7);
        await AbortedResponse(fixture, "/aborted-large", 50_001);
        await ClientDisconnect(fixture);
        var shortReadFailed = false;
        try
        {
            await fixture.Client.GetByteArrayAsync("/short");
        }
        catch (HttpRequestException)
        {
            shortReadFailed = true;
        }
        Check.True(shortReadFailed, "client rejects Content-Length mismatch");
        var shortBody = await fixture.Finished("/short");
        Check.True(
            !shortBody.ResponseComplete
                && shortBody.Response.Captured(shortBody.ResponseComplete) == null,
            "OnCompleted alone does not prove complete body"
        );

        var responseTask = fixture.Client.GetAsync("/hold-shutdown");
        var held = await state.Wait("/hold-shutdown");
        await held.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = fixture.Stop();
        await state.ServerStopping.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check.True(
            !stop.IsCompleted && !held.ActivityEnded && !held.TransportEnded,
            "server waits for active request"
        );
        held.Gate.TrySetResult();
        using (var response = await responseTask)
            Check.Equal(
                "done",
                await response.Content.ReadAsStringAsync(),
                "active request drains"
            );
        await held.Finish();
        await stop.WaitAsync(TimeSpan.FromSeconds(8));
        Check.Equal(0, state.UnresolvedAtFinalDrain, "final phase sees all completed requests");
        Check.Equal(
            state.Records.Count(),
            state.ReleasedAtFinalDrain,
            "completed request drain count"
        );
        Check.Equal(1, state.FinalDrains, "one final drain");
        Check.Before(state, "/hold-shutdown joined", "server.stop.exit");
        Check.Before(state, "server.stop.exit", "lifecycle.stopped.enter");
        Check.Before(state, "lifecycle.stopped.exit", "host.stopped");
        Check.Before(
            state,
            modern ? "server.stop.exit" : "lifecycle.stop",
            modern ? "lifecycle.stop" : "server.stop.enter"
        );
        Check.Equal(modern ? "host" : "request", state.ActivationTrigger, "activation trigger");
        Check.Equal(1, state.Activations, "single activation");
        if (!modern)
            Check.Before(state, "/startup observer.enter", "host.started");
        state.Print(
            fixture.Label + " normal",
            "/startup",
            "/body",
            "/unflushed",
            "/fail/42",
            "/stream/compressed",
            "/short",
            "/hold-shutdown"
        );
        Console.WriteLine(
            $"PASS {fixture.Label}: {state.Records.Count()} requests, {state.Released} joined, final drain {state.ReleasedAtFinalDrain}"
        );
    }

    public static async Task ActiveRequestExceedsBudget(bool modern)
    {
        await using var fixture = await Fixture.Start(modern, TimeSpan.FromMilliseconds(300));
        using var response = await fixture.Client.GetAsync(
            "/budget",
            HttpCompletionOption.ResponseHeadersRead
        );
        var record = await fixture.State.Wait("/budget");
        await record.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await fixture.Stop().WaitAsync(TimeSpan.FromSeconds(5));
        Check.True(
            watch.ElapsedMilliseconds >= 150 && watch.ElapsedMilliseconds < 4000,
            "host shutdown budget bounds server wait"
        );
        Check.True(
            fixture.State.FinalBudgetCancelled,
            "final phase receives cancelled host budget"
        );
        Check.Equal(
            1,
            fixture.State.UnresolvedAtFinalDrain,
            "uncooperative request remains unresolved at budget expiry"
        );
        Check.True(
            !record.ActivityEnded && !record.TransportEnded,
            "host stop does not finish application activity"
        );
        record.Gate.TrySetResult();
        await record.Finish();
        Check.True(
            record.Aborted && !record.ResponseComplete,
            "aborted partial response omitted after handler returns"
        );
        fixture.State.Print(
            fixture.Label + $" active budget ({watch.ElapsedMilliseconds}ms)",
            "/budget"
        );
    }

    public static async Task FinalDrainUsesBudget(bool modern)
    {
        await using var fixture = await Fixture.Start(modern, TimeSpan.FromMilliseconds(300));
        await fixture.Client.GetStringAsync("/body");
        await fixture.Finished("/body");
        fixture.State.DelayFinalDrain = true;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await fixture.Stop().WaitAsync(TimeSpan.FromSeconds(5));
        Check.True(
            fixture.State.FinalBudgetCancelled,
            "slow final drain observes host cancellation"
        );
        Check.Equal(
            0,
            fixture.State.UnresolvedAtFinalDrain,
            "completed requests available before slow final drain"
        );
        Check.True(
            watch.ElapsedMilliseconds >= 150 && watch.ElapsedMilliseconds < 4000,
            "final drain obeys remaining host budget"
        );
        fixture.State.Print(fixture.Label + " final-drain budget");
    }

    private static async Task Streaming(Fixture fixture, string mode)
    {
        var path = "/stream/" + mode;
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (mode == "compressed")
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        using var response = await fixture.Client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead
        );
        var record = await fixture.State.Wait(path);
        await using var raw = await response.Content.ReadAsStreamAsync();
        await using var decoded =
            mode == "compressed"
                ? new GZipStream(raw, CompressionMode.Decompress, leaveOpen: true)
                : null;
        var stream = (Stream?)decoded ?? raw;
        var prefix = new byte[6];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await stream.ReadExactlyAsync(prefix, timeout.Token);
        record.Note("client.first-bytes");
        Check.Equal(
            "first\n",
            Encoding.UTF8.GetString(prefix),
            mode + " flushed bytes before return"
        );
        Check.True(
            !record.Gate.Task.IsCompleted && !fixture.State.Has(path + " endpoint.return"),
            mode + " endpoint still blocked"
        );
        record.Gate.TrySetResult();
        using var remaining = new MemoryStream();
        await stream.CopyToAsync(remaining, timeout.Token);
        Check.Equal(
            "last\n",
            Encoding.UTF8.GetString(remaining.ToArray()),
            mode + " stream remainder"
        );
        await record.Finish();
        Check.Before(fixture.State, path + " client.first-bytes", path + " endpoint.return");
        var captured = record.Response.Captured(record.ResponseComplete)!;
        if (mode == "compressed")
        {
            Check.Equal("gzip", record.Encoding, "outer observer sees gzip");
            Check.True(record.ResponseLength == null, "compression removes content length");
            using var unzip = new GZipStream(
                new MemoryStream(captured),
                CompressionMode.Decompress
            );
            using var result = new MemoryStream();
            await unzip.CopyToAsync(result);
            Check.Equal(
                "first\nlast\n",
                Text(result.ToArray()),
                "captured gzip includes final trailer"
            );
            Check.Equal(
                (long)captured.Length,
                record.Response.Count,
                "compressed content byte size"
            );
            Console.WriteLine(
                $"COMPRESSION {fixture.Label}: logical=11 observed={record.Response.Count}"
            );
        }
        else
            Check.Equal("first\nlast\n", Text(captured), mode + " capture complete");
    }

    private static async Task RequestBodies(Fixture fixture)
    {
        foreach (
            var (path, body, chunked) in new[]
            {
                ("/request/stream", "stream-request", false),
                ("/request/reader", "reader-request", true),
                ("/request/reader-large", new string('x', 60_001), true),
                ("/request/unread", new string('x', 60_001), false),
                ("/request/partial", "0123456789", false),
            }
        )
        {
            using HttpContent content = chunked
                ? new ChunkedContent(body)
                : new StringContent(body, Encoding.UTF8, "text/plain");
            using var response = await fixture.Client.PostAsync(path, content);
            Check.Equal(200, (int)response.StatusCode, path + " response");
            var record = await fixture.Finished(path);
            if (path == "/request/unread")
            {
                Check.Equal(0L, record.Request.Count, "unread request remains unread by observer");
                Check.True(
                    record.Request.TooLarge && record.Request.PeakRetained == 0,
                    "header-only oversized sentinel"
                );
                Check.Equal(60_001L, record.RequestLength, "known unread request size");
            }
            else if (path == "/request/partial")
            {
                Check.Equal(3L, record.Request.Count, "partial request observed bytes");
                Check.True(
                    !record.Request.Complete
                        && record.Request.Captured(record.Request.Complete) == null,
                    "unread suffix prevents complete capture"
                );
                Check.Equal(
                    10L,
                    record.RequestLength,
                    "declared length remains independently known"
                );
            }
            else
            {
                Check.True(record.Request.Complete, path + " complete");
                Check.Equal((long)body.Length, record.Request.Count, path + " consumed once");
                if (body.Length <= BodyObservation.Limit)
                    Check.Equal(body, Text(record.Request.Captured(true)), path + " capture");
                else
                    Check.True(
                        record.Request.TooLarge && record.Request.Retained == 0,
                        "large chunked request count survives discard"
                    );
                if (chunked)
                    Check.True(
                        record.RequestLength == null,
                        "chunked request size from observed completion"
                    );
            }
        }
        using (var socket = new TcpClient())
        {
            await socket
                .ConnectAsync(IPAddress.Loopback, fixture.Address.Port)
                .WaitAsync(TimeSpan.FromSeconds(5));
            var bytes = Encoding.ASCII.GetBytes(
                "POST /request/aborted HTTP/1.1\r\nHost: localhost\r\nContent-Type: text/plain\r\nTransfer-Encoding: chunked\r\n\r\n4\r\npart\r\n"
            );
            await socket.GetStream().WriteAsync(bytes);
            var record = await fixture.State.Wait("/request/aborted");
            await record.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            socket.Client.Shutdown(SocketShutdown.Both);
        }
        var aborted = await fixture.Finished("/request/aborted");
        Check.Equal(4L, aborted.Request.Count, "aborted request observed prefix");
        Check.True(
            !aborted.Request.Complete && aborted.Request.Captured(false) == null,
            "aborted request omits prefix"
        );
        Check.True(aborted.RequestLength == null, "aborted chunked request size stays unknown");
    }

    private static async Task ClientDisconnect(Fixture fixture)
    {
        using (var socket = new TcpClient())
        {
            await socket
                .ConnectAsync(IPAddress.Loopback, fixture.Address.Port)
                .WaitAsync(TimeSpan.FromSeconds(5));
            await socket
                .GetStream()
                .WriteAsync("GET /client-abort HTTP/1.1\r\nHost: localhost\r\n\r\n"u8.ToArray());
            var record = await fixture.State.Wait("/client-abort");
            await record.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var first = new byte[1024];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Check.True(
                await socket.GetStream().ReadAsync(first, timeout.Token) > 0,
                "client receives data before disconnect"
            );
            socket.Client.LingerState = new LingerOption(true, 0);
        }
        var aborted = await fixture.Finished("/client-abort");
        Check.True(aborted.Aborted && !aborted.ResponseComplete, "client disconnect recognized");
        Check.Equal(7L, aborted.Response.Count, "client-disconnected prefix count");
        Check.True(
            aborted.Response.Captured(aborted.ResponseComplete) == null,
            "client-disconnected partial payload omitted"
        );
    }

    private static async Task AbortedResponse(Fixture fixture, string path, int expected)
    {
        using var response = await fixture.Client.GetAsync(
            path,
            HttpCompletionOption.ResponseHeadersRead
        );
        var record = await fixture.State.Wait(path);
        await record.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        record.Gate.TrySetResult();
        try
        {
            await response.Content.ReadAsByteArrayAsync();
        }
        catch (HttpRequestException) { }
        await record.Finish();
        Check.True(
            record.Aborted && !record.ResponseComplete,
            path + " abort recognized despite OnCompleted"
        );
        Check.Equal(
            (long)expected,
            record.Response.Count,
            path + " prefix count is not complete size"
        );
        Check.True(
            record.Response.Captured(record.ResponseComplete) == null,
            path + " partial bytes omitted"
        );
        Check.Equal(
            expected > BodyObservation.Limit,
            record.Response.TooLarge,
            path + " established sentinel survives abort"
        );
        Console.WriteLine(
            $"ABORT {fixture.Label} {path}: feature={fixture.State.Has(path + " feature.abort")} token-at-completion={record.AbortTokenAtCompletion}"
        );
    }

    private static string? Text(byte[]? bytes) =>
        bytes == null ? null : Encoding.UTF8.GetString(bytes);
}

internal sealed class Fixture : IAsyncDisposable
{
    private readonly IHost host;
    private bool stopped;
    public ProbeState State { get; }
    public string Label { get; }
    public Uri Address { get; }
    public HttpClient Client { get; }

    private Fixture(IHost host, ProbeState state, string label)
    {
        this.host = host;
        State = state;
        Label = label;
        Address = new Uri(
            host.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!
                .Addresses.Single()
        );
        Client = new HttpClient(
            new SocketsHttpHandler
            {
                UseProxy = false,
                AutomaticDecompression = DecompressionMethods.None,
            }
        )
        {
            BaseAddress = Address,
            Timeout = TimeSpan.FromSeconds(8),
        };
    }

    public static async Task<Fixture> Start(bool modern, TimeSpan shutdownTimeout)
    {
        var state = new ProbeState
        {
            FilePath = Path.Combine(AppContext.BaseDirectory, "probe-file.txt"),
        };
        await File.WriteAllTextAsync(state.FilePath, "0123456789");
        IHost host;
        if (modern)
        {
            var builder = WebApplication.CreateBuilder(
                new WebApplicationOptions { EnvironmentName = "Production", Args = [] }
            );
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(o =>
            {
                o.Listen(IPAddress.Loopback, 0);
                o.AllowSynchronousIO = true;
            });
            ConfigureServices(builder.Services, shutdownTimeout);
            builder.AddTransportPoc(state);
            var app = builder.Build();
            ConfigurePipeline(app);
            Endpoints.Map(app);
            host = app;
        }
        else
        {
            host = Host.CreateDefaultBuilder([])
                .UseEnvironment("Production")
                .ConfigureLogging(l => l.ClearProviders())
                .ConfigureWebHostDefaults(web =>
                    web.ConfigureKestrel(o =>
                        {
                            o.Listen(IPAddress.Loopback, 0);
                            o.AllowSynchronousIO = true;
                        })
                        .ConfigureServices(s => ConfigureServices(s, shutdownTimeout))
                        .UseStartup<Startup>()
                )
                .AddTransportPoc(state)
                .ConfigureServices(s => s.AddHostedService<EarlyStartupRequest>())
                .Build();
        }
        Check.Equal(0, state.Activations, "building host does not activate");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await host.StartAsync(timeout.Token);
            return new Fixture(host, state, modern ? "WebApplication" : "GenericHost+Startup");
        }
        catch
        {
            host.Dispose();
            state.Dispose();
            throw;
        }
    }

    public async Task<RequestRecord> Finished(string path)
    {
        var record = await State.Wait(path);
        await record.Finish();
        return record;
    }

    public async Task Stop()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await host.StopAsync(timeout.Token);
        stopped = true;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var record in State.Records)
            record.Gate.TrySetResult();
        if (!stopped)
            await Stop();
        Client.Dispose();
        host.Dispose();
        Check.Before(State, "host.stopped", "server.dispose");
        State.Note("listener.dispose");
        State.Dispose();
        File.Delete(State.FilePath);
        Console.WriteLine($"DISPOSE {Label}: host.stopped -> server.dispose -> listener.dispose");
    }

    public static void ConfigurePipeline(IApplicationBuilder app)
    {
        app.UseExceptionHandler("/error");
        app.UseResponseCompression();
        app.Use(
            async (context, next) =>
            {
                var record = context.Features.Get<RequestRecord>()!;
                var path = context.Request.Path;
                context.Response.OnStarting(() =>
                {
                    record.Note($"app.starting[{path}]");
                    return Task.CompletedTask;
                });
                context.Response.OnCompleted(() =>
                {
                    record.Note($"app.completed[{path}]");
                    return Task.CompletedTask;
                });
                try
                {
                    await next(context);
                }
                finally
                {
                    record.Note($"app.finally[{path}]");
                }
            }
        );
    }

    private static void ConfigureServices(IServiceCollection services, TimeSpan timeout)
    {
        services.AddControllers();
        services.AddResponseCompression(o => o.Providers.Add<GzipCompressionProvider>());
        services.Configure<HostOptions>(o => o.ShutdownTimeout = timeout);
    }
}

public sealed class Startup
{
    public void Configure(IApplicationBuilder app)
    {
        Fixture.ConfigurePipeline(app);
        app.UseRouting();
        app.UseEndpoints(Endpoints.Map);
    }
}

internal static class Endpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapControllers();
        endpoints.MapGet(
            "/fail/{id:int}",
            (RequestDelegate)(_ => Task.FromException(new InvalidOperationException("synthetic")))
        );
        endpoints.MapGet(
            "/error",
            async context =>
            {
                context.Response.ContentType = "application/json";
                await context.Response.Body.WriteAsync("{\"error\":\"handled\"}"u8.ToArray());
            }
        );
        endpoints.MapGet(
            "/{mode}",
            async context =>
            {
                var mode = context.Request.RouteValues["mode"]!.ToString();
                var record = context.Features.Get<RequestRecord>()!;
                context.Response.ContentType = "text/plain";
                switch (mode)
                {
                    case "startup":
                        await context.Response.WriteAsync("startup");
                        break;
                    case "body":
                        await context.Response.Body.WriteAsync("body"u8.ToArray());
                        break;
                    case "sync":
                        context.Response.Body.Write("sync"u8);
                        context.Response.Body.Flush();
                        break;
                    case "writer":
                        await context.Response.StartAsync();
                        "writer"u8.CopyTo(context.Response.BodyWriter.GetSpan(6));
                        context.Response.BodyWriter.Advance(6);
                        await context.Response.BodyWriter.FlushAsync();
                        await context.Response.CompleteAsync();
                        break;
                    case "unflushed":
                        "unflushed"u8.CopyTo(context.Response.BodyWriter.GetMemory(9).Span);
                        context.Response.BodyWriter.Advance(9);
                        break;
                    case "file":
                        await context.Response.SendFileAsync(
                            context.RequestServices.GetRequiredService<ProbeState>().FilePath,
                            2,
                            4
                        );
                        break;
                    case "large":
                        context.Response.ContentLength = 60_000;
                        await context.Response.Body.WriteAsync(new byte[60_000]);
                        break;
                    case "cross-cap":
                        for (var i = 0; i < 8; i++)
                            await context.Response.Body.WriteAsync(new byte[8192]);
                        break;
                    case "short":
                        context.Response.ContentLength = 10;
                        await context.Response.Body.WriteAsync("part"u8.ToArray());
                        break;
                    case "hold-shutdown":
                        record.Waiting.TrySetResult();
                        await record.Gate.Task.WaitAsync(TimeSpan.FromSeconds(8));
                        await context.Response.WriteAsync("done");
                        break;
                    case "client-abort":
                        await context.Response.Body.WriteAsync(new byte[7]);
                        await context.Response.Body.FlushAsync();
                        record.Waiting.TrySetResult();
                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(8), context.RequestAborted);
                        }
                        catch (OperationCanceledException) { }
                        break;
                    case "budget":
                    case "aborted":
                    case "aborted-large":
                        await context.Response.Body.WriteAsync(
                            new byte[mode == "aborted-large" ? 50_001 : 7]
                        );
                        await context.Response.Body.FlushAsync();
                        record.Waiting.TrySetResult();
                        await record.Gate.Task.WaitAsync(TimeSpan.FromSeconds(8));
                        if (mode != "budget")
                            context.Abort();
                        break;
                    default:
                        context.Response.StatusCode = 404;
                        context.SetEndpoint(null);
                        break;
                }
            }
        );
        endpoints.MapGet(
            "/stream/{mode}",
            async context =>
            {
                var record = context.Features.Get<RequestRecord>()!;
                context.Response.ContentType = "text/plain";
                context.Features.Get<IHttpResponseBodyFeature>()!.DisableBuffering();
                if (context.Request.RouteValues["mode"]!.ToString() == "writer")
                {
                    await context.Response.BodyWriter.WriteAsync("first\n"u8.ToArray());
                    await context.Response.BodyWriter.FlushAsync();
                }
                else
                {
                    await context.Response.Body.WriteAsync("first\n"u8.ToArray());
                    await context.Response.Body.FlushAsync();
                }
                record.Note("endpoint.waiting");
                await record.Gate.Task.WaitAsync(TimeSpan.FromSeconds(8));
                await context.Response.Body.WriteAsync("last\n"u8.ToArray());
                record.Note("endpoint.return");
            }
        );
        endpoints.MapPost(
            "/request/{mode}",
            async context =>
            {
                var mode = context.Request.RouteValues["mode"]!.ToString()!;
                var record = context.Features.Get<RequestRecord>()!;
                if (mode.StartsWith("reader"))
                {
                    var partial = false;
                    while (true)
                    {
                        var result = await context.Request.BodyReader.ReadAsync(
                            context.RequestAborted
                        );
                        if (!partial && result.Buffer.Length > 1)
                        {
                            context.Request.BodyReader.AdvanceTo(result.Buffer.GetPosition(1));
                            partial = true;
                            continue;
                        }
                        context.Request.BodyReader.AdvanceTo(result.Buffer.End);
                        if (result.IsCompleted)
                            break;
                    }
                }
                else if (mode == "partial")
                    await context.Request.Body.ReadExactlyAsync(new byte[3]);
                else if (mode == "aborted")
                {
                    await context.Request.Body.ReadExactlyAsync(new byte[4]);
                    record.Waiting.TrySetResult();
                    await context.Request.Body.CopyToAsync(Stream.Null, context.RequestAborted);
                }
                else if (mode != "unread")
                    await context.Request.Body.CopyToAsync(Stream.Null);
                await context.Response.WriteAsync("ok");
            }
        );
    }
}

[ApiController]
[Route("controller")]
public sealed class ProbeController : ControllerBase
{
    [HttpGet("{id:int}")]
    public ContentResult Get(int id) => Content($"controller:{id}", "text/plain");
}

internal sealed class EarlyStartupRequest(IServer server, ProbeState state) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(5),
        };
        var url = server.Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var response = await client.GetAsync(url + "/startup", cancellationToken);
        response.EnsureSuccessStatusCode();
        await (await state.Wait("/startup")).Finish();
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class ChunkedContent : HttpContent
{
    private readonly byte[] bytes;

    public ChunkedContent(string value)
    {
        bytes = Encoding.UTF8.GetBytes(value);
        Headers.ContentType = new MediaTypeHeaderValue("text/plain");
    }

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        stream.WriteAsync(bytes).AsTask();
}

internal static class Check
{
    public static int Assertions { get; private set; }

    public static void True(bool condition, string message)
    {
        Assertions++;
        if (!condition)
            throw new InvalidOperationException("ASSERT: " + message);
    }

    public static void Equal<T>(T expected, T actual, string message) =>
        True(
            EqualityComparer<T>.Default.Equals(expected, actual),
            $"{message}: expected={expected}, actual={actual}"
        );

    public static void Before(ProbeState state, string first, string second) =>
        True(state.Order(first) < state.Order(second), first + " precedes " + second);
}
