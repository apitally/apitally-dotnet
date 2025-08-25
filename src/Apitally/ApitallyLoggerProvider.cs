namespace Apitally;

using System.Collections.Concurrent;
using Apitally.Models;
using Microsoft.Extensions.Logging;

class ApitallyLoggerProvider : ILoggerProvider
{
    internal static readonly AsyncLocal<List<LogRecord>> LogBuffer = new();
    private const int MaxBufferSize = 1000;
    private const int MaxLogMessageLength = 2048;

    private readonly ConcurrentDictionary<string, ApitallyLogger> _loggers = new();
    private bool _disposed = false;

    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(categoryName, name => new ApitallyLogger(name));
    }

    public static void InitializeLogBuffer()
    {
        LogBuffer.Value = [];
    }

    public static List<LogRecord>? GetLogs()
    {
        return LogBuffer.Value;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _loggers.Clear();
            _disposed = true;
        }
    }

    private class ApitallyLogger(string categoryName) : ILogger
    {
        private readonly string _categoryName = categoryName;

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => new NoOpDisposable();

        private class NoOpDisposable : IDisposable
        {
            public void Dispose() { }
        }

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (!IsEnabled(logLevel))
                return;

            var buffer = LogBuffer.Value;
            if (buffer == null || buffer.Count >= MaxBufferSize)
                return;

            try
            {
                var message = formatter(state, exception);
                if (string.IsNullOrEmpty(message))
                    return;

                if (message.Length > MaxLogMessageLength)
                {
                    const string suffix = "... (truncated)";
                    message = message[..(MaxLogMessageLength - suffix.Length)] + suffix;
                }

                var (fileName, lineNumber) = GetCallerInfo();
                var logRecord = new LogRecord
                {
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Logger = _categoryName,
                    Level = logLevel.ToString(),
                    Message = message,
                    File = fileName,
                    Line = lineNumber,
                };
                buffer.Add(logRecord);
            }
            catch
            {
                // Silently ignore errors in log capture to avoid interfering with application
            }
        }

        private static (string fileName, int lineNumber) GetCallerInfo()
        {
            try
            {
                var stackTrace = new System.Diagnostics.StackTrace(
                    skipFrames: 4,
                    fNeedFileInfo: true
                );
                var frame = stackTrace.GetFrame(0);
                if (frame != null)
                {
                    var fileName = frame.GetFileName() ?? string.Empty;
                    var lineNumber = frame.GetFileLineNumber();
                    return (fileName, lineNumber);
                }
            }
            catch
            {
                // Ignore errors getting caller info
            }

            return (string.Empty, 0);
        }
    }
}
