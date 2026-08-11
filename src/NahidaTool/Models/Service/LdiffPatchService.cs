using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using NahidaTool.Models.Config;
using NahidaTool.Models.Enum;
using SharpHDiffPatch.Core;

namespace NahidaTool.Models.Service;

public sealed class LdiffUpdateInfo
{
    public required string SourceVersion { get; init; }
    public required string TargetVersion { get; init; }
    public required string PatchId { get; init; }
    public required IReadOnlyList<PatchBuildData> Resources { get; init; }
    public long DownloadSize { get; init; }
}

public sealed class LdiffPrerequisiteException : InvalidOperationException
{
    public LdiffPrerequisiteException(string message) : base(message)
    {
    }

    public LdiffPrerequisiteException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed class LdiffPatchJournal
{
    public int SchemaVersion { get; set; } = 1;
    public string SourceVersion { get; set; } = string.Empty;
    public string TargetVersion { get; set; } = string.Empty;
    public string? CurrentAsset { get; set; }
    public HashSet<string> CompletedAssets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(LdiffPatchJournal))]
internal partial class LdiffPatchJournalJsonContext : JsonSerializerContext
{
}

public sealed class LdiffPatchService
{
    private const int BufferSize = 1024 * 1024;
    private readonly ApiService _apiService;

    public event Action<string>? StatusChanged;
    public event Action<double, long, long>? ProgressChanged;

    public LdiffPatchService(ApiService apiService)
    {
        _apiService = apiService;
    }

