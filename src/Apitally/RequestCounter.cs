namespace Apitally;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Apitally.Models;

public class RequestCounter
{
    private readonly ConcurrentDictionary<string, int> _requestCounts = new();
    private readonly ConcurrentDictionary<string, long> _requestSizeSums = new();
    private readonly ConcurrentDictionary<string, long> _responseSizeSums = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, int>> _responseTimes =
        new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, int>> _requestSizes =
        new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, int>> _responseSizes =
        new();

    public void AddRequest(
        string consumer,
        string method,
        string path,
        int statusCode,
        long responseTime,
        long requestSize,
        long responseSize
    )
    {
        string key = string.Join("|", consumer, method.ToUpper(), path, statusCode.ToString());

        // Increment request count
        _requestCounts.AddOrUpdate(key, 1, (_, count) => count + 1);

        // Add response time (rounded to nearest 10ms)
        var responseTimeMap = _responseTimes.GetOrAdd(
            key,
            _ => new ConcurrentDictionary<int, int>()
        );
        int responseTimeMsBin = (int)(Math.Floor(responseTime / 10.0) * 10);
        responseTimeMap.AddOrUpdate(responseTimeMsBin, 1, (_, count) => count + 1);

        // Add request size (rounded down to nearest KB)
        if (requestSize >= 0)
        {
            _requestSizeSums.AddOrUpdate(key, requestSize, (_, sum) => sum + requestSize);
            var requestSizeMap = _requestSizes.GetOrAdd(
                key,
                _ => new ConcurrentDictionary<int, int>()
            );
            int requestSizeKbBin = (int)Math.Floor(requestSize / 1000.0);
            requestSizeMap.AddOrUpdate(requestSizeKbBin, 1, (_, count) => count + 1);
        }

        // Add response size (rounded down to nearest KB)
        if (responseSize >= 0)
        {
            _responseSizeSums.AddOrUpdate(key, responseSize, (_, sum) => sum + responseSize);
            var responseSizeMap = _responseSizes.GetOrAdd(
                key,
                _ => new ConcurrentDictionary<int, int>()
            );
            int responseSizeKbBin = (int)Math.Floor(responseSize / 1000.0);
            responseSizeMap.AddOrUpdate(responseSizeKbBin, 1, (_, count) => count + 1);
        }
    }

    public List<Requests> GetAndResetRequests()
    {
        var data = _requestCounts
            .Select(entry =>
            {
                string key = entry.Key;
                string[] keyParts = key.Split('|');

                _responseTimes.TryGetValue(key, out var responseTimeMap);
                _requestSizes.TryGetValue(key, out var requestSizeMap);
                _responseSizes.TryGetValue(key, out var responseSizeMap);

                return new Requests
                {
                    Consumer = string.IsNullOrEmpty(keyParts[0]) ? string.Empty : keyParts[0],
                    Method = keyParts[1],
                    Path = keyParts[2],
                    StatusCode = int.Parse(keyParts[3]),
                    RequestCount = entry.Value,
                    RequestSizeSum = _requestSizeSums.GetValueOrDefault(key),
                    ResponseSizeSum = _responseSizeSums.GetValueOrDefault(key),
                    ResponseTimes =
                        responseTimeMap?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value) ?? [],
                    RequestSizes =
                        requestSizeMap?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value) ?? [],
                    ResponseSizes =
                        responseSizeMap?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value) ?? [],
                };
            })
            .ToList();

        // Reset all counters
        _requestCounts.Clear();
        _requestSizeSums.Clear();
        _responseSizeSums.Clear();
        _responseTimes.Clear();
        _requestSizes.Clear();
        _responseSizes.Clear();

        return data;
    }
}
