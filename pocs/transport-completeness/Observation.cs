using System.Buffers;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http.Features;

namespace TransportCompleteness;

internal enum FileModeProbe
{
    Control,
    ExperimentalSideRead,
    ExperimentalSinglePass,
}

internal sealed record Completion(
    ReadOnlyMemory<byte>? Body,
    ReadOnlyMemory<byte>? RequestBody,
    string Disposition,
    long AcceptedCount,
    long RequestCount,
    bool NoObservedIncompleteness,
    bool OriginalPredicate,
    bool OriginalTokenCanceled,
    bool CurrentTokenCanceled,
    bool TokenReplaced,
    bool TokenChangedAtCompletion,
    string[] Failures,
    int PeakRetained,
    long SideReadBytes,
    int NativeFiles,
    int HelperFiles,
    int StreamWrites,
    int WriterAdvances,
    bool RequestEof,
    bool Bodyless,
    long? ApplicableLength
);

internal sealed class Exchange(string name, FileModeProbe mode, string file)
{
    public string Name { get; } = name;
    public string ConnectionId { get; set; } = "not-reached";
    public FileModeProbe Mode { get; } = mode;
    public string File { get; } = file;
    public TaskCompletionSource<Completion> Completed { get; } = NewSource<Completion>();
    public TaskCompletionSource<bool> PrefixReady { get; } = NewSource<bool>();
    public TaskCompletionSource<bool> Release { get; } = NewSource<bool>();
    public TaskCompletionSource<bool> HandlerEnded { get; } = NewSource<bool>();
    public ConcurrentQueue<Exception> CallbackErrors { get; } = new();
    public ConcurrentQueue<string> Failures { get; } = new();
    public string? ApplicationException { get; set; }
    public bool AbortCalled { get; set; }
    public bool Escaped { get; set; }
    public bool OriginalCanceled;
    public bool TokenReplaced { get; set; }
    public long SideReadBytes { get; set; }
    public int NativeFiles { get; set; }
    public int HelperFiles { get; set; }
    public int StreamWrites { get; set; }
    public int WriterAdvances { get; set; }
    public Capture? Response { get; set; }
    public Capture? Request { get; set; }

