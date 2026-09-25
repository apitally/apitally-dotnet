using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace TransportCompleteness;

internal static class Program
{
    private static int assertions;
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);
    private static readonly string[] HeaderNames =
    [
        "Content-Type",
        "Content-Length",
        "Content-Encoding",
        "Content-Range",
        "Accept-Ranges",
        "Vary",
        "Content-Disposition",
        "Transfer-Encoding",
    ];
    private static readonly string[] Cases =
    [
        "stream",
        "stream-sync",
        "writer",
        "writer-writeasync",
        "writer-cancel-pending",
        "feature-start-error",
        "feature-complete-short",
        "writer-unflushed",
        "writer-complete",
        "explicit-complete",
        "empty",
        "ineligible",
        "boundary-49999",
        "boundary-50000",
        "boundary-50001",
        "head",
        "204",
        "304",
        "handled-error",
        "replace-token",
        "streaming",
        "gzip-streaming",
        "gzip",
        "gzip-decoded-overcap",
        "file-full",
        "file-slice",
        "file-remainder",
        "file-mixed",
        "file-mixed-side-read-fault",
        "file-mixed-overcap",
        "file-overcap",
        "file-ineligible",
        "minimal-full",
        "minimal-range",
        "minimal-416",
        "minimal-head",
        "mvc-full",
        "mvc-range",
        "stream-file",
        "gzip-file",
        "gzip-range",
        "file-missing",
        "file-offset-invalid",
        "file-count-invalid",
        "file-canceled",
        "file-side-read-fault",
        "file-change",
        "file-delete",
        "file-truncate",
        "abort",
        "abort-oversized",
        "disconnect",
        "short",
        "overwrite-stream",
        "overwrite-writer",
        "swallowed-write",
        "canceled-write",
        "canceled-flush",
        "canceled-writer-flush",
        "writer-error",
        "writer-error-async",
        "writer-error-replaced",
        "request-full",
        "request-eof",
        "request-reader",
        "request-partial",
        "request-unread",
        "upload-disconnect",
    ];

    public static async Task Main(string[] args)
    {
        var freshConnections = args.Contains("--fresh-connections");
        Console.WriteLine($"Connections: {(freshConnections ? "fresh" : "pooled")}");
        Console.WriteLine(
            $"Runtime: {RuntimeInformation.FrameworkDescription}; ASP.NET: {typeof(WebApplication).Assembly.Location}"
        );
        var directory = Path.Combine(
            Path.GetTempPath(),
            "transport-completeness-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        var exchanges = new ConcurrentDictionary<string, Exchange>();
        await using var app = Server.Create(exchanges);
        try
        {
            using var start = new CancellationTokenSource(Limit);
            await app.StartAsync(start.Token);
            var address = app
                .Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!
                .Addresses.Single();
            using var client = new HttpClient(
                new SocketsHttpHandler
                {
                    AutomaticDecompression = DecompressionMethods.None,
                    UseProxy = false,
                    PooledConnectionLifetime = freshConnections
                        ? TimeSpan.Zero
                        : Timeout.InfiniteTimeSpan,
                }
            )
            {
                BaseAddress = new Uri(address),
                Timeout = Limit,
            };
            string? firstConnection = null;
            foreach (var name in Cases)
            {
                ClientResult? control = null;
                string? controlException = null;
                foreach (var mode in Enum.GetValues<FileModeProbe>())
                {
                    var file = Path.Combine(directory, "synthetic.txt");
                    await File.WriteAllBytesAsync(
                        file,
                        Server.Payload(
                            name is "file-overcap" ? 50001
                            : name is "file-mixed-overcap" ? 49990
                            : 120
                        )
                    );
                    var exchange = new Exchange(name, mode, file);
                    var id = Guid.NewGuid().ToString("N");
                    exchanges[id] = exchange;
                    var result = name is "disconnect" or "upload-disconnect"
                        ? await RawClient(new Uri(address), id, exchange)
                        : await Send(client, id, exchange);
                    var completion = await exchange.Completed.Task.WaitAsync(Limit);
                    Check(exchange.CallbackErrors.IsEmpty, $"{name}/{mode}: callbacks succeeded");
                    if (name is "stream" or "stream-sync" or "writer" or "writer-writeasync")
                    {
                        if (firstConnection is null)
                            firstConnection = exchange.ConnectionId;
                        else
                            Check(
                                freshConnections
                                    ? exchange.ConnectionId != firstConnection
                                    : exchange.ConnectionId == firstConnection,
                                $"{name}/{mode}: connection isolation or reuse matches client mode"
                            );
                    }
                    if (mode == FileModeProbe.Control)
                    {
                        control = result;
                        controlException = exchange.ApplicationException;
                        ValidateClient(name, result);
                    }
                    else
                    {
                        // Abort timing can race header receipt; deterministic file/error cases compare exact outcomes.
                        if (name is not "abort" and not "abort-oversized")
                        {
                            Check(
                                result.Status == control!.Status,
                                $"{name}/{mode}: status unchanged"
                            );
                            Check(
                                result.Headers == control.Headers,
                                $"{name}/{mode}: selected headers unchanged ({result.Headers})"
                            );
                            Check(
                                result.Bytes.SequenceEqual(control.Bytes),
                                $"{name}/{mode}: client bytes unchanged"
                            );
                            Check(
                                name is "writer-error" or "writer-error-replaced"
                                    ? result.Error is "IOException" or "HttpIOException"
                                        && control.Error is "IOException" or "HttpIOException"
                                    : result.Error == control.Error,
                                $"{name}/{mode}: client outcome unchanged ({result.Error}/{control.Error})"
                            );
                        }
                        Check(
                            exchange.ApplicationException == controlException,
                            $"{name}/{mode}: application exception unchanged ({exchange.ApplicationException}/{controlException})"
                        );
                        ValidateCapture(exchange, result, completion);
                    }
                    var captureComparison = completion.Body is { } captured
                        ? captured.Span.SequenceEqual(result.Bytes)
                            ? "equal"
                            : "MISMATCH"
                        : "not-buffered";
                    Console.WriteLine(
                        $"CASE {name} {mode}: connection={exchange.ConnectionId} status={result.Status} client={result.Bytes.Length} error={result.Error ?? "none"} app={exchange.ApplicationException ?? "none"} capture={completion.Disposition} captureClient={captureComparison} accepted={completion.AcceptedCount} peak={completion.PeakRetained} sideRead={completion.SideReadBytes} native={completion.NativeFiles} helper={completion.HelperFiles} writes={completion.StreamWrites} advances={completion.WriterAdvances} originalPredicate={completion.OriginalPredicate} originalCanceled={completion.OriginalTokenCanceled} currentCanceled={completion.CurrentTokenCanceled} replaced={completion.TokenReplaced} tokenChangedAtCompletion={completion.TokenChangedAtCompletion} evidence=[{string.Join(',', completion.Failures)}]"
                    );
                    exchanges.TryRemove(id, out _);
                }
            }
        }
        finally
        {
            using var stop = new CancellationTokenSource(Limit);
            await app.StopAsync(stop.Token);
            Directory.Delete(directory, true);
        }
        Console.WriteLine(
            $"PASS {Cases.Length} cases x 3 modes; {assertions} assertions; host stopped, temporary files removed"
        );
    }

    private static async Task<ClientResult> Send(HttpClient client, string id, Exchange exchange)
    {
        var name = exchange.Name;
        var path =
            name.StartsWith("minimal-") ? "/minimal"
            : name.StartsWith("mvc-") ? "/mvc"
            : "/case";
        using var request = new HttpRequestMessage(
            name is "head" or "minimal-head" ? HttpMethod.Head
                : name.StartsWith("request-") ? HttpMethod.Post
                : HttpMethod.Get,
            path + "?id=" + id
        );
        if (name.Contains("range"))
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(5, 21);
        if (name == "minimal-416")
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(500, 600);
        if (name.StartsWith("gzip"))
            request.Headers.AcceptEncoding.ParseAdd("gzip");
        if (name.StartsWith("request-"))
        {
            request.Content = new ByteArrayContent(Server.Payload(37));
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                "text/plain"
            );
            if (name is "request-reader" or "request-eof")
                request.Headers.TransferEncodingChunked = true;
        }
        using var timeout = new CancellationTokenSource(Limit);
        using var bytes = new MemoryStream();
        int? status = null;
        var headers = "";
        string? error = null;
        try
        {
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token
            );
            status = (int)response.StatusCode;
            headers = string.Join(
                ";",
                HeaderNames.Select(header =>
                    response.Headers.TryGetValues(header, out var values)
                    || response.Content.Headers.TryGetValues(header, out values)
                        ? header + "=" + string.Join(',', values)
                        : ""
                )
            );
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            if (name is "streaming" or "gzip-streaming" || name.StartsWith("writer-error"))
            {
                await exchange.PrefixReady.Task.WaitAsync(Limit);
                using var recording = new RecordingReader(stream, bytes);
                using var gzip =
                    name == "gzip-streaming"
                        ? new GZipStream(recording, CompressionMode.Decompress, true)
                        : null;
                var source = (Stream?)gzip ?? recording;
                var prefix = new byte[Server.Prefix.Length];
                await source.ReadExactlyAsync(prefix, timeout.Token);
                Check(
                    prefix.SequenceEqual(Server.Prefix),
                    name + ": client received decoded prefix"
                );
                Check(
                    !exchange.HandlerEnded.Task.IsCompleted,
                    name + ": handler still gated when prefix received"
                );
                exchange.Release.TrySetResult(true);
                await source.CopyToAsync(Stream.Null, timeout.Token);
            }
            else
            {
                var buffer = new byte[4096];
                int read;
                while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0)
                    bytes.Write(buffer, 0, read);
            }
        }
        catch (Exception caught) when (caught is HttpRequestException or IOException)
        {
            error = caught.GetType().Name;
        }
        finally
        {
            exchange.Release.TrySetResult(true);
        }
        return new ClientResult(status, headers, bytes.ToArray(), error);
    }

    private static async Task<ClientResult> RawClient(Uri address, string id, Exchange exchange)
    {
        using var timeout = new CancellationTokenSource(Limit);
        using var client = new TcpClient();
        await client.ConnectAsync(address.Host, address.Port, timeout.Token);
        await using var stream = client.GetStream();
        var upload = exchange.Name == "upload-disconnect";
        var message = upload
            ? $"POST /case?id={id} HTTP/1.1\r\nHost: localhost\r\nContent-Type: text/plain\r\nContent-Length: 100\r\n\r\npartial"
            : $"GET /case?id={id} HTTP/1.1\r\nHost: localhost\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(message), timeout.Token);
        await exchange.PrefixReady.Task.WaitAsync(Limit);
        if (!upload)
        {
            using var received = new MemoryStream();
            var buffer = new byte[1024];
            while (
                !Encoding
                    .ASCII.GetString(received.ToArray())
                    .Contains("prefix|", StringComparison.Ordinal)
            )
            {
                var count = await stream.ReadAsync(buffer, timeout.Token);
                Check(count > 0, "disconnect: prefix reached socket before close");
                received.Write(buffer, 0, count);
            }
        }
        client.Client.LingerState = new LingerOption(true, 0);
        client.Close();
        return new ClientResult(
            null,
            "raw socket closed after observed prefix",
            upload ? [] : Server.Prefix,
            "client-reset"
        );
    }

    private static void ValidateClient(string name, ClientResult result)
    {
        if (name is "abort" or "abort-oversized")
        {
            Check(
                result.Error is not null,
                name + ": native client observes incomplete HTTP transfer"
            );
            return;
        }
        if (name is "disconnect" or "upload-disconnect")
            return;
        if (
            name is "short" or "feature-complete-short" or "writer-error" or "writer-error-replaced"
        )
        {
            Check(result.Error is not null, name + ": native completion failure visible to client");
            Check(
                result.Bytes.SequenceEqual(Server.Prefix),
                name + ": native flushed prefix retained"
            );
            return;
        }
        Check(result.Error is null, name + ": baseline client succeeded");
        var expected = Expected(name);
        var actual =
            name.StartsWith("gzip") && name != "gzip-range"
                ? Decode(result.Bytes, 60000)
                : result.Bytes;
        Check(
            actual.SequenceEqual(expected),
            $"{name}: expected native body ({actual.Length}/{expected.Length})"
        );
        if (name is "minimal-range" or "mvc-range" or "gzip-range")
            Check(result.Status == 206, name + ": partial content");
        if (name == "minimal-416")
            Check(result.Status == 416, name + ": unsatisfiable range");
        if (name == "handled-error")
            Check(result.Status == 500, "handled error stays 500");
        if (name == "gzip-range")
            Check(!result.Headers.Contains("Content-Encoding=gzip"), "range suppresses gzip");
    }

    private static void ValidateCapture(
        Exchange exchange,
        ClientResult client,
        Completion completion
    )
    {
        var name = exchange.Name;
        if (name is "abort" or "abort-oversized")
            Check(client.Error is not null, name + ": client observes incomplete HTTP transfer");
        Check(completion.PeakRetained <= Capture.Limit, name + ": retained response bytes bounded");
        Check(completion.SideReadBytes <= Capture.Limit, name + ": side reads bounded");
        var oversized =
            name is "boundary-50001" or "file-overcap" or "file-mixed-overcap" or "abort-oversized";
        var failure =
            name
                is "abort"
                    or "disconnect"
                    or "short"
                    or "overwrite-stream"
                    or "overwrite-writer"
                    or "swallowed-write"
                    or "canceled-write"
                    or "canceled-flush"
                    or "canceled-writer-flush"
                    or "writer-cancel-pending"
                    or "feature-start-error"
                    or "feature-complete-short"
            || name.StartsWith("writer-error")
            || name
                is "file-missing"
                    or "file-offset-invalid"
                    or "file-count-invalid"
                    or "file-canceled";
        var empty =
            name
            is "empty"
                or "head"
                or "204"
                or "304"
                or "minimal-head"
                or "minimal-416"
                or "ineligible"
                or "file-ineligible"
                or "upload-disconnect";
        var missing =
            exchange.Mode == FileModeProbe.ExperimentalSideRead
            && name
                is "file-side-read-fault"
                    or "file-mixed-side-read-fault"
                    or "file-delete"
                    or "file-truncate";
        if (oversized || failure || empty || missing)
        {
            Check(completion.Body is null, name + ": no buffered body exported");
            Check(
                completion.Disposition == (oversized ? "oversized" : "omitted"),
                name + ": correct omission/sentinel"
            );
        }
        else if (name == "file-change" && exchange.Mode == FileModeProbe.ExperimentalSideRead)
        {
            Check(
                completion.Body is not null
                    && completion.Body.Value.Span.SequenceEqual(
                        Enumerable.Repeat((byte)'X', 120).ToArray()
                    ),
                "side read observes replacement file"
            );
            Check(
                !completion.Body!.Value.Span.SequenceEqual(client.Bytes),
                "LIMITATION: side-read body differs from bytes delivered by native file copy"
            );
            Check(
                completion.NoObservedIncompleteness,
                "LIMITATION: ordinary failure evidence cannot detect successful side-read mismatch"
            );
        }
        else
        {
            Check(
                completion.Body is not null
                    && completion.Body.Value.Span.SequenceEqual(client.Bytes),
                name + ": captured body equals entire client body"
            );
            Check(
                completion.NoObservedIncompleteness,
                name + ": no observed server-side incompleteness"
            );
            Check(
                completion.AcceptedCount == client.Bytes.Length,
                name + ": accepted count matches fixture"
            );
        }
        if (failure && name != "short")
            Check(
                completion.Failures.Length > 0
                    || completion.OriginalTokenCanceled
                    || completion.CurrentTokenCanceled,
                name + ": failure evidence published at completion"
            );
        if (name == "short")
            Check(
                !completion.NoObservedIncompleteness
                    && completion.AcceptedCount == Server.Prefix.Length,
                "short length is not a complete capture"
            );
        if (name.StartsWith("overwrite-"))
            Check(completion.AcceptedCount == 0, name + ": rejected write not committed");
        if (name.StartsWith("writer-error"))
            Check(
                completion.Failures.Any(value =>
                    value.Contains("complete") && value.Contains("exception")
                ),
                name + ": explicit writer error retained despite native swallowing"
            );
        if (name is "replace-token" or "writer-error-replaced")
            Check(completion.TokenReplaced, name + ": replacement diagnosed");
        if (name is "head" or "204" or "304" or "minimal-head")
            Check(
                completion.Bodyless && completion.ApplicableLength is null,
                name + ": bodyless length ignored"
            );
        if (name is "file-overcap" or "file-ineligible")
            Check(
                completion.SideReadBytes == 0
                    && completion.NativeFiles == 1
                    && completion.HelperFiles == 0,
                name + ": native skip branch has no side reads"
            );
        if (name is "gzip-file")
            Check(
                completion.NativeFiles == 0
                    && completion.HelperFiles == 0
                    && completion.StreamWrites > 0,
                "compression copies through encoded stream, not observer file callback"
            );
        if (name == "stream-file")
            Check(
                completion.NativeFiles == 0
                    && completion.HelperFiles == 0
                    && completion.StreamWrites > 0,
                "stream result follows stream path"
            );
        if (name == "file-full")
        {
            Check(
                exchange.Mode == FileModeProbe.ExperimentalSideRead
                    ? completion.NativeFiles == 1
                        && completion.HelperFiles == 0
                        && completion.SideReadBytes == 120
                    : completion.NativeFiles == 0
                        && completion.HelperFiles == 1
                        && completion.SideReadBytes == 0,
                "explicit experimental file path"
            );
        }
        if (name.StartsWith("gzip") && name != "gzip-range")
        {
            Check(client.Headers.Contains("Content-Encoding=gzip"), name + ": gzip header");
            Check(
                Decode(completion.Body!.Value.ToArray(), 60000).SequenceEqual(Expected(name)),
                name + ": capture includes gzip trailer"
            );
            if (name == "gzip-decoded-overcap")
                Check(
                    !DecodedWithinCap(completion.Body!.Value.ToArray()),
                    "harness-only bounded decoded size exceeds cap"
                );
        }
        if (name.StartsWith("request-"))
        {
            var full = name is "request-full" or "request-eof" or "request-reader";
            Check(
                full
                    ? completion.RequestBody is not null
                        && completion.RequestBody.Value.Span.SequenceEqual(Server.Payload(37))
                    : completion.RequestBody is null,
                name + ": request capture completeness"
            );
            Check(
                completion.RequestCount
                    == (
                        full ? 37
                        : name == "request-partial" ? 7
                        : 0
                    ),
                name + ": observer does not read unconsumed request bytes"
            );
            if (name is "request-eof" or "request-reader")
                Check(completion.RequestEof, name + ": request EOF observed");
        }
        if (name == "writer-cancel-pending")
            Check(
                completion.Failures.Contains("writer.flush-result:True:False"),
                "canceled FlushResult preserved and recorded"
            );
        if (name == "request-full")
            Check(
                !completion.RequestEof,
                "known-length request completes without an added EOF read"
            );
        if (name == "upload-disconnect")
            Check(
                completion.RequestBody is null && completion.RequestCount == 7,
                "disconnected upload omitted after consumed prefix"
            );
    }

    private static byte[] Expected(string name) =>
        name switch
        {
            "empty"
            or "head"
            or "204"
            or "304"
            or "minimal-head"
            or "minimal-416"
            or "file-missing"
            or "file-offset-invalid"
            or "file-count-invalid"
            or "file-canceled"
            or "overwrite-stream"
            or "overwrite-writer"
            or "feature-start-error" => [],
            "stream" or "streaming" or "gzip-streaming" => [.. Server.Prefix, .. Server.Suffix],
            "boundary-49999" => Server.Payload(49999),
            "boundary-50000" => Server.Payload(50000),
            "boundary-50001" or "gzip-decoded-overcap" or "file-overcap" => Server.Payload(50001),
            "gzip" => Server.Payload(2000),
            "file-slice" or "minimal-range" or "mvc-range" or "gzip-range" => Server.Payload(120)[
                5..22
            ],
            "file-remainder" => Server.Payload(120)[5..],
            "file-mixed" or "file-mixed-side-read-fault" =>
            [
                .. Server.Prefix,
                .. Server.Payload(120),
                .. Server.Suffix,
            ],
            "file-mixed-overcap" => [.. Server.Prefix, .. Server.Payload(49990), .. Server.Suffix],
            "file-full"
            or "file-ineligible"
            or "file-side-read-fault"
            or "minimal-full"
            or "mvc-full"
            or "stream-file"
            or "gzip-file"
            or "file-change"
            or "file-delete"
            or "file-truncate" => Server.Payload(120),
            _ => Server.Prefix,
        };

    private static byte[] Decode(byte[] input, int limit)
    {
        using var gzip = new GZipStream(new MemoryStream(input), CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[1024];
        int count;
        while ((count = gzip.Read(buffer)) > 0)
        {
            if (output.Length + count > limit)
                throw new InvalidDataException("Decoded harness cap exceeded");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static bool DecodedWithinCap(byte[] input)
    {
        try
        {
            Decode(input, Capture.Limit);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("ASSERTION: " + message);
        assertions++;
    }

    private sealed record ClientResult(int? Status, string Headers, byte[] Bytes, string? Error);
}

internal sealed class RecordingReader(Stream inner, MemoryStream recorded) : Stream
{
    public override bool CanRead => true;
    public override bool CanWrite => false;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        recorded.Write(buffer, offset, read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        var read = await inner.ReadAsync(buffer, cancellationToken);
        recorded.Write(buffer.Span[..read]);
        return read;
    }
}
