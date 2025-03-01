namespace Apitally;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Apitally.Models;

class ValidationErrorCounter
{
    private readonly ConcurrentDictionary<string, int> _errorCounts = new();
    private readonly ConcurrentDictionary<string, ValidationErrorDetails> _errorDetails = new();

    private class ValidationErrorDetails
    {
        public string Consumer { get; set; } = string.Empty;
        public string Method { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string[] Location { get; set; } = [];
        public string Message { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
    }

    public void AddValidationError(
        string consumer,
        string method,
        string path,
        string[] location,
        string message,
        string type
    )
    {
        var error = new ValidationErrorDetails
        {
            Consumer = consumer ?? string.Empty,
            Method = method,
            Path = path,
            Location = location,
            Message = message.Trim(),
            Type = type,
        };

        string key = GetKey(error);
        _errorDetails.TryAdd(key, error);
        _errorCounts.AddOrUpdate(key, 1, (_, count) => count + 1);
    }

    public List<ValidationErrors> GetAndResetValidationErrors()
    {
        var data = _errorCounts
            .Where(entry => _errorDetails.TryGetValue(entry.Key, out _))
            .Select(entry =>
            {
                var error = _errorDetails[entry.Key];
                return new ValidationErrors
                {
                    Consumer = error.Consumer,
                    Method = error.Method,
                    Path = error.Path,
                    Location = error.Location,
                    Message = error.Message,
                    Type = error.Type,
                    ErrorCount = entry.Value,
                };
            })
            .ToList();

        // Reset all counters
        _errorCounts.Clear();
        _errorDetails.Clear();

        return data;
    }

    private string GetKey(ValidationErrorDetails error)
    {
        string hashInput = string.Join(
            "|",
            error.Consumer,
            error.Method.ToUpper(),
            error.Path,
            string.Join(".", error.Location),
            error.Message,
            error.Type
        );

        using var md5 = MD5.Create();
        byte[] hashBytes = md5.ComputeHash(Encoding.UTF8.GetBytes(hashInput));
        return Convert.ToHexString(hashBytes).ToLower();
    }
}
