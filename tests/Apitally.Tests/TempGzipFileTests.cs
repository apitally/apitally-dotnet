namespace Apitally.Tests;

using System;
using System.IO;
using System.Text;
using Apitally;
using Xunit;

public class TempGzipFileTests : IDisposable
{
    private readonly TempGzipFile _tempFile;

    public TempGzipFileTests()
    {
        _tempFile = new TempGzipFile();
    }

    public void Dispose()
    {
        _tempFile.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void WriteLine_ShouldWriteCompressed()
    {
        Assert.Equal(36, _tempFile.Uuid.ToString().Length);
        Assert.True(File.Exists(_tempFile.Path));
        Assert.Equal(0, _tempFile.Size);

        _tempFile.WriteLine(Encoding.UTF8.GetBytes("test1"));
        _tempFile.WriteLine(Encoding.UTF8.GetBytes("test2"));
        Assert.True(_tempFile.Size > 0);

        var lines = _tempFile.ReadDecompressedLines();
        Assert.Equal(2, lines.Count);
        Assert.Equal("test1", lines[0]);
        Assert.Equal("test2", lines[1]);
    }

    [Fact]
    public void Delete_ShouldRemoveFile()
    {
        string filePath = _tempFile.Path;
        Assert.True(File.Exists(filePath));

        _tempFile.Delete();
        Assert.False(File.Exists(filePath));
    }
}