    public static TaskCompletionSource<T> NewSource<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal static class Observer
{
    public static async Task Invoke(HttpContext context, Exchange exchange, RequestDelegate next)
    {
        var originalToken = context.RequestAborted;
        var lifetime = context.Features.Get<IHttpRequestLifetimeFeature>()!;
        var response = context.Features.Get<IHttpResponseBodyFeature>()!;
        var requestStream = context.Request.Body;
        var requestPipeFeature = context.Features.Get<IRequestBodyPipeFeature>();
        var requestReader = context.Request.BodyReader;
        var capture = new Capture(
            () => context.Response.ContentType,
            () => context.Response.ContentLength
        );
        var requestCapture = new Capture(
            () => context.Request.ContentType,
            () => context.Request.ContentLength
        );
        exchange.Response = capture;
        exchange.Request = requestCapture;
        var registration = originalToken.Register(() => exchange.OriginalCanceled = true);
        context.Features.Set<IHttpRequestLifetimeFeature>(new ObservedLifetime(lifetime, exchange));
        context.Request.Body = new ObservedStream(requestStream, requestCapture, exchange, true);
        context.Features.Set<IRequestBodyPipeFeature>(
            new ObservedRequestPipe(requestReader, requestCapture)
        );
        context.Features.Set<IHttpResponseBodyFeature>(
            new ObservedResponse(response, capture, exchange)
        );
        context.Response.OnCompleted(() =>
        {
            try
            {
                var originalCanceled =
                    exchange.OriginalCanceled || originalToken.IsCancellationRequested;
                var currentCanceled = context.RequestAborted.IsCancellationRequested;
                var bodyless =
                    HttpMethods.IsHead(context.Request.Method)
                    || context.Response.StatusCode is 204 or 304;
                var length =
                    bodyless || context.Response.Headers.ContainsKey("Transfer-Encoding")
                        ? null
                        : context.Response.ContentLength;
                var originalPredicate =
                    !exchange.AbortCalled
                    && !originalCanceled
                    && !currentCanceled
                    && !exchange.Escaped
                    && (length is null || length == capture.Count);
                var failures = exchange.Failures.ToArray();
                var complete = originalPredicate && failures.Length == 0;
                var body = capture.Export(complete && !bodyless);
                var requestComplete =
                    requestCapture.Eof || context.Request.ContentLength == requestCapture.Count;
                exchange.Completed.TrySetResult(
                    new Completion(
                        body,
                        requestCapture.Export(
                            requestComplete
                                && !exchange.AbortCalled
                                && !originalCanceled
                                && !currentCanceled
                        ),
                        bodyless ? "omitted"
                            : capture.TooLarge ? "oversized"
                            : body is not null ? "buffered"
                            : "omitted",
                        capture.Count,
                        requestCapture.Count,
                        complete,
                        originalPredicate,
                        originalCanceled,
                        currentCanceled,
                        exchange.TokenReplaced,
                        context.RequestAborted != originalToken,
                        failures,
                        capture.PeakRetained,
                        exchange.SideReadBytes,
                        exchange.NativeFiles,
                        exchange.HelperFiles,
                        exchange.StreamWrites,
                        exchange.WriterAdvances,
                        requestCapture.Eof,
                        bodyless,
                        length
                    )
                );
            }
            catch (Exception error)
            {
                exchange.CallbackErrors.Enqueue(error);
                exchange.Completed.TrySetException(error);
            }
            finally
            {
                registration.Dispose();
            }
            return Task.CompletedTask;
        });
        try
        {
            await next(context);
        }
        catch
        {
            exchange.Escaped = true;
            throw;
        }
        finally
        {
            context.Features.Set(response);
            context.Request.Body = requestStream;
            context.Features.Set(requestPipeFeature);
            context.Features.Set(lifetime);
        }
    }
}

internal sealed class Capture(Func<string?> contentType, Func<long?> length)
{
    public const int Limit = 50_000;
    private byte[]? buffer;
    private int used;
    public long Count { get; private set; }
    public bool TooLarge { get; private set; }
    public bool Missing { get; private set; }
    public bool Eof { get; set; }
    public int PeakRetained { get; private set; }
    public bool Eligible =>
        contentType()?.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase) == true
        || contentType()?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)
            == true;
    public int Remaining => Limit - used;
    public bool KnownOvercap => length() > Limit;

    public ReadOnlyMemory<byte>? Export(bool complete)
    {
        if (!complete || TooLarge || Missing || used == 0 || used != Count)
            return null;
        var owned = buffer!.AsMemory(0, used);
        buffer = null;
        used = 0;
        return owned;
    }

    // Stage into the one bounded buffer while pipe memory is still leased. Commit only after acceptance.
    public int Stage(ReadOnlySpan<byte> bytes)
    {
        try
        {
            if (!Eligible || Missing || TooLarge || bytes.Length > Remaining || length() > Limit)
                return 0;
            buffer ??= new byte[Limit];
            bytes.CopyTo(buffer.AsSpan(used));
            PeakRetained = Math.Max(PeakRetained, used + bytes.Length);
            return bytes.Length;
        }
        catch
        {
            Unavailable();
            return 0;
        }
    }

    public int Stage(ReadOnlySequence<byte> bytes)
    {
        try
        {
            if (!Eligible || Missing || TooLarge || bytes.Length > Remaining || KnownOvercap)
                return 0;
            bytes.CopyTo(SideReadMemory((int)bytes.Length).Span);
            return (int)bytes.Length;
        }
        catch
        {
            Unavailable();
            return 0;
        }
    }

    public void Commit(long count, int staged)
    {
        Count += count;
        try
        {
            if (Eligible && (Count > Limit || length() > Limit))
            {
                TooLarge = true;
                buffer = null;
                used = 0;
            }
            if (!TooLarge && !Missing)
                used += staged;
        }
        catch
        {
            Unavailable();
        }
    }

    public void Observe(ReadOnlySpan<byte> bytes) => Commit(bytes.Length, Stage(bytes));

    public void Unavailable()
    {
        Missing = true;
        buffer = null;
        used = 0;
    }

    public Memory<byte> SideReadMemory(int count)
    {
        buffer ??= new byte[Limit];
        PeakRetained = Math.Max(PeakRetained, used + count);
        return buffer.AsMemory(used, count);
    }
}

