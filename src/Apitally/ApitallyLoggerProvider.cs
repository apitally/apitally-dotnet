namespace Apitally;

using System.Collections.Concurrent;
using System.Threading;
using Apitally.Models;
using Microsoft.Extensions.Logging;

class ApitallyLoggerProvider : ILoggerProvider
{
    internal static readonly AsyncLocal<LogBuffer?> LogBufferLocal = new();
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
        LogBufferLocal.Value = new LogBuffer();
    }

    public static List<LogRecord>? GetLogs()
    {
        return LogBufferLocal.Value?.GetLogRecords();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _loggers.Clear();
            _disposed = true;
        }
    }

    internal class LogBuffer
    {
        private readonly ConcurrentQueue<LogRecord> _queue = new();
        private int _count = 0;

        public int Count => _count;

        public void Enqueue(LogRecord record)
        {
            _queue.Enqueue(record);
            Interlocked.Increment(ref _count);
        }

        public List<LogRecord> GetLogRecords()
        {
            return [.. _queue];
        }
    }

    private class ApitallyLogger(string categoryName) : ILogger
    {
        private readonly string _categoryName = categoryName;

        IDisposable ILogger.BeginScope<TState>(TState state) => NoOpDisposable.Instance;

        private sealed class NoOpDisposable : IDisposable
        {
            public static readonly NoOpDisposable Instance = new();

            public void Dispose() { }
        }

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None && !_categoryName.StartsWith("Microsoft.AspNetCore.");

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

            var buffer = LogBufferLocal.Value;
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

                var logRecord = new LogRecord
                {
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d,
                    Logger = _categoryName,
                    Level = logLevel.ToString(),
                    Message = message,
                };
                buffer.Enqueue(logRecord);
            }
            catch
            {
                // Silently ignore errors in log capture to avoid interfering with application
            }
        }
    }
}
