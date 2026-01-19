namespace Apitally.Models;

using System.Text.Json.Serialization;

public class ActivityData
{
    [JsonPropertyName("span_id")]
    public string SpanId { get; set; } = string.Empty;

    [JsonPropertyName("parent_span_id")]
    public string? ParentSpanId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("start_time")]
    public long StartTime { get; set; }

    [JsonPropertyName("end_time")]
    public long EndTime { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("attributes")]
    public Dictionary<string, object?>? Attributes { get; set; }
}
