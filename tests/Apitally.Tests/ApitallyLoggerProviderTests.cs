namespace Apitally.Tests;

using Microsoft.Extensions.Logging;
using Xunit;

public class ApitallyLoggerProviderTests : IDisposable
{
    private readonly ApitallyLoggerProvider _loggerProvider;

    public ApitallyLoggerProviderTests()
    {
        _loggerProvider = new ApitallyLoggerProvider();
    }

    public void Dispose()
    {
        _loggerProvider.Dispose();
    }

    [Fact]
    public void InitializeRequestLogBuffer_ShouldCreateEmptyBuffer()
    {
        // Act
        ApitallyLoggerProvider.InitializeLogBuffer();

        // Assert
        var buffer = ApitallyLoggerProvider.LogBufferLocal.Value;
        Assert.NotNull(buffer);
        Assert.Equal(0, buffer.Count);
        Assert.Empty(buffer.GetLogRecords());
    }

    [Fact]
    public void Logger_ShouldCaptureLogsIntoBuffer()
    {
        // Arrange
        ApitallyLoggerProvider.InitializeLogBuffer();
        var logger = _loggerProvider.CreateLogger("Test.Logger");

        // Act
        logger.LogInformation("Test information message");
        logger.LogWarning("Test warning message");
        logger.LogError("Test error message");

        // Assert
        var logs = ApitallyLoggerProvider.GetLogs();
        Assert.NotNull(logs);
        Assert.Equal(3, logs.Count);

        Assert.Equal("Test.Logger", logs[0].Logger);
        Assert.Equal("Information", logs[0].Level);
        Assert.Equal("Test information message", logs[0].Message);

        Assert.Equal("Test.Logger", logs[1].Logger);
        Assert.Equal("Warning", logs[1].Level);
        Assert.Equal("Test warning message", logs[1].Message);

        Assert.Equal("Test.Logger", logs[2].Logger);
        Assert.Equal("Error", logs[2].Level);
        Assert.Equal("Test error message", logs[2].Message);
    }

    [Fact]
    public void Logger_ShouldTruncateLongMessages()
    {
        // Arrange
        ApitallyLoggerProvider.InitializeLogBuffer();
        var logger = _loggerProvider.CreateLogger("Test.Logger");
        var longMessage = new string('A', 3000); // Message longer than MaxLogMessageLength

        // Act
        logger.LogInformation(longMessage);

        // Assert
        var logs = ApitallyLoggerProvider.GetLogs();
        Assert.NotNull(logs);
        Assert.Single(logs);
        Assert.EndsWith("... (truncated)", logs[0].Message);
        Assert.True(logs[0].Message.Length <= 2048);
    }

    [Fact]
    public void Logger_ShouldRespectBufferSizeLimit()
    {
        // Arrange
        ApitallyLoggerProvider.InitializeLogBuffer();
        var logger = _loggerProvider.CreateLogger("Test.Logger");

        // Act - Try to add more than the buffer limit (1000)
        for (int i = 0; i < 1100; i++)
        {
            logger.LogInformation("Message {Index}", i);
        }

        // Assert
        var logs = ApitallyLoggerProvider.GetLogs();
        Assert.NotNull(logs);
        Assert.Equal(1000, logs.Count); // Should be limited to MaxBufferSize
    }

    [Fact]
    public void Logger_ShouldNotCaptureWhenNoBufferInitialized()
    {
        // Arrange - No buffer initialization
        var logger = _loggerProvider.CreateLogger("Test.Logger");

        // Act
        logger.LogInformation("This should not be captured");

        // Assert
        var logs = ApitallyLoggerProvider.GetLogs();
        Assert.Null(logs);
    }
}
