using System.Collections.Concurrent;
using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Trace;

internal sealed class SnapshotBatchProcessor(
    BaseExporter<SpanSnapshot> exporter,
    int delay = 1000,
    int batchSize = 8,
    int queueSize = 32
) : BatchExportProcessor<SpanSnapshot>(exporter, queueSize, delay, 1000, batchSize);

internal sealed class SnapshotProcessor(Action<SpanSnapshot> accept, bool requireRecorded = true)
    : BaseProcessor<Activity>
{
    internal int EndCount { get; private set; }
    internal int RecordOnlyCount { get; private set; }

    public override void OnEnd(Activity activity)
    {
        EndCount++;
        if (!activity.Recorded)
        {
            RecordOnlyCount++;
            if (requireRecorded)
            {
                return;
            }
        }
        accept(SpanSnapshot.Copy(activity, ParentProvider!.GetResource()));
    }
}

internal sealed class ProbeExporter<T>(Action<T>? observe = null) : BaseExporter<T>
    where T : class
{
    internal ConcurrentQueue<T> Items { get; } = new();
    internal ConcurrentQueue<Exception> Errors { get; } = new();
    internal ManualResetEventSlim Exported { get; } = new(false);
    internal Thread? Worker { get; private set; }
    internal int ShutdownCalls { get; private set; }
    internal int DisposeCalls { get; private set; }

    public override ExportResult Export(in Batch<T> batch)
    {
        Worker = Thread.CurrentThread;
        try
        {
            foreach (var item in batch)
            {
                observe?.Invoke(item);
                Items.Enqueue(item);
            }
            return ExportResult.Success;
        }
        catch (Exception exception)
        {
            Errors.Enqueue(exception);
            return ExportResult.Failure;
        }
        finally
        {
            Exported.Set();
        }
    }

    protected override bool OnShutdown(int timeoutMilliseconds)
    {
        ShutdownCalls++;
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        DisposeCalls++;
        base.Dispose(disposing);
    }
}

internal sealed class NameSampler : Sampler
{
    public override SamplingResult ShouldSample(in SamplingParameters parameters) =>
        new(
            parameters.Name switch
            {
                "recorded" => SamplingDecision.RecordAndSample,
                "record-only" => SamplingDecision.RecordOnly,
                _ => SamplingDecision.Drop,
            }
        );
}
