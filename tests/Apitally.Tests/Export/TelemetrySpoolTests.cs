using Apitally.Export;
using Apitally.Tests.Support;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Trace.V1;

namespace Apitally.Tests.Export;

public sealed class TelemetrySpoolTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("apitally-tests").FullName;
    private readonly DiagnosticsCollector diagnostics = new();
    private readonly FakeTimeProvider timeProvider = new(DateTimeOffset.UtcNow);

    [Fact]
    public void AppendedRequestsFormOneContinuousGzipStream()
    {
        using var spool = CreateSpool();

        spool.Append(TelemetrySignal.Traces, Request("first").ToByteArray());
        spool.Append(TelemetrySignal.Traces, Request("second").ToByteArray());
        spool.RotateForExport();

        var file = Assert.Single(spool.GetPendingFiles());
        var bytes = file.ReadStoredBytes();
        var merged = OtlpDecoding.Traces(bytes);
        Assert.Equal(
            ["first", "second"],
            merged.ResourceSpans.SelectMany(r => r.ScopeSpans).Select(s => s.Scope.Name)
        );
        Assert.Equal(bytes, file.ReadStoredBytes());
    }

    [Fact]
    public async Task ConcurrentAppendsAndRotationsProduceCompleteFiles()
    {
        using var spool = CreateSpool();
        var appends = Enumerable
            .Range(0, 4)
            .Select(writer =>
                Task.Run(() =>
                {
                    for (var i = 0; i < 200; i++)
                        spool.Append(
                            TelemetrySignal.Traces,
                            Request($"{writer}-{i}").ToByteArray()
                        );
                })
            )
            .ToList();
        var contents = new List<byte[]>();
        while (!appends.All(append => append.IsCompleted))
        {
            spool.RotateForExport();
            foreach (var file in spool.GetPendingFiles())
            {
                contents.Add(file.ReadStoredBytes());
                spool.Delete(file);
            }
        }
        await Task.WhenAll(appends);
        spool.CloseCurrentFiles();
        contents.AddRange(spool.GetPendingFiles().Select(file => file.ReadStoredBytes()));

        var names = contents
            .SelectMany(bytes => OtlpDecoding.Traces(bytes).ResourceSpans)
            .SelectMany(resource => resource.ScopeSpans)
            .Select(scope => scope.Scope.Name)
            .ToList();
        Assert.Equal(800, names.Count);
        Assert.Equal(800, names.Distinct().Count());
    }

    [Fact]
    public void SpoolFilesAreOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var spool = CreateSpool();

        spool.Append(TelemetrySignal.Logs, [1, 2, 3]);

        var path = Assert.Single(Directory.GetFiles(directory));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void FileRotatesBeforeExceedingTheUncompressedLimit()
    {
        using var spool = CreateSpool();

        spool.Append(TelemetrySignal.Traces, new byte[3_000_000]);
        spool.Append(TelemetrySignal.Traces, new byte[1_000_000]);
        spool.Append(TelemetrySignal.Traces, new byte[1]);
        spool.CloseCurrentFiles();

        Assert.Equal(
            [4_000_000L, 1L],
            spool.GetPendingFiles().Select(file => file.UncompressedSize)
        );
    }

    [Fact]
    public void RotateForExportReturnsFilesClosedSinceThePreviousCall()
    {
        using var spool = CreateSpool();
        spool.Append(TelemetrySignal.Traces, new byte[3_000_000]);
        spool.Append(TelemetrySignal.Traces, new byte[1_000_001]);
        spool.Append(TelemetrySignal.Logs, [1]);

        // The size-closed traces file and the logs file; the current traces file waits.
        Assert.Equal(2, spool.RotateForExport());
        Assert.Equal(0, spool.RotateForExport());
    }

    [Fact]
    public void CurrentFileRotatesForExportOnlyWithoutBacklog()
    {
        using var spool = CreateSpool();
        spool.Append(TelemetrySignal.Traces, [1]);
        spool.RotateForExport();

        spool.Append(TelemetrySignal.Traces, [2]);
        spool.Append(TelemetrySignal.Logs, [3]);
        spool.RotateForExport();

        Assert.Equal(
            [TelemetrySignal.Traces, TelemetrySignal.Logs],
            spool.GetPendingFiles().Select(file => file.Signal)
        );
    }

    [Fact]
    public void FilesExpireOnlyAfterFirstAttempt()
    {
        using var spool = CreateSpool();
        spool.Append(TelemetrySignal.Traces, [1]);
        spool.RotateForExport();
        var file = Assert.Single(spool.GetPendingFiles());

        timeProvider.Advance(TimeSpan.FromHours(3));
        Assert.True(spool.IsDeliverable(file));

        spool.MarkAttempt(file);
        timeProvider.Advance(TimeSpan.FromMinutes(59));
        Assert.True(spool.IsDeliverable(file));

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        Assert.False(spool.IsDeliverable(file));
        Assert.Empty(spool.GetPendingFiles());
        Assert.Single(diagnostics.Records(LogLevel.Warning));
    }

    [Fact]
    public void SizeLimitEvictsOldestNonMetricsFilesFirst()
    {
        using var spool = CreateMemorySpool();
        foreach (
            var signal in new[]
            {
                TelemetrySignal.Metrics,
                TelemetrySignal.Traces,
                TelemetrySignal.Logs,
            }
        )
        {
            spool.Append(signal, RandomBytes(3_000_000));
            spool.CloseCurrentFiles();
        }

        spool.Append(TelemetrySignal.Metrics, RandomBytes(3_000_000));
        spool.CloseCurrentFiles();

        Assert.Equal(
            [TelemetrySignal.Metrics, TelemetrySignal.Logs, TelemetrySignal.Metrics],
            spool.GetPendingFiles().Select(file => file.Signal)
        );
    }

    [Fact]
    public void UnwritableDirectoryFallsBackToMemoryWithOneWarning()
    {
        using var spool = CreateMemorySpool();

        spool.Append(TelemetrySignal.Logs, Request("x").ToByteArray());
        spool.RotateForExport();

        Assert.True(spool.IsInMemory);
        Assert.Single(diagnostics.Records(LogLevel.Warning));
        Assert.Single(
            OtlpDecoding
                .Traces(Assert.Single(spool.GetPendingFiles()).ReadStoredBytes())
                .ResourceSpans
        );
    }

    [Fact]
    public void WriteFailureDiscardsOnlyTheAffectedFile()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var spool = CreateSpool();
        spool.Append(TelemetrySignal.Metrics, [1]);
        spool.RotateForExport();
        spool.Append(TelemetrySignal.Logs, [2]);

        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            spool.Append(TelemetrySignal.Traces, [3]);
            spool.Append(TelemetrySignal.Logs, [4]);
        }
        finally
        {
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            );
        }
        spool.CloseCurrentFiles();

        Assert.Equal(
            [TelemetrySignal.Metrics, TelemetrySignal.Logs],
            spool.GetPendingFiles().Select(file => file.Signal)
        );
        Assert.Equal(2, spool.GetPendingFiles()[1].UncompressedSize);
        Assert.Single(diagnostics.Records(LogLevel.Warning));
    }

    [Fact]
    public void StaleOrphanedFilesAreDeletedAtConstruction()
    {
        var stale = Path.Combine(directory, "apitally-stale.gz");
        var fresh = Path.Combine(directory, "apitally-fresh.gz");
        var other = Path.Combine(directory, "other.gz");
        foreach (var path in new[] { stale, fresh, other })
        {
            File.WriteAllBytes(path, [1]);
            File.SetLastWriteTimeUtc(path, timeProvider.GetUtcNow().UtcDateTime.AddHours(-3));
        }
        File.SetLastWriteTimeUtc(fresh, timeProvider.GetUtcNow().UtcDateTime.AddHours(-1));

        using var spool = CreateSpool();

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(other));
    }

    public void Dispose()
    {
        diagnostics.Dispose();
        Directory.Delete(directory, recursive: true);
    }

    private TelemetrySpool CreateSpool() => new(diagnostics.Diagnostics, timeProvider, directory);

    private TelemetrySpool CreateMemorySpool() =>
        new(diagnostics.Diagnostics, timeProvider, Path.Combine(directory, "missing"));

    private static ExportTraceServiceRequest Request(string scopeName) =>
        new()
        {
            ResourceSpans =
            {
                new ResourceSpans
                {
                    ScopeSpans = { new ScopeSpans { Scope = new() { Name = scopeName } } },
                },
            },
        };

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }
}
