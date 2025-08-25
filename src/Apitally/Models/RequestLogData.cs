namespace Apitally.Models;

using System;
using System.Linq;
using System.Text.Json.Serialization;

public record struct Header(string Name, string Value);

public class RequestResponseBase
{
    [JsonIgnore]
    public Header[] Headers { get; set; } = [];

    [JsonPropertyName("headers")]
    public string[][] HeadersForJson => [.. Headers.Select(h => new string[] { h.Name, h.Value })];

    [JsonPropertyName("size")]
    public long? Size { get; set; }

    [JsonIgnore]
    public byte[]? Body { get; set; }

    [JsonPropertyName("body")]
    public string? Base64EncodedBody => Body != null ? Convert.ToBase64String(Body) : null;
}

public class Request : RequestResponseBase
{
    [JsonPropertyName("timestamp")]
    public double Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    [JsonPropertyName("consumer")]
    public string? Consumer { get; set; }

    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
}

public class Response : RequestResponseBase
{
    [JsonPropertyName("status_code")]
    public int StatusCode { get; set; }

    [JsonPropertyName("response_time")]
    public double ResponseTime { get; set; }
}

public class ExceptionInfo
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("stacktrace")]
    public string StackTrace { get; set; } = string.Empty;
}

public class LogRecord
{
    [JsonPropertyName("timestamp")]
    public double Timestamp { get; set; }

    [JsonPropertyName("logger")]
    public string Logger { get; set; } = string.Empty;

    [JsonPropertyName("level")]
    public string Level { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("file")]
    public string File { get; set; } = string.Empty;

    [JsonPropertyName("line")]
    public int Line { get; set; }
}

class RequestLogItem
{
    [JsonPropertyName("uuid")]
    public string Uuid { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("request")]
    public Request Request { get; set; } = new();

    [JsonPropertyName("response")]
    public Response Response { get; set; } = new();

    [JsonPropertyName("exception")]
    public ExceptionInfo? Exception { get; set; }

    [JsonPropertyName("logs")]
    public List<LogRecord>? Logs { get; set; }
}
