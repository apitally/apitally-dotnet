using System.IO.Pipelines;
using Microsoft.AspNetCore.Http.Features;

namespace Apitally.AspNetCore;

// Observes the response through both the stream and the pipe writer, and delegates native
// file sends unchanged.
internal sealed class ObservedResponseBodyFeature : IHttpResponseBodyFeature
{
    private readonly IHttpResponseBodyFeature inner;
    private readonly BodyCapture capture;

    public ObservedResponseBodyFeature(IHttpResponseBodyFeature inner, BodyCapture capture)
    {
        this.inner = inner;
        this.capture = capture;
        Stream = new ObservedStream(inner.Stream, capture, isRequest: false);
        Writer = new ObservedPipeWriter(inner.Writer, capture);
    }

    public Stream Stream { get; }
    public PipeWriter Writer { get; }

    public void DisableBuffering() => inner.DisableBuffering();

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await inner.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            capture.MarkIncomplete();
            throw;
        }
    }

    public async Task CompleteAsync()
    {
        try
        {
            await inner.CompleteAsync().ConfigureAwait(false);
        }
        catch
        {
            capture.MarkIncomplete();
            throw;
        }
    }

    // The whole response capture is omitted, including any stream output mixed with the file.
    public async Task SendFileAsync(
        string path,
        long offset,
        long? count,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            await inner.SendFileAsync(path, offset, count, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            capture.MarkIncomplete();
            throw;
        }
        capture.Bypass(count);
    }
}

// Copies written pipe memory into the capture at Advance and counts it once accepted.
internal sealed class ObservedPipeWriter(PipeWriter inner, BodyCapture capture) : PipeWriter
{
    private Memory<byte> memory;

    public override bool CanGetUnflushedBytes => inner.CanGetUnflushedBytes;
    public override long UnflushedBytes => inner.UnflushedBytes;

    public override Memory<byte> GetMemory(int sizeHint = 0) => memory = inner.GetMemory(sizeHint);

    public override Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

    public override void Advance(int bytes)
    {
        var staged = bytes <= memory.Length ? capture.Stage(memory.Span[..bytes]) : 0;
        memory = default;
        try
        {
            inner.Advance(bytes);
        }
        catch
        {
            capture.MarkIncomplete();
            throw;
        }
        capture.Commit(bytes, staged);
    }

    public override async ValueTask<FlushResult> FlushAsync(
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var result = await inner.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (result.IsCanceled || result.IsCompleted)
                capture.MarkIncomplete();
            return result;
        }
        catch
        {
            capture.MarkIncomplete();
            throw;
        }
    }

    public override async ValueTask<FlushResult> WriteAsync(
        ReadOnlyMemory<byte> source,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var result = await inner.WriteAsync(source, cancellationToken).ConfigureAwait(false);
            if (result.IsCanceled || result.IsCompleted)
                capture.MarkIncomplete();
            else
                capture.Observe(source.Span);
            return result;
        }
        catch
        {
            capture.MarkIncomplete();
            throw;
        }
    }

    public override void CancelPendingFlush() => inner.CancelPendingFlush();

    public override void Complete(Exception? exception = null)
    {
        if (exception is not null)
            capture.MarkIncomplete();
        inner.Complete(exception);
    }

    public override async ValueTask CompleteAsync(Exception? exception = null)
    {
        if (exception is not null)
            capture.MarkIncomplete();
        await inner.CompleteAsync(exception).ConfigureAwait(false);
    }
}

// Records explicit aborts, which make both captures incomplete.
internal sealed class ObservedRequestLifetimeFeature(
    IHttpRequestLifetimeFeature inner,
    BodyCapture requestCapture,
    BodyCapture responseCapture
) : IHttpRequestLifetimeFeature
{
    public CancellationToken RequestAborted
    {
        get => inner.RequestAborted;
        set => inner.RequestAborted = value;
    }

    public void Abort()
    {
        requestCapture.MarkIncomplete();
        responseCapture.MarkIncomplete();
        inner.Abort();
    }
}
