using System.Text.Json.Serialization;

namespace NahidaTool.Models;

/// <summary>Kun 统一入口 /api/meta 的版本查询响应。</summary>
public sealed class KunServerMetadataResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("server")]
    public KunServerMetadata? Server { get; set; }
}

public sealed class KunServerMetadata
{
    // "version" 是网站/SDK 构建号；gameversion 可列出多个版本，如 7.1/7.0。
    [JsonPropertyName("gameversion")]
    public string? GameVersion { get; set; }
}
