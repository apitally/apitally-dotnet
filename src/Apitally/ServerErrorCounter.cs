namespace Apitally;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Apitally.Models;

class ServerErrorCounter
{
    private const int MaxMessageLength = 2048;
    private const int MaxStackTraceLength = 65536;

    private readonly ConcurrentDictionary<string, int> _errorCounts = new();
    private readonly ConcurrentDictionary<string, ServerErrorDetails> _errorDetails = new();

    private class ServerErrorDetails
    {
        public string Consumer { get; set; } = string.Empty;
        public string Method { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string StackTrace { get; set; } = string.Empty;
    }

    public void AddServerError(string consumer, string method, string path, Exception exception)
    {
        var error = new ServerErrorDetails
        {
            Consumer = consumer ?? string.Empty,
            Method = method,
            Path = path,
            Type = exception.GetType().Name,
            Message = exception.Message,
            StackTrace = exception.StackTrace ?? string.Empty,
        };

        string key = GetKey(error);
        _errorDetails.TryAdd(key, error);
        _errorCounts.AddOrUpdate(key, 1, (_, count) => count + 1);
    }

    public List<ServerErrors> GetAndResetServerErrors()
    {
        var data = _errorCounts
            .Where(entry => _errorDetails.TryGetValue(entry.Key, out _))
            .Select(entry =>
            {
                var error = _errorDetails[entry.Key];
                return new ServerErrors
                {
                    Consumer = error.Consumer,
                    Method = error.Method,
                    Path = error.Path,
                    Type = error.Type,
                    Message = TruncateMessage(error.Message),
                    StackTrace = TruncateStackTrace(error.StackTrace),
                    ErrorCount = entry.Value,
                };
            })
            .ToList();
        Clear();
        return data;
    }

    public void Clear()
    {
        _errorCounts.Clear();
        _errorDetails.Clear();
    }

    private string GetKey(ServerErrorDetails error)
    {
        string hashInput = string.Join(
            "|",
            error.Consumer,
            error.Method,
            error.Path,
            error.Type,
            error.Message,
            error.StackTrace
        );

        using var md5 = MD5.Create();
        byte[] hashBytes = md5.ComputeHash(Encoding.UTF8.GetBytes(hashInput));
        return Convert.ToHexString(hashBytes).ToLower();
    }

    public static string TruncateMessage(string message)
    {
        message = message.Trim();
        if (message.Length <= MaxMessageLength)
        {
            return message;
        }

        const string suffix = "... (truncated)";
        var cutoff = MaxMessageLength - suffix.Length;
        return message[..cutoff] + suffix;
    }

    public static string TruncateStackTrace(string stackTrace)
    {
        stackTrace = stackTrace.Trim();
        if (stackTrace.Length <= MaxStackTraceLength)
        {
            return stackTrace;
        }

        const string suffix = "... (truncated) ...";
        var cutoff = MaxStackTraceLength - suffix.Length;
        var lines = stackTrace.Split('\n');
        var truncatedLines = new List<string>();
        var length = 0;
        foreach (var line in lines)
        {
            if (length + line.Length + 1 > cutoff)
            {
                truncatedLines.Add(suffix);
                break;
            }
            truncatedLines.Add(line);
            length += line.Length + 1;
        }
        return string.Join('\n', truncatedLines);
    }
}