    public async Task<LdiffUpdateInfo?> GetAvailableUpdateAsync(
        string sourceVersion,
        VoiceLanguageType voices,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceVersion))
            throw new ArgumentException("缺少当前游戏版本", nameof(sourceVersion));

        PatchBuildResponse response = await _apiService.GetPatchBuildInfoAsync(ct);
        PatchBuildResponseData data = response.Data!;
        if (string.IsNullOrWhiteSpace(data.Tag) ||
            string.Equals(data.Tag, sourceVersion, StringComparison.OrdinalIgnoreCase))
            return null;

        var matchingFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ServerConfig.VoicePackages.Game,
        };
        foreach (VoiceLanguageType voice in System.Enum.GetValues<VoiceLanguageType>())
        {
            if (voice != VoiceLanguageType.None && voices.HasFlag(voice))
                matchingFields.Add(ServerConfig.VoicePackages.GetMatchingField(voice));
        }

        var resources = (data.Manifests ?? new List<PatchBuildData>())
            .Where(resource => !string.IsNullOrWhiteSpace(resource.MatchingField) &&
                               matchingFields.Contains(resource.MatchingField) &&
                               resource.Stats?.ContainsKey(sourceVersion) == true)
            .ToList();

        if (!resources.Any(resource => string.Equals(resource.MatchingField,
                ServerConfig.VoicePackages.Game, StringComparison.OrdinalIgnoreCase)))
            throw new LdiffPrerequisiteException(
                $"当前版本 {sourceVersion} 没有可用的官方增量补丁，请先通过官方启动器更新或修复游戏。");

        long downloadSize = resources.Sum(resource => resource.Stats![sourceVersion].CompressedSize);
        return new LdiffUpdateInfo
        {
            SourceVersion = sourceVersion,
            TargetVersion = data.Tag,
            PatchId = data.PatchId ?? data.BuildId ?? data.Tag,
            Resources = resources,
            DownloadSize = downloadSize,
        };
    }

    public async Task ApplyUpdateAsync(
        LdiffUpdateInfo update,
        string gameRoot,
        string? existingLdiffFolder = null,
        CancellationToken ct = default,
        bool requireOfficialPackage = false)
    {
        string normalizedGameRoot = NormalizeRoot(gameRoot);
        string workRoot = Path.Combine(normalizedGameRoot, ".nahida", "ldiff", SafeFileName(update.PatchId));
        string backupRoot = Path.Combine(workRoot, "backup");
        Directory.CreateDirectory(workRoot);
        Directory.CreateDirectory(backupRoot);

        if (requireOfficialPackage && !ContainsAnyFile(existingLdiffFolder))
            throw new LdiffPrerequisiteException(
                "未找到官方启动器下载的增量包，请先在官方启动器中完成预下载。");

        LdiffPatchJournal journal = LoadJournal(workRoot, update);

        long completedBytes = 0;
        long totalBytes = Math.Max(1, update.DownloadSize);
        bool completedSuccessfully = false;
        var resourceWorkspaces = new List<(PatchBuildData Resource, SophonLdiffManifest Manifest,
            string ChunkRoot, IReadOnlyDictionary<string, string> ChunkPaths)>();

        try
        {
            foreach (PatchBuildData resource in update.Resources)
            {
                ct.ThrowIfCancellationRequested();
                string name = resource.MatchingField ?? "unknown";
                SetStatus($"正在读取 {name} LDiff manifest...");

                byte[] compressedManifest = await DownloadManifestAsync(resource, ct);
                byte[] manifestBytes = new ManifestParser().DecompressZstd(compressedManifest);
                VerifyBytes(manifestBytes, resource.Manifest?.UncompressedSize ?? 0,
                    resource.Manifest?.CheckSum, $"{name} manifest");

                SophonLdiffManifest manifest = SophonLdiffManifest.ParseFrom(manifestBytes);
                var selectedDiffs = SelectDiffs(manifest, update.SourceVersion).ToList();
                if (selectedDiffs.Count == 0)
                    throw new LdiffPrerequisiteException(
                        $"官方增量包不包含从 {update.SourceVersion} 更新所需的 {name} 补丁，请先在官方启动器中完成预下载。");
                string chunkRoot = Path.Combine(workRoot, SafeFileName(name));
                Directory.CreateDirectory(chunkRoot);
                var chunkPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (LdiffAssetData diff in selectedDiffs
                             .Select(item => item.Diff)
                             .GroupBy(item => item.ChunkName, StringComparer.OrdinalIgnoreCase)
                             .Select(group => group.First()))
                {
                    ct.ThrowIfCancellationRequested();
                    string chunkPath = await EnsureChunkAsync(resource, diff, chunkRoot,
                        existingLdiffFolder, (delta) =>
                        {
                            long current = Interlocked.Add(ref completedBytes, delta);
                            ReportProgress(current, totalBytes);
                        }, ct, requireOfficialPackage);
                    chunkPaths[diff.ChunkName] = chunkPath;
                }

                resourceWorkspaces.Add((resource, manifest, chunkRoot, chunkPaths));
            }

            int totalAssets = resourceWorkspaces.Sum(workspace =>
                SelectDiffs(workspace.Manifest, update.SourceVersion).Count());
            int currentAsset = 0;
            foreach (var workspace in resourceWorkspaces)
            {
                foreach (var selected in SelectDiffs(workspace.Manifest, update.SourceVersion))
                {
                    ct.ThrowIfCancellationRequested();
                    currentAsset++;
                    SetStatus($"正在应用 LDiff ({currentAsset}/{totalAssets}): {selected.Asset.AssetName}");

                    if (!workspace.ChunkPaths.TryGetValue(selected.Diff.ChunkName, out string? chunkPath))
                        throw new FileNotFoundException($"缺少 LDiff 文件: {selected.Diff.ChunkName}");

                    bool isRecovery = journal.CompletedAssets.Contains(selected.Asset.AssetName) ||
                                      string.Equals(journal.CurrentAsset, selected.Asset.AssetName,
                                          StringComparison.OrdinalIgnoreCase) ||
                                      HasAssetBackup(backupRoot, selected.Asset, selected.Diff);
                    journal.CurrentAsset = selected.Asset.AssetName;
                    SaveJournal(workRoot, journal);

                    bool repaired = await ApplyAssetAsync(normalizedGameRoot, workspace.ChunkRoot,
                        backupRoot, selected.Asset, selected.Diff, chunkPath, isRecovery, ct);
                    if (repaired && isRecovery)
                        SetStatus($"已清理异常文件并重新修补: {selected.Asset.AssetName}");

                    journal.CompletedAssets.Add(selected.Asset.AssetName);
                    journal.CurrentAsset = null;
                    SaveJournal(workRoot, journal);

                    double patchProgress = totalAssets == 0 ? 1 : (double)currentAsset / totalAssets;
                    ProgressChanged?.Invoke(patchProgress, 0, 0);
                }
            }

            UpdateGameVersion(normalizedGameRoot, update.TargetVersion);
            SetStatus($"LDiff 更新完成：{update.TargetVersion}");
            ProgressChanged?.Invoke(1, 0, 0);
            completedSuccessfully = true;
        }
        finally
        {
            if (completedSuccessfully)
                TryDeleteOwnedDirectory(workRoot);
        }
    }

    private async Task<byte[]> DownloadManifestAsync(PatchBuildData resource, CancellationToken ct)
    {
        string prefix = resource.ManifestDownload?.UrlPrefix ??
                        throw new InvalidDataException("LDiff manifest 缺少下载地址");
        string id = resource.Manifest?.Id ?? throw new InvalidDataException("LDiff manifest 缺少 ID");
        string suffix = resource.ManifestDownload?.UrlSuffix ?? string.Empty;
        string url = $"{prefix.TrimEnd('/')}/{Uri.EscapeDataString(id)}{suffix}";

        using HttpClient client = _apiService.CreateDownloadClient(TimeSpan.FromMinutes(10));
        using HttpResponseMessage response = await client.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    private async Task<string> EnsureChunkAsync(
        PatchBuildData resource,
        LdiffAssetData diff,
        string chunkRoot,
        string? existingLdiffFolder,
        Action<long> progress,
        CancellationToken ct,
        bool requireOfficialPackage)
    {
        ValidateChunkName(diff.ChunkName);

        if (requireOfficialPackage)
        {
            string? officialChunk = string.IsNullOrWhiteSpace(existingLdiffFolder)
                ? null
                : Path.Combine(existingLdiffFolder, diff.ChunkName);
            if (officialChunk == null ||
                !await IsFileValidAsync(officialChunk, diff.ChunkSize, diff.ChunkHashMd5, ct))
                throw new LdiffPrerequisiteException(
                    $"官方增量包缺少或损坏: {diff.ChunkName}。请先在官方启动器中完成预下载。");

            progress(diff.ChunkSize);
            return officialChunk;
        }

        string? existing = FindExistingChunk(diff, chunkRoot, existingLdiffFolder);
        if (existing != null && await IsFileValidAsync(existing, diff.ChunkSize, diff.ChunkHashMd5, ct))
        {
            progress(diff.ChunkSize);
            return existing;
        }

        string destination = Path.Combine(chunkRoot, diff.ChunkName);
        string prefix = resource.DiffDownload?.UrlPrefix ??
                        throw new InvalidDataException("LDiff 资源缺少下载地址");
        string suffix = resource.DiffDownload?.UrlSuffix ?? string.Empty;
        string url = $"{prefix.TrimEnd('/')}/{Uri.EscapeDataString(diff.ChunkName)}{suffix}";

        SetStatus($"正在下载 LDiff: {diff.ChunkName}");
        await DownloadFileResumableAsync(url, destination, diff.ChunkSize, progress, ct);
        if (!await IsFileValidAsync(destination, diff.ChunkSize, diff.ChunkHashMd5, ct))
        {
            TryDeleteFile(destination);
            throw new InvalidDataException($"LDiff 校验失败: {diff.ChunkName}");
        }
        return destination;
    }

    private async Task DownloadFileResumableAsync(
        string url,
        string destination,
        long expectedSize,
        Action<long> progress,
        CancellationToken ct)
    {
        long existingLength = File.Exists(destination) ? new FileInfo(destination).Length : 0;
        if (existingLength > expectedSize)
        {
            TryDeleteFile(destination);
            existingLength = 0;
        }

        using HttpClient client = _apiService.CreateDownloadClient(TimeSpan.FromHours(4));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existingLength > 0)
            request.Headers.Range = new RangeHeaderValue(existingLength, null);

        using HttpResponseMessage response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
        bool append = existingLength > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!append)
            existingLength = 0;
        response.EnsureSuccessStatusCode();

        if (existingLength > 0)
            progress(existingLength);
        await using Stream input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(destination, append ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] buffer = new byte[BufferSize];
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            progress(read);
        }
        await output.FlushAsync(ct);
    }

    private static IEnumerable<(LdiffAssetProperty Asset, LdiffAssetData Diff)> SelectDiffs(
        SophonLdiffManifest manifest,
        string sourceVersion)
    {
        foreach (LdiffAssetProperty asset in manifest.Assets)
        {
            LdiffAssetEntry? selected = asset.AssetLdiffs.FirstOrDefault(candidate =>
                string.Equals(candidate.LatestDiffVersion, sourceVersion, StringComparison.OrdinalIgnoreCase));
            if (selected?.DiffData != null && !string.IsNullOrWhiteSpace(selected.DiffData.ChunkName))
                yield return (asset, selected.DiffData);
        }
    }

    private async Task<bool> ApplyAssetAsync(
        string gameRoot,
        string workRoot,
        string backupRoot,
        LdiffAssetProperty asset,
        LdiffAssetData diff,
        string chunkPath,
        bool isRecovery,
        CancellationToken ct)
    {
        string targetPath = ResolveUnderRoot(gameRoot, asset.AssetName);
        if (await IsFileValidAsync(targetPath, asset.AssetSize, asset.AssetHashMd5, ct))
            return false;

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        string targetBackupPath = ResolveUnderRoot(backupRoot, asset.AssetName);
        string temporaryOutput = targetPath + $".nahida-{Guid.NewGuid():N}.tmp";
        string? temporaryHdiff = null;

        try
        {
            if (string.IsNullOrWhiteSpace(diff.SourcePath))
            {
                if (diff.HdiffSize != asset.AssetSize)
                    throw new InvalidDataException($"新增文件分片大小不匹配: {asset.AssetName}");
                await CopySliceAsync(chunkPath, temporaryOutput, diff.HdiffInChunkOffset, diff.HdiffSize, ct);
            }
            else
            {
                string sourcePath = ResolveUnderRoot(gameRoot, diff.SourcePath);
                string sourceBackupPath = ResolveUnderRoot(backupRoot, diff.SourcePath);
                bool sourceIsTarget = string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase);
                string patchSource;

                if (sourceIsTarget)
                {
                    patchSource = await PrepareInPlaceSourceAsync(targetPath, sourceBackupPath,
                        diff.SourceSize, diff.SourceHashMd5, asset.AssetName, ct);
                }
                else
                {
                    patchSource = await FindValidPatchSourceAsync(sourcePath, sourceBackupPath,
                        diff.SourceSize, diff.SourceHashMd5, diff.SourcePath, ct);
                }

                temporaryHdiff = Path.Combine(workRoot, $"{Guid.NewGuid():N}.hdiff");
                await CopySliceAsync(chunkPath, temporaryHdiff, diff.HdiffInChunkOffset, diff.HdiffSize, ct);
                ct.ThrowIfCancellationRequested();

                var patcher = new HDiffPatch();
                patcher.Initialize(temporaryHdiff);
                patcher.Patch(patchSource, temporaryOutput, true, ct, false, true);
            }

            await VerifyFileAsync(temporaryOutput, asset.AssetSize, asset.AssetHashMd5, ct);
            ct.ThrowIfCancellationRequested();
            CommitPatchedFile(temporaryOutput, targetPath, targetBackupPath,
                asset.AssetName, isRecovery);
            return true;
        }
        finally
        {
            TryDeleteFile(temporaryOutput);
            if (temporaryHdiff != null)
                TryDeleteFile(temporaryHdiff);
        }
    }

    private async Task<string> PrepareInPlaceSourceAsync(
        string targetPath,
        string backupPath,
        long expectedSize,
        string? expectedMd5,
        string assetName,
        CancellationToken ct)
    {
        if (File.Exists(backupPath))
        {
            if (await IsFileValidAsync(backupPath, expectedSize, expectedMd5, ct))
                return backupPath;

            if (await IsFileValidAsync(targetPath, expectedSize, expectedMd5, ct))
            {
                SetStatus($"正在使用官方启动器修复后的原文件: {assetName}");
                File.Delete(backupPath);
                return targetPath;
            }

            throw new LdiffPrerequisiteException(
                $"原始文件及其修补备份均已损坏: {assetName}。请先使用官方启动器修复游戏并重新下载增量包。");
        }

        try
        {
            await VerifyFileAsync(targetPath, expectedSize, expectedMd5, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException)
        {
            throw new LdiffPrerequisiteException(
                $"待修补的原始文件缺失或损坏: {assetName}。请先使用官方启动器修复游戏并重新下载增量包。", ex);
        }

        return targetPath;
    }

    private static async Task<string> FindValidPatchSourceAsync(
        string sourcePath,
        string sourceBackupPath,
        long expectedSize,
        string? expectedMd5,
        string sourceName,
        CancellationToken ct)
    {
        if (await IsFileValidAsync(sourceBackupPath, expectedSize, expectedMd5, ct))
            return sourceBackupPath;
        if (await IsFileValidAsync(sourcePath, expectedSize, expectedMd5, ct))
            return sourcePath;

        throw new LdiffPrerequisiteException(
            $"待修补的原始文件缺失或损坏: {sourceName}。请先使用官方启动器修复游戏并重新下载增量包。");
    }

    private void CommitPatchedFile(
        string patchedFile,
        string targetPath,
        string backupPath,
        string assetName,
        bool isRecovery)
    {
        if (File.Exists(targetPath) && !File.Exists(backupPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            File.Replace(patchedFile, targetPath, backupPath, true);
            return;
        }

        if (File.Exists(targetPath))
        {
            if (isRecovery)
                SetStatus($"检测到上次修补文件异常，正在清理: {assetName}");
            File.Delete(targetPath);
        }
        File.Move(patchedFile, targetPath);
    }

    private static async Task CopySliceAsync(
        string source,
        string destination,
        long offset,
        long length,
        CancellationToken ct)
    {
        if (offset < 0 || length < 0)
            throw new InvalidDataException("LDiff 分片范围无效");

        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (offset > input.Length || length > input.Length - offset)
            throw new InvalidDataException($"LDiff 分片越界: {Path.GetFileName(source)}");
        input.Position = offset;

        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] buffer = new byte[BufferSize];
        long remaining = length;
        while (remaining > 0)
        {
            int read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
            if (read == 0)
                throw new EndOfStreamException($"LDiff 分片提前结束: {Path.GetFileName(source)}");
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            remaining -= read;
        }
        await output.FlushAsync(ct);
    }

    private static string? FindExistingChunk(
        LdiffAssetData diff,
        string chunkRoot,
        string? existingLdiffFolder)
    {
        ValidateChunkName(diff.ChunkName);
        if (!string.IsNullOrWhiteSpace(existingLdiffFolder))
        {
            string candidate = Path.Combine(existingLdiffFolder, diff.ChunkName);
            if (File.Exists(candidate))
                return candidate;
        }

        string owned = Path.Combine(chunkRoot, diff.ChunkName);
        return File.Exists(owned) ? owned : null;
    }

    private static async Task<bool> IsFileValidAsync(
        string path,
        long expectedSize,
        string? expectedMd5,
        CancellationToken ct)
    {
        try
        {
            await VerifyFileAsync(path, expectedSize, expectedMd5, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static async Task VerifyFileAsync(
        string path,
        long expectedSize,
        string? expectedMd5,
        CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException($"文件不存在: {path}", path);
        if (expectedSize > 0 && info.Length != expectedSize)
            throw new InvalidDataException($"文件大小不匹配: {path}");
        if (string.IsNullOrWhiteSpace(expectedMd5))
            return;

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using MD5 md5 = MD5.Create();
        byte[] hash = await md5.ComputeHashAsync(stream, ct);
        string actual = Convert.ToHexString(hash).ToLowerInvariant();
        if (!string.Equals(actual, expectedMd5, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"文件 MD5 不匹配: {path}");
    }

    private static void VerifyBytes(byte[] data, long expectedSize, string? expectedMd5, string name)
    {
        if (expectedSize > 0 && data.LongLength != expectedSize)
            throw new InvalidDataException($"{name} 解压后大小不匹配");
        if (string.IsNullOrWhiteSpace(expectedMd5))
            return;

        string actual = Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant();
        if (!string.Equals(actual, expectedMd5, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{name} MD5 不匹配");
    }

    public static bool HasOfficialIncrementalPackage(string gameRoot)
    {
        if (string.IsNullOrWhiteSpace(gameRoot))
            return false;
        return ContainsAnyFile(Path.Combine(gameRoot, "ldiff"));
    }

    private static bool ContainsAnyFile(string? folder)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(folder) &&
                   Directory.Exists(folder) &&
                   Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly).Any();
        }
        catch
        {
            return false;
        }
    }

    private static bool HasAssetBackup(
        string backupRoot,
        LdiffAssetProperty asset,
        LdiffAssetData diff)
    {
        if (File.Exists(ResolveUnderRoot(backupRoot, asset.AssetName)))
            return true;
        return !string.IsNullOrWhiteSpace(diff.SourcePath) &&
               File.Exists(ResolveUnderRoot(backupRoot, diff.SourcePath));
    }

    private static LdiffPatchJournal LoadJournal(string workRoot, LdiffUpdateInfo update)
    {
        string path = Path.Combine(workRoot, "patch-state.json");
        try
        {
            if (File.Exists(path))
            {
                LdiffPatchJournal? existing = JsonSerializer.Deserialize(
                    File.ReadAllText(path), LdiffPatchJournalJsonContext.Default.LdiffPatchJournal);
                if (existing?.SchemaVersion == 1 &&
                    string.Equals(existing.SourceVersion, update.SourceVersion,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.TargetVersion, update.TargetVersion,
                        StringComparison.OrdinalIgnoreCase))
                {
                    existing.CompletedAssets = new HashSet<string>(
                        existing.CompletedAssets ?? Enumerable.Empty<string>(),
                        StringComparer.OrdinalIgnoreCase);
                    return existing;
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"读取 LDiff 修补记录失败，将根据备份恢复: {ex.Message}");
        }

        return new LdiffPatchJournal
        {
            SourceVersion = update.SourceVersion,
            TargetVersion = update.TargetVersion,
        };
    }

    private static void SaveJournal(string workRoot, LdiffPatchJournal journal)
    {
        string path = Path.Combine(workRoot, "patch-state.json");
        string temporaryPath = path + ".tmp";
        string json = JsonSerializer.Serialize(
            journal, LdiffPatchJournalJsonContext.Default.LdiffPatchJournal);
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, path, true);
    }

    private static string NormalizeRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            throw new DirectoryNotFoundException($"游戏目录不存在: {root}");
        return Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string ResolveUnderRoot(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidDataException($"manifest 包含无效路径: {relativePath}");

        string normalizedRelative = relativePath.Replace('/', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);
        string fullPath = Path.GetFullPath(Path.Combine(root, normalizedRelative));
        string prefix = root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"manifest 路径越界: {relativePath}");
        return fullPath;
    }

    private static void ValidateChunkName(string chunkName)
    {
        if (string.IsNullOrWhiteSpace(chunkName) ||
            !string.Equals(Path.GetFileName(chunkName), chunkName, StringComparison.Ordinal))
            throw new InvalidDataException($"无效的 LDiff 文件名: {chunkName}");
    }

    private static string SafeFileName(string value)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(value) ? "unknown" : value;
    }

    private static void UpdateGameVersion(string gameRoot, string targetVersion)
    {
        string configPath = Path.Combine(gameRoot, "config.ini");
        if (!File.Exists(configPath))
            return;

        string[] lines = File.ReadAllLines(configPath);
        bool replaced = false;
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("game_version=", StringComparison.OrdinalIgnoreCase))
                continue;
            lines[i] = $"game_version={targetVersion}";
            replaced = true;
            break;
        }

        if (!replaced)
            lines = lines.Append($"game_version={targetVersion}").ToArray();
        File.WriteAllLines(configPath, lines);
    }

    private void SetStatus(string status)
    {
        LogService.Info(status);
        StatusChanged?.Invoke(status);
    }

    private void ReportProgress(long current, long total)
    {
        ProgressChanged?.Invoke(Math.Clamp((double)current / total, 0, 1), current, total);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static void TryDeleteOwnedDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch
        {
        }
    }
}
