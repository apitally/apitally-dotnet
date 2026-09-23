using System.Buffers;
using System.IO.Pipelines;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace TransportLifecycle;

// Experimental transport observer. Deliberately not an SDK or a full capture policy.
internal sealed class TransportFilter(ProbeState state) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
        app =>
        {
            app.Use(
                async (context, downstream) =>
                {
                    state.Activate("request");
                    var record = state.Begin(context);
                    var response = context.Features.Get<IHttpResponseBodyFeature>()!;
                    var requestStream = context.Request.Body;
                    var requestPipe = context.Features.Get<IRequestBodyPipeFeature>();
                    var requestReader = context.Request.BodyReader;
                    var lifetime = context.Features.Get<IHttpRequestLifetimeFeature>()!;
                    context.Features.Set<IHttpRequestLifetimeFeature>(
                        new ObservedLifetime(lifetime, record)
                    );
                    var aborted = context.RequestAborted.Register(() =>
                    {
                        record.Aborted = true;
                        record.Note("request.aborted");
                    });
                    context.Request.Body = new ObservedStream(
                        requestStream,
                        record.Request,
                        reading: true
                    );
                    context.Features.Set<IRequestBodyPipeFeature>(
                        new ObservedRequestPipe(requestReader, record.Request)
                    );
                    context.Features.Set<IHttpResponseBodyFeature>(
                        new ObservedResponse(response, record)
                    );
                    context.Response.OnStarting(() =>
                    {
                        record.Note("observer.starting");
                        record.ReadFinalMetadata(context);
                        return Task.CompletedTask;
                    });
                    context.Response.OnCompleted(() =>
                    {
                        record.Note("observer.completed");
                        record.ReadFinalMetadata(context);
                        record.AbortTokenAtCompletion = context
                            .RequestAborted
                            .IsCancellationRequested;
                        record.Aborted |= record.AbortTokenAtCompletion;
                        aborted.Dispose();
                        record.ResponseComplete =
                            !record.Aborted
                            && !record.Escaped
                            && (
                                context.Response.ContentLength is not long length
                                || length == record.Response.Count
                            );
                        record.TransportEnded = true;
                        state.TryRelease(record);
                        record.Completed.TrySetResult();
                        return Task.CompletedTask;
                    });
                    try
                    {
                        await downstream(context);
                    }
                    catch
                    {
                        record.Escaped = true;
                        throw;
                    }
                    finally
                    {
                        record.ReadFinalMetadata(context);
                        record.Note("observer.finally");
                        context.Features.Set(response);
                        context.Request.Body = requestStream;
                        context.Features.Set(requestPipe);
                        context.Features.Set(lifetime);
                    }
                }
            );
            next(app);
        };
}

internal sealed class ObservedLifetime(IHttpRequestLifetimeFeature inner, RequestRecord record)
    : IHttpRequestLifetimeFeature
{
    public CancellationToken RequestAborted
    {
        get => inner.RequestAborted;
        set => inner.RequestAborted = value;
    }

    public void Abort()
    {
        record.Aborted = true;
        record.Note("feature.abort");
        inner.Abort();
    }
}

internal sealed class BodyObservation(Func<(string? Type, long? Length)> headers)
{
    public const int Limit = 50_000;
    private byte[]? bytes;
    private int used;
    public long Count { get; private set; }
    public bool TooLarge { get; private set; }
    public bool Missing { get; private set; }
    public bool Eof { get; set; }
    public int PeakRetained { get; private set; }
    public int Retained => used;
    public bool Complete => Eof || headers().Length == Count;

    public byte[]? Captured(bool complete) =>
        TooLarge || Missing || !complete || used == 0 ? null : bytes![..used];

    public void Observe(ReadOnlySpan<byte> data)
    {
        Count += data.Length;
        var (type, length) = headers();
        // The probe needs two types, not a replacement for the canonical SDK allowlist.
        if (
            type?.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase) != true
            && type?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true
        )
            return;
        if (length > Limit || Count > Limit)
        {
            TooLarge = true;
            bytes = null;
            used = 0;
        }
        if (TooLarge || Missing || data.IsEmpty)
            return;
        bytes ??= new byte[Limit];
        data.CopyTo(bytes.AsSpan(used));
        used += data.Length;
        PeakRetained = Math.Max(PeakRetained, used);
    }

    public void ObserveUncapturedFile(long count)
    {
        Count += count;
        Missing = true;
        bytes = null;
        used = 0;
    }
}

