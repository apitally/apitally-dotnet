using OpenTelemetry;

namespace Apitally.Export;

// Stock OTel batching for Apitally-owned span and log entries. All settings are explicit, so
// OTEL_BSP_* and OTEL_BLRP_* environment variables never tune this pipeline.
internal sealed class ApitallyBatchProcessor<T> : BatchExportProcessor<T>
    where T : class
{
    public const int MaxQueueSize = 2_048;
    public const int ScheduledDelayMilliseconds = 1_000;
    public const int ExporterTimeoutMilliseconds = 30_000;
    public const int MaxExportBatchSize = 512;

    public ApitallyBatchProcessor(Action<IReadOnlyList<T>> export)
        : base(
            new DelegatingExporter(export),
            MaxQueueSize,
            ScheduledDelayMilliseconds,
            ExporterTimeoutMilliseconds,
            MaxExportBatchSize
        ) { }

    private sealed class DelegatingExporter(Action<IReadOnlyList<T>> export) : BaseExporter<T>
    {
        public override ExportResult Export(in Batch<T> batch)
        {
            var items = new List<T>((int)batch.Count);
            foreach (var item in batch)
                items.Add(item);
            try
            {
                export(items);
                return ExportResult.Success;
            }
            catch
            {
                return ExportResult.Failure;
            }
        }
    }
}
