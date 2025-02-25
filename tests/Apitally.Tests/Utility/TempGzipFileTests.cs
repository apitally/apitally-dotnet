namespace Apitally.Tests.Utility;

using System;
using System.IO;
using System.Text;
using Apitally.Utility;
using Xunit;

public class TempGzipFileTests : IDisposable
{
    private TempGzipFile tempFile;

    public TempGzipFileTests()
    {
        tempFile = new TempGzipFile();
    }

    public void Dispose()
    {
        tempFile?.Dispose();
    }

    [Fact]
    public void TestEndToEnd()
    {
        Assert.Equal(36, tempFile.Uuid.ToString().Length);
        Assert.True(File.Exists(tempFile.Path));
        Assert.Equal(0, tempFile.Size);

        tempFile.WriteLine(Encoding.UTF8.GetBytes("test1"));
        tempFile.WriteLine(Encoding.UTF8.GetBytes("test2"));
        Assert.True(tempFile.Size > 0);

        var lines = tempFile.ReadDecompressedLines();
        Assert.Equal(2, lines.Count);
        Assert.Equal("test1", lines[0]);
        Assert.Equal("test2", lines[1]);
    }
}
