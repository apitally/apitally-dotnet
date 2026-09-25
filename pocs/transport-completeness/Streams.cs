using System.Buffers;
using System.IO.Pipelines;
using Microsoft.AspNetCore.Http.Features;

namespace TransportCompleteness;

internal sealed class ObservedWriter(PipeWriter inner, Capture capture, Exchange exchange)
    : PipeWriter
{
    private Memory<byte> memory;
    public override bool CanGetUnflushedBytes => inner.CanGetUnflushedBytes;
    public override long UnflushedBytes => inner.UnflushedBytes;

    public override Memory<byte> GetMemory(int sizeHint = 0) => memory = inner.GetMemory(sizeHint);

    public override Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

    public override void Advance(int bytes)
    {
        var staged = 0;
        try
        {
            staged = capture.Stage(memory.Span[..bytes]);
        }
        catch
        {
            capture.Unavailable();
        }
        try
        {
            inner.Advance(bytes);
            capture.Commit(bytes, staged);
            exchange.WriterAdvances++;
        }
        catch
        {
            exchange.Failures.Enqueue("writer.advance");
            throw;
        }
        finally
        {
            memory = default;
        }
    }

    public override async ValueTask<FlushResult> FlushAsync(
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var result = await inner.FlushAsync(cancellationToken);
            if (result.IsCanceled || result.IsCompleted)
                exchange.Failures.Enqueue(
                    $"writer.flush-result:{result.IsCanceled}:{result.IsCompleted}"
                );
            return result;
        }
        catch
        {
            exchange.Failures.Enqueue("writer.flush");
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
            var result = await inner.WriteAsync(source, cancellationToken);
            if (result.IsCanceled || result.IsCompleted)
                exchange.Failures.Enqueue(
                    $"writer.write-result:{result.IsCanceled}:{result.IsCompleted}"
                );
            else
                capture.Observe(source.Span);
            return result;
        }
        catch
        {
            exchange.Failures.Enqueue("writer.write");
            throw;
        }
    }

    public override void CancelPendingFlush() => inner.CancelPendingFlush();

    public override void Complete(Exception? exception = null)
    {
        if (exception is not null)
            exchange.Failures.Enqueue("writer.complete-exception");
        try
        {
            inner.Complete(exception);
        }
        catch
        {
            exchange.Failures.Enqueue("writer.complete");
            throw;
        }
    }

    public override async ValueTask CompleteAsync(Exception? exception = null)
    {
        if (exception is not null)
            exchange.Failures.Enqueue("writer.complete-async-exception");
        try
        {
            await inner.CompleteAsync(exception);
        }
        catch
        {
            exchange.Failures.Enqueue("writer.complete-async");
            throw;
        }
    }
}

internal sealed class ObservedStream(Stream inner, Capture capture, Exchange exchange, bool reading)
    : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override bool CanTimeout => inner.CanTimeout;
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
    public override long Length => inner.Length;
    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void SetLength(long value) => inner.SetLength(value);

    public override void Flush()
    {
        try
        {
            inner.Flush();
        }
        catch
        {
            if (!reading)
                exchange.Failures.Enqueue("stream.flush");
            throw;
        }
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        try
        {
            await inner.FlushAsync(cancellationToken);
        }
        catch
        {
            if (!reading)
                exchange.Failures.Enqueue("stream.flush");
            throw;
        }
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        try
        {
            var count = inner.Read(buffer);
            if (reading)
            {
                capture.Observe(buffer[..count]);
                capture.Eof |= count == 0 && buffer.Length > 0;
            }
            return count;
        }
        catch
        {
            if (reading)
                capture.Unavailable();
            throw;
        }
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
        try
        {
            var count = await inner.ReadAsync(buffer, cancellationToken);
            if (reading)
            {
                capture.Observe(buffer.Span[..count]);
                capture.Eof |= count == 0 && buffer.Length > 0;
            }
            return count;
        }
        catch
        {
            if (reading)
                capture.Unavailable();
            throw;
        }
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
            exchange.Failures.Enqueue("stream.write");
            throw;
        }
        if (!reading)
        {
            capture.Observe(buffer);
            exchange.StreamWrites++;
        }
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
            await inner.WriteAsync(buffer, cancellationToken);
        }
        catch
        {
            exchange.Failures.Enqueue("stream.write");
            throw;
        }
        if (!reading)
        {
            capture.Observe(buffer.Span);
            exchange.StreamWrites++;
        }
    }

    // The underlying stream belongs to the server or application, not this observer.
    protected override void Dispose(bool disposing) { }

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class ObservedRequestPipe(PipeReader inner, Capture capture)
    : IRequestBodyPipeFeature
{
    public PipeReader Reader { get; } = new ObservedReader(inner, capture);
}

internal sealed class ObservedReader(PipeReader inner, Capture capture) : PipeReader
{
    private ReadResult pending;

    public override async ValueTask<ReadResult> ReadAsync(
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            return pending = await inner.ReadAsync(cancellationToken);
        }
        catch
        {
            capture.Unavailable();
            throw;
        }
    }

    public override bool TryRead(out ReadResult result)
    {
        if (!inner.TryRead(out result))
            return false;
        pending = result;
        return true;
    }

    public override void AdvanceTo(SequencePosition consumed) => AdvanceTo(consumed, consumed);

    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
    {
        // Request segments are observed before returning their memory to the underlying reader.
        var bytes = pending.Buffer.Slice(0, consumed);
        var staged = capture.Stage(bytes);
        var eof = pending.IsCompleted && pending.Buffer.Slice(consumed).IsEmpty;
        var count = bytes.Length;
        try
        {
            inner.AdvanceTo(consumed, examined);
            capture.Commit(count, staged);
            capture.Eof |= eof;
        }
        catch
        {
            capture.Unavailable();
            throw;
        }
        pending = default;
    }

    public override void CancelPendingRead() => inner.CancelPendingRead();

    public override void Complete(Exception? exception = null) => inner.Complete(exception);

    public override ValueTask CompleteAsync(Exception? exception = null) =>
        inner.CompleteAsync(exception);
}
