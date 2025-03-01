namespace Apitally.Models;

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

class Path
{
    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string PathValue { get; set; } = string.Empty;
}

class StartupData
{
    [JsonPropertyName("instance_uuid")]
    public Guid InstanceUuid { get; set; }

    [JsonPropertyName("message_uuid")]
    public Guid MessageUuid { get; set; } = Guid.NewGuid();

    [JsonPropertyName("paths")]
    public List<Path> Paths { get; set; } = new();

    [JsonPropertyName("versions")]
    public Dictionary<string, string> Versions { get; set; } = new();

    [JsonPropertyName("client")]
    public string Client { get; set; } = string.Empty;
}
