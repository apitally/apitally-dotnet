namespace Apitally;

using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Apitally.Models;

public class ServerErrorCounter
{
    private readonly ConcurrentDictionary<string, int> _errorCounts = new();
    private readonly ConcurrentDictionary<string, ServerErrorDetails> _errorDetails = new();

    private class ServerErrorDetails
    {
        public string Consumer { get; set; } = string.Empty;
        public string Method { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string StackTraceString { get; set; } = string.Empty;
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
            StackTraceString = exception.StackTrace ?? string.Empty
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
                    Message = error.Message,
                    StackTraceString = error.StackTraceString,
                    ErrorCount = entry.Value
                };
            })
            .ToList();

        // Reset all counters
        _errorCounts.Clear();
        _errorDetails.Clear();

        return data;
    }

    private string GetKey(ServerErrorDetails error)
    {
        string hashInput = string.Join("|",
            error.Consumer,
            error.Method,
            error.Path,
            error.Type,
            error.Message,
            error.StackTraceString);

        using var md5 = MD5.Create();
        byte[] hashBytes = md5.ComputeHash(Encoding.UTF8.GetBytes(hashInput));
        return Convert.ToHexString(hashBytes).ToLower();
    }
}