internal sealed class ObservedLifetime(IHttpRequestLifetimeFeature inner, Exchange exchange)
    : IHttpRequestLifetimeFeature
{
    public CancellationToken RequestAborted
    {
        get => inner.RequestAborted;
        set
        {
            exchange.TokenReplaced |= value != inner.RequestAborted;
            inner.RequestAborted = value;
        }
    }

    public void Abort()
    {
        exchange.AbortCalled = true;
        exchange.Failures.Enqueue("lifetime.abort");
        inner.Abort();
    }
}

internal sealed class ObservedResponse : IHttpResponseBodyFeature
{
    private readonly IHttpResponseBodyFeature inner;
    private readonly Capture capture;
    private readonly Exchange exchange;

    public ObservedResponse(IHttpResponseBodyFeature inner, Capture capture, Exchange exchange)
    {
        this.inner = inner;
        this.capture = capture;
        this.exchange = exchange;
        Stream = new ObservedStream(inner.Stream, capture, exchange, false);
        Writer = new ObservedWriter(inner.Writer, capture, exchange);
    }

    public Stream Stream { get; }
    public System.IO.Pipelines.PipeWriter Writer { get; }

    public void DisableBuffering() => inner.DisableBuffering();

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await inner.StartAsync(cancellationToken);
        }
        catch
        {
            exchange.Failures.Enqueue("feature.start");
            throw;
        }
    }

    public async Task CompleteAsync()
    {
        try
        {
            await inner.CompleteAsync();
        }
        catch
        {
            exchange.Failures.Enqueue("feature.complete");
            throw;
        }
    }

    public async Task SendFileAsync(
        string path,
        long offset,
        long? count,
        CancellationToken cancellationToken = default
    )
    {
        long? size = count;
        try
        {
            size ??= new FileInfo(path).Length - offset;
        }
        catch
        { /* Observation metadata must not change native validation or exception ordering. */
        }
        var eligible = false;
        try
        {
            eligible =
                capture.Eligible
                && !capture.KnownOvercap
                && !capture.Missing
                && !capture.TooLarge
                && size >= 0
                && size <= capture.Remaining;
        }
        catch
        {
            capture.Unavailable();
        }
        if (exchange.Mode == FileModeProbe.ExperimentalSinglePass && eligible)
        {
            exchange.HelperFiles++;
            try
            {
                // Public infrastructure API; intentionally bypasses inner.SendFileAsync for this comparison only.
                await SendFileFallback.SendFileAsync(
                    Stream,
                    path,
                    offset,
                    count,
                    cancellationToken
                );
            }
            catch
            {
                exchange.Failures.Enqueue("file.helper");
                throw;
            }
            return;
        }
        exchange.NativeFiles++;
        try
        {
            await inner.SendFileAsync(path, offset, count, cancellationToken);
        }
        catch
        {
            exchange.Failures.Enqueue("file.native");
            throw;
        }
        var staged = 0;
        if (exchange.Mode == FileModeProbe.ExperimentalSideRead && eligible)
        {
            try
            {
                if (exchange.Name is "file-side-read-fault" or "file-mixed-side-read-fault")
                    throw new IOException("Injected observation-only side-read failure");
                var destination = capture.SideReadMemory((int)size!.Value);
                await using var file = new FileStream(
                    path,
                    System.IO.FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    1,
                    FileOptions.Asynchronous
                );
                file.Seek(offset, SeekOrigin.Begin);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (staged < destination.Length)
                {
                    var read = await file.ReadAsync(destination[staged..], timeout.Token);
                    exchange.SideReadBytes += read;
                    if (read == 0)
                        throw new EndOfStreamException("Side read was partial");
                    staged += read;
                }
            }
            catch
            {
                capture.Unavailable();
                staged = 0;
            }
        }
        else if (eligible)
            capture.Unavailable();
        if (size is long accepted && accepted >= 0)
            capture.Commit(accepted, staged);
        else
            capture.Unavailable();
    }
}