internal sealed class ObservedResponse(IHttpResponseBodyFeature inner, RequestRecord record)
    : IHttpResponseBodyFeature
{
    public Stream Stream { get; } =
        new ObservedStream(inner.Stream, record.Response, reading: false);
    public PipeWriter Writer { get; } = new ObservedWriter(inner.Writer, record.Response, record);

    public void DisableBuffering()
    {
        record.Note("feature.disable-buffering");
        inner.DisableBuffering();
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        record.Note("feature.start");
        return inner.StartAsync(cancellationToken);
    }

    public async Task CompleteAsync()
    {
        record.Note("feature.complete.enter");
        await inner.CompleteAsync();
        record.Note("feature.complete.exit");
    }

    public async Task SendFileAsync(
        string path,
        long offset,
        long? count,
        CancellationToken cancellationToken = default
    )
    {
        record.Note("feature.send-file");
        var size = count ?? new FileInfo(path).Length - offset;
        await inner.SendFileAsync(path, offset, count, cancellationToken);
        // Delegate the native file send. Do not read the file again just to capture it.
        record.Response.ObserveUncapturedFile(size);
    }
}

internal sealed class ObservedWriter(
    PipeWriter inner,
    BodyObservation observation,
    RequestRecord record
) : PipeWriter
{
    private Memory<byte> memory;
    public override bool CanGetUnflushedBytes => inner.CanGetUnflushedBytes;
    public override long UnflushedBytes => inner.UnflushedBytes;

    public override Memory<byte> GetMemory(int sizeHint = 0) => memory = inner.GetMemory(sizeHint);

    public override Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

    public override void Advance(int bytes)
    {
        observation.Observe(memory.Span[..bytes]);
        inner.Advance(bytes);
        memory = default;
    }

    public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
    {
        record.Note("writer.flush");
        return inner.FlushAsync(cancellationToken);
    }

    public override void CancelPendingFlush() => inner.CancelPendingFlush();

    public override void Complete(Exception? exception = null) => inner.Complete(exception);

    public override ValueTask CompleteAsync(Exception? exception = null) =>
        inner.CompleteAsync(exception);
}

internal sealed class ObservedRequestPipe(PipeReader reader, BodyObservation observation)
    : IRequestBodyPipeFeature
{
    public PipeReader Reader { get; } = new ObservedReader(reader, observation);
}

internal sealed class ObservedReader(PipeReader inner, BodyObservation observation) : PipeReader
{
    private ReadResult pending;

    public override async ValueTask<ReadResult> ReadAsync(
        CancellationToken cancellationToken = default
    ) => pending = await inner.ReadAsync(cancellationToken);

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
        var consumedBytes = pending.Buffer.Slice(0, consumed);
        foreach (var segment in consumedBytes)
            observation.Observe(segment.Span);
        observation.Eof |= pending.IsCompleted && pending.Buffer.Slice(consumed).IsEmpty;
        inner.AdvanceTo(consumed, examined);
        pending = default;
    }

    public override void CancelPendingRead() => inner.CancelPendingRead();

    public override void Complete(Exception? exception = null) => inner.Complete(exception);

    public override ValueTask CompleteAsync(Exception? exception = null) =>
        inner.CompleteAsync(exception);
}

internal sealed class ObservedStream(Stream inner, BodyObservation observation, bool reading)
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

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        inner.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void SetLength(long value) => inner.SetLength(value);

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var count = inner.Read(buffer);
        if (reading)
        {
            observation.Observe(buffer[..count]);
            observation.Eof |= count == 0 && buffer.Length != 0;
        }
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
        var count = await inner.ReadAsync(buffer, cancellationToken);
        if (reading)
        {
            observation.Observe(buffer.Span[..count]);
            observation.Eof |= count == 0 && buffer.Length != 0;
        }
        return count;
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        inner.Write(buffer);
        if (!reading)
            observation.Observe(buffer);
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
        await inner.WriteAsync(buffer, cancellationToken);
        if (!reading)
            observation.Observe(buffer.Span);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() => inner.DisposeAsync();
}
