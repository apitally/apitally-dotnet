using Apitally.Logging;
using OpenTelemetry;

namespace Apitally.Export;

// Runs export cycles independently of request traffic: flush intake, rotate the spool and
// send closed files oldest first. Retry pacing comes only from the cycle schedule.
internal sealed class ExportWorker
{
    public static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(15);
    private const int MinIntervalSeconds = 5;
    private const int MaxIntervalSeconds = 60;
    private const int MaxSendsPerCycle = 10;

    private readonly TelemetrySpool spool;
    private readonly ExportHttpClient client;
    private readonly TimeProvider timeProvider;
    private readonly SdkDiagnostics diagnostics;
    private readonly Action flushIntake;
    private readonly CancellationTokenSource stopping = new();
    private Task loop = Task.CompletedTask;

    public ExportWorker(
        TelemetrySpool spool,
        ExportHttpClient client,
        TimeProvider timeProvider,
        SdkDiagnostics diagnostics,
        Action flushIntake
    )
    {
        this.spool = spool;
        this.client = client;
        this.timeProvider = timeProvider;
        this.diagnostics = diagnostics;
        this.flushIntake = flushIntake;
    }

    public TimeSpan Interval { get; private set; } = DefaultInterval;

    public void Start()
    {
        // Background work must not inherit the activating request's context.
        using (ExecutionContext.SuppressFlow())
            loop = Task.Run(RunAsync);
    }

    // Stops scheduling and returns once any in-progress cycle has finished.
    public async Task StopAsync()
    {
        stopping.Cancel();
        await loop.ConfigureAwait(false);
    }

    // Sends all remaining files without pauses or the per-cycle cap.
    public async Task SendRemainingFilesAsync(CancellationToken cancellationToken)
    {
        using var suppression = SuppressInstrumentationScope.Begin();
        await SendPendingFilesAsync(isFinal: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunAsync()
    {
        try
        {
            await Task.Delay(InitialDelay, timeProvider, stopping.Token).ConfigureAwait(false);
            while (!stopping.IsCancellationRequested)
            {
                await RunCycleAsync().ConfigureAwait(false);
                // Jitter desynchronizes processes that started together.
                var delay = Interval * (0.9 + Random.Shared.NextDouble() * 0.2);
                await Task.Delay(delay, timeProvider, stopping.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunCycleAsync()
    {
        try
        {
            // The worker's flushes and POSTs must not generate telemetry.
            using var suppression = SuppressInstrumentationScope.Begin();
            flushIntake();
            spool.RotateForExport();
            spool.TouchFiles();
            await SendPendingFilesAsync(isFinal: false, stopping.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            diagnostics.ExportCycleFailed(exception);
        }
    }

    // During an outage, stopping at the first retryable failure limits each cycle to one probe.
    private async Task SendPendingFilesAsync(bool isFinal, CancellationToken cancellationToken)
    {
        var sent = 0;
        foreach (var file in spool.GetPendingFiles())
        {
            if (cancellationToken.IsCancellationRequested || (!isFinal && sent >= MaxSendsPerCycle))
                return;
            if (!spool.IsDeliverable(file))
                continue;
            if (!isFinal && sent > 0)
            {
                var pause = TimeSpan.FromMilliseconds(100 + Random.Shared.NextDouble() * 400);
                try
                {
                    await Task.Delay(pause, timeProvider, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
            sent++;
            if (!await SendFileAsync(file, cancellationToken).ConfigureAwait(false))
                return;
        }
    }

    // Returns false on a retryable failure, which keeps the file and ends the sequence.
    private async Task<bool> SendFileAsync(SpoolFile file, CancellationToken cancellationToken)
    {
        spool.MarkAttempt(file);
        byte[] body;
        try
        {
            body = file.ReadStoredBytes();
        }
        catch
        {
            // An evicted file was already removed.
            spool.Delete(file);
            return true;
        }
        var response = await client
            .PostAsync(file.Signal, body, cancellationToken)
            .ConfigureAwait(false);
        if (response.ExportIntervalSeconds is { } seconds)
            Interval = TimeSpan.FromSeconds(
                Math.Clamp(seconds, MinIntervalSeconds, MaxIntervalSeconds)
            );
        switch (response.Outcome)
        {
            case ExportOutcome.Accepted:
                spool.Delete(file);
                return true;
            case ExportOutcome.Rejected:
                diagnostics.ExportRejected(file.Signal, response.StatusCode ?? 0);
                spool.Delete(file);
                return true;
            default:
                diagnostics.ExportRetryable(file.Signal, response.StatusCode);
                return false;
        }
    }
}
