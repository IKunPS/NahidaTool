using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using NahidaTool.Models.Config;

namespace NahidaTool.Models;

public class Manifest
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
    [JsonPropertyName("checksum")]
    public string? CheckSum { get; set; }
    [JsonPropertyName("compressed_size")]
    public long CompressedSize { get; set; }
    [JsonPropertyName("uncompressed_size")]
    public long UncompressedSize { get; set; }
}

public class ChunkDownload
{
    [JsonPropertyName("encryption")]
    public int Encryption { get; set; }
    [JsonPropertyName("password")]
    public string? Password { get; set; }
    [JsonPropertyName("compression")]
    public int Compression { get; set; }
    [JsonPropertyName("url_prefix")]
    public string? UrlPrefix { get; set; }
    [JsonPropertyName("url_suffix")]
    public string? UrlSuffix { get; set; }
}

public class ManifestDownload
{
    [JsonPropertyName("encryption")]
    public int Encryption { get; set; }
    [JsonPropertyName("password")]
    public string? Password { get; set; }
    [JsonPropertyName("compression")]
    public int Compression { get; set; }
    [JsonPropertyName("url_prefix")]
    public string? UrlPrefix { get; set; }
    [JsonPropertyName("url_suffix")]
    public string? UrlSuffix { get; set; }
}

public class Stats
{
    [JsonPropertyName("compressed_size")]
    public long CompressedSize { get; set; }
    [JsonPropertyName("uncompressed_size")]
    public long UncompressedSize { get; set; }
    [JsonPropertyName("file_count")]
    public long FileCount { get; set; }
    [JsonPropertyName("chunk_count")]
    public long ChunkCount { get; set; }
}

public class DeduplicatedStats
{
    [JsonPropertyName("compressed_size")]
    public long CompressedSize { get; set; }
    [JsonPropertyName("uncompressed_size")]
    public long UncompressedSize { get; set; }
    [JsonPropertyName("file_count")]
    public long FileCount { get; set; }
    [JsonPropertyName("chunk_count")]
    public long ChunkCount { get; set; }
}

public class BuildData
{
    [JsonPropertyName("category_id")]
    public string? CategoryId { get; set; }
    [JsonPropertyName("category_name")]
    public string? CategoryName { get; set; }
    [JsonPropertyName("manifest")]
    public Manifest? Manifest { get; set; }
    [JsonPropertyName("chunk_download")]
    public ChunkDownload? ChunkDownload { get; set; }
    [JsonPropertyName("manifest_download")]
    public ManifestDownload? ManifestDownload { get; set; }
    [JsonPropertyName("matching_field")]
    public string? MatchingField { get; set; }
    [JsonPropertyName("stats")]
    public Stats? Stats { get; set; }
    [JsonPropertyName("deduplicated_stats")]
    public DeduplicatedStats? DeduplicatedStats { get; set; }
    [JsonPropertyName("tag")]
    public string? Tag { get; set; }
}

public class BuildResponse
{
    [JsonPropertyName("data")]
    public BuildResponseData? Data { get; set; }
}

public class BuildResponseData
{
    [JsonPropertyName("build_id")]
    public string? BuildId { get; set; }
    [JsonPropertyName("tag")]
    public string? Tag { get; set; }
    [JsonPropertyName("manifests")]
    public List<BuildData>? Manifests { get; set; }
    
    public BuildResponseData()
    {
        Manifests = new List<BuildData>();
    }
}

public class PatchBuildResponse
{
    [JsonPropertyName("retcode")]
    public int RetCode { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("data")]
    public PatchBuildResponseData? Data { get; set; }
}

public class PatchBuildResponseData
{
    [JsonPropertyName("build_id")]
    public string? BuildId { get; set; }

    [JsonPropertyName("patch_id")]
    public string? PatchId { get; set; }

    [JsonPropertyName("tag")]
    public string? Tag { get; set; }

    [JsonPropertyName("manifests")]
    public List<PatchBuildData>? Manifests { get; set; } = new();
}

public class PatchBuildData
{
    [JsonPropertyName("category_id")]
    public string? CategoryId { get; set; }

    [JsonPropertyName("category_name")]
    public string? CategoryName { get; set; }

    [JsonPropertyName("manifest")]
    public Manifest? Manifest { get; set; }

    [JsonPropertyName("diff_download")]
    public ChunkDownload? DiffDownload { get; set; }

    [JsonPropertyName("manifest_download")]
    public ManifestDownload? ManifestDownload { get; set; }

    [JsonPropertyName("matching_field")]
    public string? MatchingField { get; set; }

    [JsonPropertyName("stats")]
    public Dictionary<string, Stats>? Stats { get; set; }
}

public class ChunkInfo
{
    [JsonPropertyName("ChunkId")]
    public string? ChunkId { get; set; }
    [JsonPropertyName("CompressedSize")]
    public long CompressedSize { get; set; }
    [JsonPropertyName("UncompressedSize")]
    public long UncompressedSize { get; set; }
    [JsonPropertyName("CheckSum")]
    public string? CheckSum { get; set; }
    [JsonPropertyName("Url")]
    public string? Url { get; set; }
    /// <summary>
    /// Chunk 在目标文件中的偏移量（由 Manifest 解析时设置）
    /// </summary>
    [JsonPropertyName("Offset")]
    public long Offset { get; set; }
}

// ============ 分支API响应模型 ============

/// <summary>
/// 分支API响应
/// </summary>
public class BranchResponse
{
    [JsonPropertyName("retcode")]
    public int RetCode { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("data")]
    public BranchData? Data { get; set; }
}

