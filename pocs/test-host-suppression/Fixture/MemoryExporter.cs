using System.Collections.Concurrent;
using System.Diagnostics;
using OpenTelemetry;

namespace TestHostSuppression;

public sealed class MemoryExporter(Func<bool>? enabled = null) : BaseExporter<Activity>
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Span>> completions = new();
    private readonly TaskCompletionSource disposed = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    public readonly ConcurrentQueue<Span> Spans = new();
    public int ExportCalls;
    public int DisposeCount;

    public Task<Span> WaitForServerSpan(string traceId) =>
        Completion(traceId).Task.WaitAsync(TimeSpan.FromSeconds(5));

    public Task WaitForDisposal() => disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));

    public override ExportResult Export(in Batch<Activity> batch)
    {
        Interlocked.Increment(ref ExportCalls);
        if (enabled is not null && !enabled())
            return ExportResult.Success;
        foreach (var activity in batch)
        {
            var span = new Span(
                activity.TraceId.ToHexString(),
                activity.Kind,
                activity.Recorded,
                activity.Duration,
                activity.GetTagItem("http.route")?.ToString(),
                activity.GetTagItem("http.response.status_code")?.ToString()
            );
            Spans.Enqueue(span);
            if (span.Kind == ActivityKind.Server)
                Completion(span.TraceId).TrySetResult(span);
        }
        return ExportResult.Success;
    }

    protected override void Dispose(bool disposing)
    {
        Interlocked.Increment(ref DisposeCount);
        disposed.TrySetResult();
    }

    private TaskCompletionSource<Span> Completion(string traceId) =>
        completions.GetOrAdd(
            traceId,
            _ => new TaskCompletionSource<Span>(TaskCreationOptions.RunContinuationsAsynchronously)
        );
}

public sealed record Span(
    string TraceId,
    ActivityKind Kind,
    bool Recorded,
    TimeSpan Duration,
    string? Route,
    string? StatusCode
);
