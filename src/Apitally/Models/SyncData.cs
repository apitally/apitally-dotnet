namespace Apitally.Models;

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

public class Requests
{
    [JsonPropertyName("consumer")]
    public string Consumer { get; set; } = string.Empty;

    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("status_code")]
    public int StatusCode { get; set; }

    [JsonPropertyName("request_count")]
    public int RequestCount { get; set; }

    [JsonPropertyName("request_size_sum")]
    public long RequestSizeSum { get; set; }

    [JsonPropertyName("response_size_sum")]
    public long ResponseSizeSum { get; set; }

    [JsonPropertyName("response_times")]
    public Dictionary<int, int> ResponseTimes { get; set; } = new();

    [JsonPropertyName("request_sizes")]
    public Dictionary<int, int> RequestSizes { get; set; } = new();

    [JsonPropertyName("response_sizes")]
    public Dictionary<int, int> ResponseSizes { get; set; } = new();
}

public class ValidationErrors
{
    [JsonPropertyName("consumer")]
    public string Consumer { get; set; } = string.Empty;

    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("loc")]
    public string[] Location { get; set; } = [];

    [JsonPropertyName("msg")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("error_count")]
    public int ErrorCount { get; set; }
}

public class ServerErrors
{
    [JsonPropertyName("consumer")]
    public string Consumer { get; set; } = string.Empty;

    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("msg")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("traceback")]
    public string StackTraceString { get; set; } = string.Empty;

    [JsonPropertyName("error_count")]
    public int ErrorCount { get; set; }
}

public class Consumer
{
    private string _identifier = string.Empty;
    private string? _name;
    private string? _group;

    [JsonPropertyName("identifier")]
    public string Identifier
    {
        get => _identifier;
        set => _identifier = TruncateString(value, 128) ?? string.Empty;
    }

    [JsonPropertyName("name")]
    public string? Name
    {
        get => _name;
        set => _name = TruncateString(value, 64);
    }

    [JsonPropertyName("group")]
    public string? Group
    {
        get => _group;
        set => _group = TruncateString(value, 64);
    }

    private static string? TruncateString(string? value, int maxLength) =>
        value?.Trim()?.Substring(0, Math.Min(value.Trim().Length, maxLength));
}

public class SyncData
{
    [JsonPropertyName("timestamp")]
    public double Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    [JsonPropertyName("instance_uuid")]
    public Guid InstanceUuid { get; set; }

    [JsonPropertyName("message_uuid")]
    public Guid MessageUuid { get; set; } = Guid.NewGuid();

    [JsonPropertyName("requests")]
    public List<Requests> Requests { get; set; } = new();

    [JsonPropertyName("validation_errors")]
    public List<ValidationErrors> ValidationErrors { get; set; } = new();

    [JsonPropertyName("server_errors")]
    public List<ServerErrors> ServerErrors { get; set; } = new();

    [JsonPropertyName("consumers")]
    public List<Consumer> Consumers { get; set; } = new();

    [JsonIgnore]
    public double AgeInSeconds => DateTimeOffset.UtcNow.ToUnixTimeSeconds() - Timestamp;
}
