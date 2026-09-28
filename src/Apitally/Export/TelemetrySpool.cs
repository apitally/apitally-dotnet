using Apitally.Logging;

namespace Apitally.Export;

// Write-through storage of encoded telemetry awaiting delivery. Short operations are
// serialized by one lock; encoding and HTTP happen outside it.
internal sealed class TelemetrySpool : IDisposable
{
    public const long MaxUncompressedFileSize = 4_000_000;
    public const long MaxDiskSize = 50_000_000;
    public const long MaxMemorySize = 10_000_000;

    // Apitally deduplicates for one hour; a later retry could ingest a file twice.
    public static readonly TimeSpan MaxRetryTimeAfterFirstAttempt = TimeSpan.FromMinutes(59);
    public static readonly TimeSpan MaxUntouchedOrphanAge = TimeSpan.FromHours(2);

    private readonly object sync = new();
    private readonly SdkDiagnostics diagnostics;
    private readonly TimeProvider timeProvider;
    private readonly string? directory;
    private readonly Dictionary<TelemetrySignal, SpoolFile> currentFiles = [];
    private readonly List<SpoolFile> closedFiles = [];
    private long nextSequence;

    public TelemetrySpool(
        SdkDiagnostics diagnostics,
        TimeProvider timeProvider,
        string? directory = null
    )
    {
        this.diagnostics = diagnostics;
        this.timeProvider = timeProvider;
        directory ??= Path.GetTempPath();
        if (IsWritable(directory))
        {
            this.directory = directory;
            DeleteOrphanedFiles(directory, timeProvider.GetUtcNow().UtcDateTime);
        }
        else
        {
            diagnostics.SpoolUsingMemory(MaxMemorySize / 1_000_000);
        }
    }

    public bool IsInMemory => directory is null;
    public long MaxSize => IsInMemory ? MaxMemorySize : MaxDiskSize;

    public void Append(TelemetrySignal signal, byte[] payload)
    {
        lock (sync)
        {
            if (
                currentFiles.TryGetValue(signal, out var file)
                && file.UncompressedSize + payload.Length > MaxUncompressedFileSize
            )
            {
                CloseCurrentFile(signal);
                file = null;
            }
            try
            {
                if (file is null)
                {
                    var sequence = nextSequence++;
                    file = directory is null
                        ? SpoolFile.CreateInMemory(signal, sequence)
                        : SpoolFile.CreateInFile(signal, sequence, directory);
                    currentFiles[signal] = file;
                }
                file.Append(payload);
                diagnostics.ResetWarning(SdkDiagnostics.SpoolWriteFailedKey);
            }
            catch (Exception exception)
            {
                diagnostics.SpoolWriteFailed(exception);
                DiscardCurrentFile(signal);
            }
            EnforceSizeLimit();
        }
    }

    // Closes a signal's current file only when none of its files are waiting, so an outage
    // grows the current file instead of producing one file per cycle.
    public void RotateForExport()
    {
        lock (sync)
        {
            foreach (var signal in currentFiles.Keys.ToList())
            {
                if (!closedFiles.Any(file => file.Signal == signal))
                    CloseCurrentFile(signal);
            }
            closedFiles.RemoveAll(DeleteIfExpired);
            EnforceSizeLimit();
        }
    }

    public void CloseCurrentFiles()
    {
        lock (sync)
        {
            foreach (var signal in currentFiles.Keys.ToList())
                CloseCurrentFile(signal);
            EnforceSizeLimit();
        }
    }

    public IReadOnlyList<SpoolFile> GetPendingFiles()
    {
        lock (sync)
            return [.. closedFiles.OrderBy(file => file.Sequence)];
    }

    public void MarkAttempt(SpoolFile file)
    {
        lock (sync)
            file.FirstAttemptAt ??= timeProvider.GetUtcNow();
    }

    // Returns false if the file expired and was deleted instead.
    public bool IsDeliverable(SpoolFile file)
    {
        lock (sync)
        {
            if (!closedFiles.Contains(file))
                return false;
            if (!DeleteIfExpired(file))
                return true;
            closedFiles.Remove(file);
            return false;
        }
    }

    public void Delete(SpoolFile file)
    {
        lock (sync)
        {
            closedFiles.Remove(file);
            file.Delete();
        }
    }

    // Live processes refresh modification times so orphan cleanup elsewhere keeps their files.
    public void TouchFiles()
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        lock (sync)
        {
            foreach (var file in currentFiles.Values.Concat(closedFiles))
                file.Touch(now);
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            foreach (var file in currentFiles.Values.Concat(closedFiles))
                file.Delete();
            currentFiles.Clear();
            closedFiles.Clear();
        }
    }

    private void CloseCurrentFile(TelemetrySignal signal)
    {
        if (!currentFiles.Remove(signal, out var file))
            return;
        try
        {
            file.Close();
            closedFiles.Add(file);
        }
        catch (Exception exception)
        {
            diagnostics.SpoolWriteFailed(exception);
            file.Delete();
        }
    }

    private void DiscardCurrentFile(TelemetrySignal signal)
    {
        if (currentFiles.Remove(signal, out var file))
            file.Delete();
    }

    private bool DeleteIfExpired(SpoolFile file)
    {
        if (
            file.FirstAttemptAt is not { } firstAttempt
            || timeProvider.GetUtcNow() - firstAttempt <= MaxRetryTimeAfterFirstAttempt
        )
            return false;
        diagnostics.SpoolFileExpired(file.Signal);
        file.Delete();
        return true;
    }

    // Metrics carry the liveness signal, so other signals are evicted first.
    private void EnforceSizeLimit()
    {
        while (currentFiles.Values.Concat(closedFiles).Sum(file => file.StoredSize) > MaxSize)
        {
            var byAge = closedFiles.OrderBy(file => file.Sequence).ToList();
            var oldest =
                byAge.FirstOrDefault(file => file.Signal != TelemetrySignal.Metrics)
                ?? byAge.FirstOrDefault();
            if (oldest is null)
                return;
            diagnostics.SpoolSizeLimitReached(oldest.Signal);
            closedFiles.Remove(oldest);
            oldest.Delete();
        }
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            using var probe = SpoolFile.CreateOwnerOnlyFile(directory);
            File.Delete(probe.Name);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void DeleteOrphanedFiles(string directory, DateTime utcNow)
    {
        try
        {
            var pattern = SpoolFile.FilePrefix + "*" + SpoolFile.FileExtension;
            foreach (var path in Directory.EnumerateFiles(directory, pattern))
            {
                try
                {
                    if (utcNow - File.GetLastWriteTimeUtc(path) > MaxUntouchedOrphanAge)
                        File.Delete(path);
                }
                catch
                {
                    // Another process may have removed the file first.
                }
            }
        }
        catch
        {
            // Orphan cleanup is best effort.
        }
    }
}
