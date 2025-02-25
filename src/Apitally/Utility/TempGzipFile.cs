namespace Apitally.Utility;

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

public class TempGzipFile : IDisposable
{
    private readonly Guid _uuid;
    private readonly string _path;
    private FileStream _fileStream;
    private GZipStream _gzipStream;
    private long _size = 0;
    private bool _disposed = false;

    public Guid Uuid => _uuid;
    public string Path => _path;
    public long Size => _size;

    public TempGzipFile()
    {
        _uuid = Guid.NewGuid();
        _path = System.IO.Path.GetTempFileName();
        _fileStream = new FileStream(_path, FileMode.Create, FileAccess.ReadWrite);
        _gzipStream = new GZipStream(_fileStream, CompressionMode.Compress, true);
    }

    public void WriteLine(byte[] data)
    {
        try
        {
            _gzipStream.Write(data, 0, data.Length);
            _gzipStream.WriteByte((byte)'\n');
            _size += data.Length + 1;
        }
        catch (IOException)
        {
            // Ignore
        }
    }

    public Stream GetInputStream()
    {
        // Ensure the current stream is flushed and closed before opening for reading
        _gzipStream.Close();
        _fileStream.Close();
        return new FileStream(_path, FileMode.Open, FileAccess.Read);
    }

    public List<string> ReadDecompressedLines()
    {
        var lines = new List<string>();

        using (var inputStream = GetInputStream())
        using (var gzipStream = new GZipStream(inputStream, CompressionMode.Decompress))
        using (var reader = new StreamReader(gzipStream, Encoding.UTF8))
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    public void Delete()
    {
        try
        {
            Dispose();
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch (IOException)
        {
            // Ignore
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _gzipStream?.Dispose();
                _fileStream?.Dispose();
            }

            _disposed = true;
        }
    }

    ~TempGzipFile()
    {
        Dispose(false);
    }
}
