namespace Apitally.AspNetCore;

// Transparently observes bytes the application reads from the request or writes to the
// response. Streaming, backpressure, cancellation and exceptions pass through unchanged.
internal sealed class ObservedStream(Stream inner, BodyCapture capture, bool isRequest) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override bool CanTimeout => inner.CanTimeout;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override int ReadTimeout
    {
        get => inner.ReadTimeout;
        set => inner.ReadTimeout = value;
    }

    public override int WriteTimeout
    {
        get => inner.WriteTimeout;
        set => inner.WriteTimeout = value;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int count;
        try
        {
            count = inner.Read(buffer);
        }
        catch
        {
            capture.MarkIncomplete();
            throw;
        }
        ObserveRead(buffer[..count], buffer.Length);
        return count;
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    ) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        int count;
        try
        {
            count = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            capture.MarkIncomplete();
            throw;
        }
        ObserveRead(buffer.Span[..count], buffer.Length);
        return count;
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        try
        {
            inner.Write(buffer);
        }
        catch
        {
            capture.MarkIncomplete();
            throw;
        }
        if (!isRequest)
            capture.Observe(buffer);
    }

    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    ) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            capture.MarkIncomplete();
            throw;
        }
        if (!isRequest)
            capture.Observe(buffer.Span);
    }

    public override void Flush()
    {
        try
        {
            inner.Flush();
        }
        catch
        {
            if (!isRequest)
                capture.MarkIncomplete();
            throw;
        }
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        try
        {
            await inner.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (!isRequest)
                capture.MarkIncomplete();
            throw;
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void SetLength(long value) => inner.SetLength(value);

    // The inner stream belongs to the server or application.
    protected override void Dispose(bool disposing) { }

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private void ObserveRead(ReadOnlySpan<byte> bytes, int requested)
    {
        if (!isRequest)
            return;
        capture.Observe(bytes);
        capture.IsEndOfStream |= bytes.Length == 0 && requested > 0;
    }
}
