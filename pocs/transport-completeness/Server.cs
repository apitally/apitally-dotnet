using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.ResponseCompression;

namespace TransportCompleteness;

internal static class Server
{
    public static WebApplication Create(ConcurrentDictionary<string, Exchange> exchanges)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(
                IPAddress.Loopback,
                0,
                endpoint =>
                    endpoint.Protocols = Microsoft
                        .AspNetCore
                        .Server
                        .Kestrel
                        .Core
                        .HttpProtocols
                        .Http1
            );
            options.AllowSynchronousIO = true;
        });
        builder.Services.AddControllers();
        builder.Services.AddResponseCompression(options =>
        {
            options.Providers.Add<GzipCompressionProvider>();
            options.MimeTypes = ["text/plain", "application/json"];
        });
        builder.Services.Configure<GzipCompressionProviderOptions>(options =>
            options.Level = CompressionLevel.Fastest
        );
        var app = builder.Build();
        app.Use(
            async (context, next) =>
            {
                var exchange = exchanges[context.Request.Query["id"].ToString()];
                context.Items[typeof(Exchange)] = exchange;
                exchange.ConnectionId = context.Connection.Id;
                if (exchange.Mode != FileModeProbe.Control)
                {
                    await Observer.Invoke(context, exchange, next);
                    return;
                }
                context.Response.OnCompleted(() =>
                {
                    exchange.Completed.TrySetResult(
                        new Completion(
                            null,
                            null,
                            "control",
                            0,
                            0,
                            false,
                            false,
                            false,
                            false,
                            false,
                            false,
                            [],
                            0,
                            0,
                            0,
                            0,
                            0,
                            0,
                            false,
                            false,
                            null
                        )
                    );
                    return Task.CompletedTask;
                });
                await next(context);
            }
        );
        app.UseWhen(
            context => Get(context).Name.StartsWith("gzip"),
            branch => branch.UseResponseCompression()
        );
        app.MapControllers();
        app.MapMethods(
            "/minimal",
            ["GET", "HEAD"],
            (HttpContext context) =>
                Results.File(
                    Get(context).File,
                    "text/plain",
                    "synthetic.txt",
                    enableRangeProcessing: true
                )
        );
        app.MapMethods("/case", ["GET", "HEAD", "POST"], Handle);
        return app;
    }

    public static Exchange Get(HttpContext context) => (Exchange)context.Items[typeof(Exchange)]!;

    public static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

    public static byte[] Payload(int count) =>
        Enumerable.Range(0, count).Select(index => (byte)('a' + index % 26)).ToArray();

    public static readonly byte[] Prefix = Bytes("prefix|");
    public static readonly byte[] Suffix = Bytes("|suffix");

    private static async Task Handle(HttpContext context)
    {
        var exchange = Get(context);
        var response = context.Response;
        response.ContentType =
            exchange.Name == "ineligible" || exchange.Name == "file-ineligible"
                ? "application/octet-stream"
                : "text/plain";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var token = timeout.Token;
        var feature = context.Features.Get<IHttpResponseBodyFeature>()!;
        try
        {
            switch (exchange.Name)
            {
                case "stream":
                    await response.Body.WriteAsync(Prefix, token);
                    await response.Body.WriteAsync(Suffix, token);
                    break;
                case "stream-sync":
                    response.Body.Write(Prefix);
                    response.Body.Flush();
                    break;
                case "writer-writeasync":
                    await response.BodyWriter.WriteAsync(Prefix, token);
                    break;
                case "writer-cancel-pending":
                    await response.BodyWriter.WriteAsync(Prefix, token);
                    response.BodyWriter.CancelPendingFlush();
                    var canceledFlush = await response.BodyWriter.FlushAsync(token);
                    exchange.ApplicationException =
                        $"flush-result:{canceledFlush.IsCanceled}:{canceledFlush.IsCompleted}";
                    break;
                case "feature-start-error":
                    response.OnStarting(() =>
                        throw new IOException("Synthetic OnStarting failure")
                    );
                    await response.StartAsync(token);
                    break;
                case "feature-complete-short":
                    response.ContentLength = Prefix.Length + 10;
                    await response.Body.WriteAsync(Prefix, token);
                    await response.CompleteAsync();
                    break;
                case "writer":
                case "writer-unflushed":
                case "writer-complete":
                    Prefix.CopyTo(response.BodyWriter.GetMemory(Prefix.Length));
                    response.BodyWriter.Advance(Prefix.Length);
                    if (exchange.Name == "writer")
                        await response.BodyWriter.FlushAsync(token);
                    if (exchange.Name == "writer-complete")
                        await response.BodyWriter.CompleteAsync();
                    break;
                case "explicit-complete":
                    await response.StartAsync(token);
                    await response.Body.WriteAsync(Prefix, token);
                    await response.CompleteAsync();
                    break;
                case "empty":
                    break;
                case "head":
                    response.ContentLength = Prefix.Length;
                    await response.Body.WriteAsync(Prefix, token);
                    break;
                case "204":
                    response.StatusCode = 204;
                    break;
                case "304":
                    response.StatusCode = 304;
                    response.ContentLength = 123;
                    break;
                case "handled-error":
                    try
                    {
                        throw new InvalidOperationException("Synthetic handled error");
                    }
                    catch (InvalidOperationException)
                    {
                        response.StatusCode = 500;
                        await response.Body.WriteAsync(Prefix, token);
                    }
                    break;
                case "replace-token":
                    context.RequestAborted = CancellationToken.None;
                    await response.Body.WriteAsync(Prefix, token);
                    break;
                case "ineligible":
                    await response.Body.WriteAsync(Prefix, token);
                    break;
                case "boundary-49999":
                case "boundary-50000":
                case "boundary-50001":
                    await response.Body.WriteAsync(
                        Payload(int.Parse(exchange.Name.Split('-')[1])),
                        token
                    );
                    break;
                case "streaming":
                case "gzip-streaming":
                    await response.Body.WriteAsync(Prefix, token);
                    await response.Body.FlushAsync(token);
                    exchange.PrefixReady.TrySetResult(true);
                    await exchange.Release.Task.WaitAsync(token);
                    await response.Body.WriteAsync(Suffix, token);
                    break;
                case "gzip":
                    await response.Body.WriteAsync(Payload(2000), token);
                    break;
                case "gzip-decoded-overcap":
                    await response.Body.WriteAsync(Payload(50001), token);
                    break;
                case "file-full":
                case "file-ineligible":
                case "file-overcap":
                case "file-side-read-fault":
                    await feature.SendFileAsync(exchange.File, 0, null, token);
                    break;
                case "file-slice":
                    await feature.SendFileAsync(exchange.File, 5, 17, token);
                    break;
                case "file-remainder":
                    await feature.SendFileAsync(exchange.File, 5, null, token);
                    break;
                case "file-mixed":
                case "file-mixed-side-read-fault":
                case "file-mixed-overcap":
                    await response.Body.WriteAsync(Prefix, token);
                    await feature.SendFileAsync(exchange.File, 0, null, token);
                    await response.Body.WriteAsync(Suffix, token);
                    break;
                case "file-change":
                case "file-delete":
                case "file-truncate":
                    response.OnStarting(() =>
                    {
                        try
                        {
                            if (exchange.Name == "file-delete")
                                File.Delete(exchange.File);
                            else
                                File.WriteAllBytes(
                                    exchange.File,
                                    exchange.Name == "file-change"
                                        ? Enumerable.Repeat((byte)'X', 120).ToArray()
                                        : Bytes("short")
                                );
                        }
                        catch (Exception error)
                        {
                            exchange.CallbackErrors.Enqueue(error);
                            throw;
                        }
                        return Task.CompletedTask;
                    });
                    await feature.SendFileAsync(exchange.File, 0, 120, token);
                    break;
                case "file-missing":
                    await feature.SendFileAsync(exchange.File + ".missing", 0, null, token);
                    break;
                case "file-offset-invalid":
                    await feature.SendFileAsync(exchange.File, -1, null, token);
                    break;
                case "file-count-invalid":
                    await feature.SendFileAsync(exchange.File, 0, 1000, token);
                    break;
                case "file-canceled":
                    await feature.SendFileAsync(
                        exchange.File,
                        0,
                        null,
                        new CancellationToken(true)
                    );
                    break;
                case "stream-file":
                    await Results
                        .File(
                            new MemoryStream(Payload(120)),
                            "text/plain",
                            "synthetic.txt",
                            enableRangeProcessing: true
                        )
                        .ExecuteAsync(context);
                    break;
                case "gzip-file":
                case "gzip-range":
                    await Results
                        .File(
                            exchange.File,
                            "text/plain",
                            "synthetic.txt",
                            enableRangeProcessing: true
                        )
                        .ExecuteAsync(context);
                    break;
                case "abort":
                case "abort-oversized":
                    await response.Body.WriteAsync(
                        exchange.Name == "abort" ? Prefix : Payload(50001),
                        token
                    );
                    await response.Body.FlushAsync(token);
                    context.Abort();
                    break;
                case "disconnect":
                    await response.Body.WriteAsync(Prefix, token);
                    await response.Body.FlushAsync(token);
                    exchange.PrefixReady.TrySetResult(true);
                    await context.RequestAborted.WaitHandleAsync(token);
                    break;
                case "short":
                    response.ContentLength = Prefix.Length + 10;
                    await response.Body.WriteAsync(Prefix, token);
                    break;
                case "overwrite-stream":
                case "overwrite-writer":
                    response.ContentLength = Prefix.Length;
                    if (exchange.Name == "overwrite-stream")
                        await response.Body.WriteAsync(Payload(Prefix.Length + 1), token);
                    else
                    {
                        Payload(Prefix.Length + 1)
                            .CopyTo(response.BodyWriter.GetMemory(Prefix.Length + 1));
                        response.BodyWriter.Advance(Prefix.Length + 1);
                    }
                    break;
                case "swallowed-write":
                    response.ContentLength = Prefix.Length;
                    await response.Body.WriteAsync(Prefix, token);
                    try
                    {
                        await response.Body.WriteAsync(Suffix, token);
                    }
                    catch (InvalidOperationException error)
                    {
                        exchange.ApplicationException = error.GetType().Name;
                    }
                    break;
                case "canceled-write":
                case "canceled-flush":
                case "canceled-writer-flush":
                    await response.Body.WriteAsync(Prefix, token);
                    await response.Body.FlushAsync(token);
                    try
                    {
                        if (exchange.Name == "canceled-write")
                            await response.Body.WriteAsync(Suffix, new CancellationToken(true));
                        else if (exchange.Name == "canceled-flush")
                            await response.Body.FlushAsync(new CancellationToken(true));
                        else
                            await response.BodyWriter.FlushAsync(new CancellationToken(true));
                    }
                    catch (OperationCanceledException error)
                    {
                        exchange.ApplicationException = error.GetType().Name;
                    }
                    break;
                case "writer-error":
                case "writer-error-async":
                case "writer-error-replaced":
                    if (exchange.Name == "writer-error-replaced")
                        context.RequestAborted = CancellationToken.None;
                    await response.Body.WriteAsync(Prefix, token);
                    await response.Body.FlushAsync(token);
                    exchange.PrefixReady.TrySetResult(true);
                    await exchange.Release.Task.WaitAsync(token);
                    var errorCompletion = new IOException(
                        "Synthetic application writer failure after prefix"
                    );
                    if (exchange.Name == "writer-error-async")
                        await response.BodyWriter.CompleteAsync(errorCompletion);
                    else
                        response.BodyWriter.Complete(errorCompletion);
                    break;
                case "request-full":
                case "request-eof":
                case "request-partial":
                case "request-unread":
                case "upload-disconnect":
                    var buffer = new byte[7];
                    if (exchange.Name == "request-full")
                        await context.Request.Body.ReadExactlyAsync(new byte[37], token);
                    else if (exchange.Name == "request-partial")
                        await context.Request.Body.ReadExactlyAsync(buffer, token);
                    else if (exchange.Name != "request-unread")
                    {
                        var read = 0;
                        do
                        {
                            read = await context.Request.Body.ReadAsync(buffer, token);
                            if (exchange.Name == "upload-disconnect")
                                exchange.PrefixReady.TrySetResult(true);
                        } while (read != 0);
                    }
                    await response.Body.WriteAsync(Prefix, token);
                    break;
                case "request-reader":
                    while (true)
                    {
                        var read = await context.Request.BodyReader.ReadAsync(token);
                        context.Request.BodyReader.AdvanceTo(read.Buffer.End);
                        if (read.IsCompleted)
                            break;
                    }
                    await response.Body.WriteAsync(Prefix, token);
                    break;
                default:
                    throw new InvalidOperationException(exchange.Name);
            }
        }
        catch (Exception error)
        {
            exchange.ApplicationException = error.GetType().Name;
            if (
                exchange.Name.StartsWith("file-")
                || exchange.Name.StartsWith("overwrite-")
                || exchange.Name.StartsWith("feature-")
            )
            {
                if (!response.HasStarted)
                {
                    response.StatusCode = 500;
                    response.ContentLength = 0;
                }
            }
            else if (exchange.Name != "upload-disconnect")
                throw;
        }
        finally
        {
            exchange.HandlerEnded.TrySetResult(true);
        }
    }

    private static async Task WaitHandleAsync(
        this CancellationToken token,
        CancellationToken timeout
    )
    {
        var source = Exchange.NewSource<bool>();
        using var registration = token.Register(() => source.TrySetResult(true));
        await source.Task.WaitAsync(timeout);
    }
}

[ApiController]
public sealed class FileProbeController : ControllerBase
{
    [HttpGet("/mvc")]
    [HttpHead("/mvc")]
    public IActionResult Get() =>
        PhysicalFile(
            Server.Get(HttpContext).File,
            "text/plain",
            "synthetic.txt",
            enableRangeProcessing: true
        );
}
