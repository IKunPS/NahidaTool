using System.Text.Json.Serialization;

namespace NahidaTool.Models;

/// <summary>剧情服 /api/meta 的版本查询响应。</summary>
public sealed class KunServerMetadataResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("server")]
    public KunServerMetadata? Server { get; set; }
}

public sealed class KunServerMetadata
{
    // "version" is the website/SDK build, not the supported game client.
    [JsonPropertyName("gameversion")]
    public string? GameVersion { get; set; }
}
