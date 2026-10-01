using Apitally.Export;
using Apitally.Tests.Support;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Apitally.Tests.Export;

public sealed class ExportWorkerTests : IAsyncDisposable
{
    private readonly FakeTimeProvider timeProvider = new(DateTimeOffset.UtcNow);
    private readonly string directory = Directory.CreateTempSubdirectory("apitally-tests").FullName;
    private readonly DiagnosticsCollector diagnostics = new();
    private readonly TelemetrySpool spool;
    private OtlpReceiver receiver = null!;
    private ExportHttpClient client = null!;
    private ExportWorker worker = null!;

    public ExportWorkerTests() => spool = new(diagnostics.Diagnostics, timeProvider, directory);

    [Fact]
    public async Task FirstCycleRunsAboutTwoSecondsAfterStart()
    {
        await StartAsync();
        spool.Append(TelemetrySignal.Traces, [1]);

        await AdvanceAsync(TimeSpan.FromSeconds(1.9));
        Assert.Empty(receiver.Exports);

        await AdvanceAsync(TimeSpan.FromSeconds(0.2));
        await receiver.WaitForExportsAsync(1);
        Assert.Single(receiver.Exports);
    }

    [Fact]
    public async Task RetryableFailureKeepsFileAndResendsIdenticalBytesNextCycle()
    {
        await StartAsync();
        var attempts = 0;
        receiver.Respond = _ => (Interlocked.Increment(ref attempts) == 1 ? 503 : 200, null);
        spool.Append(TelemetrySignal.Traces, [1]);
        spool.RotateForExport();
        spool.Append(TelemetrySignal.Logs, [2]);

        await AdvanceAsync(TimeSpan.FromSeconds(2.1));
        await receiver.WaitForExportsAsync(1);
        await Task.Delay(100);
        Assert.Single(receiver.Exports);

        await AdvanceUntilAsync(() => receiver.Exports.Count == 3);
        var exports = receiver.Exports.ToList();
        Assert.Equal(
            ["/v1/traces", "/v1/traces", "/v1/logs"],
            exports.Select(export => export.Path)
        );
        Assert.Equal(exports[0].Body, exports[1].Body);
        Assert.Empty(spool.GetPendingFiles());
    }

    [Fact]
    public async Task RejectedFilesAreDroppedWithOneWarningPerStatus()
    {
        await StartAsync();
        receiver.Respond = _ => (401, null);
        for (var i = 0; i < 3; i++)
        {
            spool.Append(TelemetrySignal.Metrics, [(byte)i]);
            spool.CloseCurrentFiles();
        }

        await AdvanceUntilAsync(() => receiver.Exports.Count == 3);
        await Task.Delay(100);

        Assert.Empty(spool.GetPendingFiles());
        Assert.Single(diagnostics.Records(LogLevel.Warning));
    }

    [Fact]
    public async Task CycleSendsAllFilesClosedSinceThePreviousCycle()
    {
        await StartAsync();
        AppendClosedLogFiles(12);

        // The second cycle starts no earlier than 15.5 seconds after start.
        await AdvanceUntilAsync(
            () => receiver.Exports.Count == 12,
            step: TimeSpan.FromSeconds(1),
            limit: TimeSpan.FromSeconds(12)
        );
        Assert.Empty(spool.GetPendingFiles());
    }

    [Fact]
    public async Task CycleSendsNewFilesPlusAtMostTenFilesFromEarlierCycles()
    {
        await StartAsync();
        var attempts = 0;
        receiver.Respond = _ => (Interlocked.Increment(ref attempts) == 1 ? 503 : 200, null);
        AppendClosedLogFiles(12);
        await AdvanceAsync(TimeSpan.FromSeconds(2.1));
        await receiver.WaitForExportsAsync(1);
        await Task.Delay(100);
        spool.Append(TelemetrySignal.Traces, [1]);

        await AdvanceUntilAsync(
            () => receiver.Exports.Count == 12,
            step: TimeSpan.FromSeconds(1),
            limit: TimeSpan.FromSeconds(30)
        );
        await Task.Delay(100);
        Assert.Equal(12, receiver.Exports.Count);

        await worker.StopAsync();
        await worker.SendRemainingFilesAsync(CancellationToken.None);
        Assert.Equal(14, receiver.Exports.Count);
    }

    [Fact]
    public async Task ServerExportIntervalIsClamped()
    {
        await StartAsync();
        receiver.Respond = _ => (200, "1");
        spool.Append(TelemetrySignal.Metrics, [1]);
        await AdvanceAsync(TimeSpan.FromSeconds(2.1));
        await receiver.WaitForExportsAsync(1);
        await Task.Delay(100);
        spool.Append(TelemetrySignal.Metrics, [2]);

        // The clamped 5-second interval has up to 10% jitter.
        await AdvanceAsync(TimeSpan.FromSeconds(4.4));
        Assert.Single(receiver.Exports);
        await AdvanceAsync(TimeSpan.FromSeconds(1.2));
        await receiver.WaitForExportsAsync(1);
    }

    public async ValueTask DisposeAsync()
    {
        await worker.StopAsync();
        client.Dispose();
        spool.Dispose();
        Directory.Delete(directory, recursive: true);
        await receiver.DisposeAsync();
        diagnostics.Dispose();
    }

    private async Task StartAsync()
    {
        receiver = await OtlpReceiver.StartAsync();
        client = new ExportHttpClient(
            receiver.Endpoint,
            TestConfiguration.WriteToken,
            "test",
            null
        );
        worker = new ExportWorker(spool, client, timeProvider, diagnostics.Diagnostics, () => { });
        worker.Start();
        await Task.Delay(50);
    }

    private void AppendClosedLogFiles(int count)
    {
        for (var i = 0; i < count; i++)
        {
            spool.Append(TelemetrySignal.Logs, [(byte)i]);
            spool.CloseCurrentFiles();
        }
    }

    private async Task AdvanceAsync(TimeSpan duration)
    {
        timeProvider.Advance(duration);
        await Task.Delay(50);
    }

    // Advances fake time in small steps, letting the worker's continuations run in between.
    private async Task AdvanceUntilAsync(
        Func<bool> condition,
        TimeSpan? step = null,
        TimeSpan? limit = null
    )
    {
        var elapsed = TimeSpan.Zero;
        while (!condition())
        {
            if (elapsed > (limit ?? TimeSpan.FromMinutes(2)))
                throw new TimeoutException("Condition not reached");
            timeProvider.Advance(step ?? TimeSpan.FromMilliseconds(500));
            elapsed += step ?? TimeSpan.FromMilliseconds(500);
            await Task.Delay(20);
        }
    }
}
