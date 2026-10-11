using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using NahidaTool.Models.Enum;

namespace NahidaTool.Models.Service;

public partial class ServerSwitchService
{
    private sealed record SwitchResources(BuildData Build, IReadOnlyList<FileChunkInfo> Files,
        IReadOnlyList<ChunkInfo> Chunks, long DownloadBytes);

    /// <summary>Compressed transfer bytes for the same resources used by switching.</summary>
    internal async Task<long> GetRequiredDownloadSizeAsync(ServerRegionType targetRegion, string version,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (await IsCacheValidAsync(targetRegion, version, cancellationToken).ConfigureAwait(false))
            return 0;
        var resources = await LoadSwitchResourcesAsync(targetRegion, version, cancellationToken).ConfigureAwait(false);
        return resources.DownloadBytes;
    }

    private async Task<SwitchResources> LoadSwitchResourcesAsync(ServerRegionType targetRegion, string version,
        CancellationToken cancellationToken)
    {
        if (!System.Enum.IsDefined(targetRegion))
            throw new ArgumentOutOfRangeException(nameof(targetRegion));
        if (!Version.TryParse(version, out var parsedVersion) || parsedVersion.Build < 0)
            throw new ArgumentException("Invalid installed client version.", nameof(version));

        // Preview and conversion may run concurrently. Region/cache state belongs
        // to this request, while ApiService still shares its HTTP connection pool.
        var api = new ApiService();
        api.SetRegion(targetRegion);
        var response = await api.GetBuildInfoAsync(version, cancellationToken).ConfigureAwait(false);
        if (response.RetCode != 0 || response.Data?.Manifests is not { Count: > 0 } ||
            !string.Equals(response.Data.Tag, version, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("No matching server-switch build was found.");

        var build = response.Data.Manifests.FirstOrDefault(m => m.MatchingField == "game" ||
            string.IsNullOrEmpty(m.MatchingField))
            ?? throw new InvalidDataException("Game resource manifest is missing.");
        if (string.IsNullOrWhiteSpace(build.ManifestDownload?.UrlPrefix) ||
            string.IsNullOrWhiteSpace(build.Manifest?.Id) ||
            string.IsNullOrWhiteSpace(build.ChunkDownload?.UrlPrefix))
            throw new InvalidDataException("Server-switch download metadata is incomplete.");

        byte[] compressed = await api.DownloadManifestAsync(build.ManifestDownload.UrlPrefix, build.Manifest.Id,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] decompressed = _manifestParser.DecompressZstd(compressed);
        var files = FilterFilesToDownload(_manifestParser.ParseChunkManifest(decompressed), targetRegion);
        cancellationToken.ThrowIfCancellationRequested();
        if (files.Count == 0)
            throw new InvalidDataException("No server-switch files were found.");

        var chunks = new Dictionary<string, ChunkInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            foreach (var chunk in file.Chunks ?? new List<ChunkInfo>())
            {
                if (string.IsNullOrWhiteSpace(chunk.ChunkId) || chunk.ChunkId is "." or ".." ||
                    chunk.ChunkId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || chunk.CompressedSize <= 0)
                    throw new InvalidDataException("Server-switch chunk metadata is invalid.");
                if (chunks.TryGetValue(chunk.ChunkId, out var previous) &&
                    (previous.CompressedSize != chunk.CompressedSize || previous.UncompressedSize != chunk.UncompressedSize ||
                     !string.Equals(previous.CheckSum, chunk.CheckSum, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("Conflicting metadata for a shared server-switch chunk.");
                chunks.TryAdd(chunk.ChunkId, chunk);
            }
        }
        if (chunks.Count == 0)
            throw new InvalidDataException("Server-switch resources have no downloadable chunks.");
        long downloadBytes = chunks.Values.Sum(chunk => chunk.CompressedSize);
        return new SwitchResources(build, files, chunks.Values.ToArray(), downloadBytes);
    }

    /// <summary>
    /// 从API下载服务器文件
    /// </summary>
    private async Task DownloadServerFilesAsync(ServerRegionType targetRegion, string version, string cacheDir,
        DispatcherQueue dispatcherQueue, CancellationToken cancellationToken)
    {
        NotifyStatus("正在从服务器获取资源信息...", dispatcherQueue);

        var resources = await LoadSwitchResourcesAsync(targetRegion, version, cancellationToken);
        var gameBuildData = resources.Build;
        var filesToDownload = resources.Files;
        NotifyDetail($"需要下载 {filesToDownload.Count} 个转服文件，压缩资源共 {resources.DownloadBytes} 字节", dispatcherQueue);

        // 创建缓存目录
        Directory.CreateDirectory(cacheDir);
        string chunksDir = Path.Combine(cacheDir, "chunks");
        Directory.CreateDirectory(chunksDir);

        // Fetch each compressed block once, including blocks shared by files.
        int downloadedChunks = 0;
        using var semaphore = new SemaphoreSlim(8);
        await Task.WhenAll(resources.Chunks.Select(chunk =>
            DownloadChunkAsync(chunk, gameBuildData.ChunkDownload!.UrlPrefix!, chunksDir, semaphore, () =>
            {
                int completed = Interlocked.Increment(ref downloadedChunks);
                NotifyProgress((double)completed / resources.Chunks.Count * 50, dispatcherQueue);
            }, cancellationToken)));

        var downloadedFiles = new List<string>();
        var fileChecksums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fileInfo in filesToDownload)
        {
            cancellationToken.ThrowIfCancellationRequested();
            NotifyDetail($"合并: {fileInfo.FilePath}", dispatcherQueue);

            // 合并文件
            string outputPath = Path.Combine(cacheDir, fileInfo.FilePath ?? string.Empty);
            string? outputDirPath = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDirPath))
            {
                Directory.CreateDirectory(outputDirPath);
            }

            await MergeFileAsync(fileInfo, chunksDir, outputPath);
            string? expectedChecksum = fileInfo.CheckSum;
            if (string.IsNullOrWhiteSpace(expectedChecksum))
                throw new InvalidDataException($"转服资源缺少校验值: {fileInfo.FilePath}");

            string actualChecksum = await CalculateFileChecksumAsync(outputPath, cancellationToken);
            if (!string.Equals(actualChecksum, expectedChecksum, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(outputPath);
                throw new InvalidDataException($"转服资源校验失败: {fileInfo.FilePath}");
            }

            string relativePath = fileInfo.FilePath ?? string.Empty;
            downloadedFiles.Add(relativePath);
            fileChecksums[relativePath] = actualChecksum;
        }

        // 保存缓存信息
        var cacheInfo = new ServerSwitchCacheInfo
        {
            Version = version,
            Region = targetRegion == ServerRegionType.CN ? "CN" : "OS",
            CachedTime = DateTime.Now,
            Files = downloadedFiles,
            FileChecksums = fileChecksums
        };

        string cacheInfoPath = Path.Combine(cacheDir, "cache_info.json");
        string json = JsonSerializer.Serialize(cacheInfo, AppJsonSerializerContext.Default.ServerSwitchCacheInfo);
        await File.WriteAllTextAsync(cacheInfoPath, json, cancellationToken);

        // 清理chunks目录
        try
        {
            if (Directory.Exists(chunksDir))
            {
                Directory.Delete(chunksDir, true);
            }
        }
        catch (Exception ex)
        {
            LogService.Debug($"清理chunks目录失败: {ex.Message}");
        }

        NotifyDetail("资源下载完成", dispatcherQueue);
    }

    /// <summary>
    /// 筛选需要下载的文件
    /// </summary>
    private static List<FileChunkInfo> FilterFilesToDownload(List<FileChunkInfo> allFiles, ServerRegionType targetRegion)
    {
        var result = new List<FileChunkInfo>();
        string dataFolder = targetRegion == ServerRegionType.CN ? CN_DATA_FOLDER : OS_DATA_FOLDER;
        string targetExe = targetRegion == ServerRegionType.CN ? CN_EXE : OS_EXE;

        // 优化: 预构建 Data 文件 HashSet，O(1) 查找替代 O(n*m) 嵌套循环
        var dataFileSet = new HashSet<string>(FilesToReplace.Length, StringComparer.OrdinalIgnoreCase);
        foreach (var df in FilesToReplace)
            dataFileSet.Add(Path.Combine(dataFolder, df));

        foreach (var file in allFiles)
        {
            if (string.IsNullOrEmpty(file.FilePath)) continue;

            string normalizedPath = file.FilePath.Replace("/", "\\");

            if (RootFileSet.Contains(normalizedPath)
                || dataFileSet.Contains(normalizedPath)
                || normalizedPath.Equals(targetExe, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(file);
            }
        }

        return result;
    }

    private async Task DownloadChunkAsync(ChunkInfo chunk, string urlPrefix, string chunksDir,
        SemaphoreSlim semaphore, Action onCompleted, CancellationToken cancellationToken)
    {
        if (chunk == null) return;

        await semaphore.WaitAsync(cancellationToken);
        try
        {

            string chunkPath = Path.Combine(chunksDir, chunk.ChunkId ?? string.Empty);

            // 检查是否已存在且有效
            if (File.Exists(chunkPath) && new FileInfo(chunkPath).Length == chunk.CompressedSize)
            {
                onCompleted?.Invoke();
                return;
            }

            await _apiService.DownloadChunkToFileAsync(urlPrefix, chunk.ChunkId ?? string.Empty, chunkPath, cancellationToken);
            onCompleted?.Invoke();
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task MergeFileAsync(FileChunkInfo fileInfo, string chunksDir, string outputPath)
    {
        using var outputStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        foreach (var chunk in fileInfo.Chunks ?? new List<ChunkInfo>())
        {
            string chunkPath = Path.Combine(chunksDir, chunk.ChunkId ?? string.Empty);
            if (File.Exists(chunkPath))
            {
                // 流式解压：直接从文件流解压到输出流，避免在内存中保留完整数据
                await using var chunkStream = new FileStream(chunkPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                await _manifestParser.DecompressZstdToStreamAsync(chunkStream, outputStream);
            }
        }
    }
}
