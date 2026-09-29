using System.IO.Compression;

namespace Apitally.Export;

internal enum TelemetrySignal
{
    Traces,
    Logs,
    Metrics,
}

// One continuous gzip stream of concatenated same-signal OTLP requests, stored in a temporary
// file or in memory. A closed file's bytes never change, so every retry sends identical bytes.
internal sealed class SpoolFile
{
    public const string FilePrefix = "apitally-";
    public const string FileExtension = ".gz";

    private readonly FileStream? fileStream;
    private MemoryStream? memoryStream;
    private GZipStream? gzip;
    private byte[]? closedMemory;
    private long? closedSize;

    private SpoolFile(
        TelemetrySignal signal,
        long sequence,
        FileStream? fileStream,
        MemoryStream? memoryStream
    )
    {
        Signal = signal;
        Sequence = sequence;
        this.fileStream = fileStream;
        this.memoryStream = memoryStream;
        gzip = new GZipStream(
            (Stream?)fileStream ?? memoryStream!,
            CompressionLevel.Fastest,
            leaveOpen: true
        );
    }

    public TelemetrySignal Signal { get; }

    // Creation order across all signals.
    public long Sequence { get; }
    public string? Path => fileStream?.Name;
    public long UncompressedSize { get; private set; }
    public DateTimeOffset? FirstAttemptAt { get; set; }

    public long StoredSize => closedSize ?? fileStream?.Length ?? memoryStream!.Length;

    public static SpoolFile CreateInFile(TelemetrySignal signal, long sequence, string directory) =>
        new(signal, sequence, CreateOwnerOnlyFile(directory), null);

    public static SpoolFile CreateInMemory(TelemetrySignal signal, long sequence) =>
        new(signal, sequence, null, new MemoryStream());

    public static FileStream CreateOwnerOnlyFile(string directory)
    {
        var path = System.IO.Path.Combine(directory, FilePrefix + Guid.NewGuid() + FileExtension);
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.Read | FileShare.Delete,
        };
        // Spool files contain masked but potentially sensitive payloads. Windows %TEMP% is per-user.
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }

    public void Append(byte[] payload)
    {
        gzip!.Write(payload);
        UncompressedSize += payload.Length;
    }

    // Writes the gzip trailer; only closed files are sent.
    public void Close()
    {
        gzip!.Dispose();
        gzip = null;
        if (fileStream is not null)
        {
            fileStream.Flush(flushToDisk: false);
            closedSize = fileStream.Length;
            fileStream.Dispose();
        }
        else
        {
            closedMemory = memoryStream!.ToArray();
            closedSize = closedMemory.Length;
            // Disposing does not release the stream's buffer.
            memoryStream.Dispose();
            memoryStream = null;
        }
    }

    // Each send reads through its own handle, so deleting the file concurrently is safe.
    public byte[] ReadStoredBytes()
    {
        if (closedMemory is not null)
            return closedMemory;
        using var stream = new FileStream(
            Path!,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete
        );
        using var output = new MemoryStream((int)stream.Length);
        stream.CopyTo(output);
        return output.ToArray();
    }

    public void Touch(DateTime utcNow)
    {
        try
        {
            if (Path is not null)
                File.SetLastWriteTimeUtc(Path, utcNow);
        }
        catch
        {
            // The file may already be gone; orphan cleanup only removes stale files.
        }
    }

    public void Delete()
    {
        try
        {
            gzip?.Dispose();
        }
        catch
        {
            // Discarding a file whose writes failed.
        }
        gzip = null;
        fileStream?.Dispose();
        memoryStream?.Dispose();
        closedMemory = null;
        try
        {
            if (Path is not null)
                File.Delete(Path);
        }
        catch
        {
            // Another process or an earlier deletion may have removed it.
        }
    }
}
