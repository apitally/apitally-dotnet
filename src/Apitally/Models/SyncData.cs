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
    public Dictionary<int, int> ResponseTimes { get; set; } = [];

    [JsonPropertyName("request_sizes")]
    public Dictionary<int, int> RequestSizes { get; set; } = [];

    [JsonPropertyName("response_sizes")]
    public Dictionary<int, int> ResponseSizes { get; set; } = [];
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
    [JsonPropertyName("identifier")]
    public string Identifier { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("group")]
    public string? Group { get; set; }
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
    public List<Requests> Requests { get; set; } = [];

    [JsonPropertyName("validation_errors")]
    public List<ValidationErrors> ValidationErrors { get; set; } = [];

    [JsonPropertyName("server_errors")]
    public List<ServerErrors> ServerErrors { get; set; } = [];

    [JsonPropertyName("consumers")]
    public List<Consumer> Consumers { get; set; } = [];

    [JsonIgnore]
    public double AgeInSeconds => DateTimeOffset.UtcNow.ToUnixTimeSeconds() - Timestamp;
}