/// <summary>
/// 分支数据
/// </summary>
public class BranchData
{
    [JsonPropertyName("game_branches")]
    public List<GameBranch>? GameBranches { get; set; }
}

/// <summary>
/// 游戏分支
/// </summary>
public class GameBranch
{
    [JsonPropertyName("game")]
    public GameInfo? Game { get; set; }

    [JsonPropertyName("main")]
    public BranchInfo? Main { get; set; }

    [JsonPropertyName("pre_download")]
    public BranchInfo? PreDownload { get; set; }

}

/// <summary>
/// 游戏信息
/// </summary>
public class GameInfo
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("biz")]
    public string? Biz { get; set; }
}

/// <summary>
/// 分支信息 (包含package_id和password)
/// </summary>
public class BranchInfo
{
    [JsonPropertyName("package_id")]
    public string? PackageId { get; set; }

    [JsonPropertyName("password")]
    public string? Password { get; set; }

    [JsonPropertyName("tag")]
    public string? Tag { get; set; }
}

public class FileChunkInfo
{
    [JsonPropertyName("FilePath")]
    public string? FilePath { get; set; }
    [JsonPropertyName("Chunks")]
    public List<ChunkInfo>? Chunks { get; set; }
    [JsonPropertyName("CheckSum")]
    public string? CheckSum { get; set; }

    public FileChunkInfo()
    {
        Chunks = new List<ChunkInfo>();
    }
}

// ============ APM配置模型 (用于转服功能) ============

/// <summary>
/// 游戏APM配置 (StreamingAssets/APMConfig.json)
/// </summary>
public class APMConfig
{
    [JsonPropertyName("app_id")]
    public string? AppId { get; set; }

    [JsonPropertyName("area")]
    public string? Area { get; set; }

    [JsonPropertyName("compile_type")]
    public string? CompileType { get; set; }

    [JsonPropertyName("app_version")]
    public string? AppVersion { get; set; }

    /// <summary>
    /// 从app_version提取版本号 (如 "CNRELWin6.3.0" -> "6.3.0")
    /// </summary>
    public string GetVersionNumber()
    {
        if (string.IsNullOrEmpty(AppVersion)) return string.Empty;
        // 提取版本号部分，格式为 "XXXRELWinX.X.X"
        var match = System.Text.RegularExpressions.Regex.Match(AppVersion, @"(\d+\.\d+\.\d+)");
        return match.Success ? match.Groups[1].Value : AppVersion;
    }

    /// <summary>
    /// 判断是否为国服
    /// </summary>
    public bool IsCN => string.Equals(Area, "cn", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 判断是否为国际服
    /// </summary>
    public bool IsOS => string.Equals(Area, "os", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(Area, "global", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 转服缓存版本信息
/// </summary>
public class ServerSwitchCacheInfo
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("region")]
    public string? Region { get; set; }

    [JsonPropertyName("cached_time")]
    public DateTime CachedTime { get; set; }

    [JsonPropertyName("files")]
    public List<string>? Files { get; set; }

    [JsonPropertyName("file_checksums")]
    public Dictionary<string, string>? FileChecksums { get; set; }
}

// ============ 私服状态模型 ============

/// <summary>
/// 私服 /status/server 接口响应
/// </summary>
public class ServerStatusResponse
{
    [JsonPropertyName("retcode")]
    public int RetCode { get; set; }

    [JsonPropertyName("status")]
    public ServerStatus? Status { get; set; }
}

/// <summary>
/// 私服状态信息
/// </summary>
public class ServerStatus
{
    [JsonPropertyName("playerCount")]
    public int PlayerCount { get; set; }

    [JsonPropertyName("maxPlayer")]
    public int MaxPlayer { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }
}

// JSON Source Generator - 解决 PublishTrimmed 导致的反序列化问题
[JsonSourceGenerationOptions(
    WriteIndented = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BuildResponse))]
[JsonSerializable(typeof(BuildResponseData))]
[JsonSerializable(typeof(BuildData))]
[JsonSerializable(typeof(Manifest))]
[JsonSerializable(typeof(ChunkDownload))]
[JsonSerializable(typeof(ManifestDownload))]
[JsonSerializable(typeof(Stats))]
[JsonSerializable(typeof(DeduplicatedStats))]
[JsonSerializable(typeof(PatchBuildResponse))]
[JsonSerializable(typeof(PatchBuildResponseData))]
[JsonSerializable(typeof(PatchBuildData))]
[JsonSerializable(typeof(List<PatchBuildData>))]
[JsonSerializable(typeof(Dictionary<string, Stats>))]
[JsonSerializable(typeof(ChunkInfo))]
[JsonSerializable(typeof(FileChunkInfo))]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(List<BuildData>))]
[JsonSerializable(typeof(List<ChunkInfo>))]
// 分支API相关类型
[JsonSerializable(typeof(BranchResponse))]
[JsonSerializable(typeof(BranchData))]
[JsonSerializable(typeof(GameBranch))]
[JsonSerializable(typeof(GameInfo))]
[JsonSerializable(typeof(BranchInfo))]
[JsonSerializable(typeof(List<GameBranch>))]
// 转服功能相关类型
[JsonSerializable(typeof(APMConfig))]
[JsonSerializable(typeof(ServerSwitchCacheInfo))]
// 私服状态相关类型
[JsonSerializable(typeof(ServerStatusResponse))]
[JsonSerializable(typeof(ServerStatus))]
[JsonSerializable(typeof(List<string>))]
public partial class AppJsonSerializerContext : JsonSerializerContext
{
}
