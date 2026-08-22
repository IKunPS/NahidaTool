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
    public int SchemaVersion { get; set; } = 2;
    public string SourceVersion { get; set; } = string.Empty;
    public string TargetVersion { get; set; } = string.Empty;
    public string? CurrentAsset { get; set; }
    public string? CurrentDeletion { get; set; }
    public HashSet<string> CompletedAssets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> CompletedDeletions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool AllOperationsApplied { get; set; }
    public bool Completed { get; set; }
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

        var availableFields = resources
            .Select(resource => resource.MatchingField!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] missingFields = matchingFields.Where(field => !availableFields.Contains(field)).ToArray();
        if (missingFields.Length > 0)
            throw new LdiffPrerequisiteException(
                $"当前版本 {sourceVersion} 缺少 {string.Join(", ", missingFields)} 的官方增量补丁，" +
                "请先通过官方启动器更新或修复游戏。");

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
        string deletionBackupRoot = Path.Combine(workRoot, "deleted-backup");
        Directory.CreateDirectory(workRoot);
        Directory.CreateDirectory(backupRoot);
        Directory.CreateDirectory(deletionBackupRoot);

        if (requireOfficialPackage && !ContainsAnyFile(existingLdiffFolder))
            throw new LdiffPrerequisiteException(
                "未找到官方启动器下载的增量包，请先在官方启动器中完成预下载。");

        LdiffPatchJournal journal = LoadJournal(workRoot, update);

        long completedBytes = 0;
        long totalBytes = Math.Max(1, update.DownloadSize);
        bool completedSuccessfully = false;
        var resourceWorkspaces = new List<(PatchBuildData Resource, global::SophonPatchManifest Manifest,
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

                global::SophonPatchManifest manifest = global::SophonPatchManifest.Parser.ParseFrom(manifestBytes);
                var selectedDiffs = SelectDiffs(manifest, update.SourceVersion).ToList();
                var selectedDeletions = SelectDeletions(manifest, update.SourceVersion).ToList();
                if (selectedDiffs.Count == 0 && selectedDeletions.Count == 0)
                    throw new LdiffPrerequisiteException(
                        $"官方增量包不包含从 {update.SourceVersion} 更新所需的 {name} 补丁，请先在官方启动器中完成预下载。");
                string chunkRoot = Path.Combine(workRoot, SafeFileName(name));
                Directory.CreateDirectory(chunkRoot);
                var chunkPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (global::SophonPatch diff in selectedDiffs
                             .Select(item => item.Diff)
                             .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                             .Select(group => group.First()))
                {
                    ct.ThrowIfCancellationRequested();
                    string chunkPath = await EnsureChunkAsync(resource, diff, chunkRoot,
                        existingLdiffFolder, (delta) =>
                        {
                            long current = Interlocked.Add(ref completedBytes, delta);
                            ReportPreparationProgress(current, totalBytes);
                        }, ct, requireOfficialPackage);
                    chunkPaths[diff.Id] = chunkPath;
                }

                resourceWorkspaces.Add((resource, manifest, chunkRoot, chunkPaths));
            }

            int totalOperations = resourceWorkspaces.Sum(workspace =>
                SelectDiffs(workspace.Manifest, update.SourceVersion).Count() +
                SelectDeletions(workspace.Manifest, update.SourceVersion).Count());
            int currentOperation = 0;
            foreach (var workspace in resourceWorkspaces)
            {
                foreach (var selected in SelectDiffs(workspace.Manifest, update.SourceVersion))
                {
                    ct.ThrowIfCancellationRequested();
                    currentOperation++;
                    SetStatus($"正在应用 LDiff ({currentOperation}/{totalOperations}): {selected.Asset.File}");

                    if (!workspace.ChunkPaths.TryGetValue(selected.Diff.Id, out string? chunkPath))
                        throw new FileNotFoundException($"缺少 LDiff 文件: {selected.Diff.Id}");

                    bool isRecovery = journal.CompletedAssets.Contains(selected.Asset.File) ||
                                      string.Equals(journal.CurrentAsset, selected.Asset.File,
                                          StringComparison.OrdinalIgnoreCase) ||
                                      HasAssetBackup(backupRoot, selected.Asset, selected.Diff);
                    journal.CurrentAsset = selected.Asset.File;
                    SaveJournal(workRoot, journal);

                    bool repaired = await ApplyAssetAsync(normalizedGameRoot, workspace.ChunkRoot,
                        backupRoot, selected.Asset, selected.Diff, chunkPath, isRecovery, ct);
                    if (repaired && isRecovery)
                        SetStatus($"已清理异常文件并重新修补: {selected.Asset.File}");

                    journal.CompletedAssets.Add(selected.Asset.File);
                    journal.CurrentAsset = null;
                    SaveJournal(workRoot, journal);

                    ReportApplyProgress(currentOperation, totalOperations);
                }
            }

            foreach (var workspace in resourceWorkspaces)
            {
                foreach (global::SophonPatchDeleteFile deletion in
                         SelectDeletions(workspace.Manifest, update.SourceVersion))
                {
                    ct.ThrowIfCancellationRequested();
                    currentOperation++;
                    SetStatus($"正在清理旧文件 ({currentOperation}/{totalOperations}): {deletion.File}");

                    journal.CurrentDeletion = deletion.File;
                    SaveJournal(workRoot, journal);
                    await ApplyDeletionAsync(normalizedGameRoot, deletionBackupRoot, deletion, ct);
                    journal.CompletedDeletions.Add(deletion.File);
                    journal.CurrentDeletion = null;
                    SaveJournal(workRoot, journal);

                    ReportApplyProgress(currentOperation, totalOperations);
                }
            }

            journal.AllOperationsApplied = true;
            SaveJournal(workRoot, journal);
            UpdateGameVersion(normalizedGameRoot, update.TargetVersion);
            journal.Completed = true;
            try
            {
                SaveJournal(workRoot, journal);
            }
            catch (Exception ex)
            {
                LogService.Warn($"保存 LDiff 完成状态失败，将直接清理工作目录: {ex.Message}");
            }
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
        global::SophonPatch diff,
        string chunkRoot,
        string? existingLdiffFolder,
        Action<long> progress,
        CancellationToken ct,
        bool requireOfficialPackage)
    {
        ValidateChunkName(diff.Id);

        if (requireOfficialPackage)
        {
            string? officialChunk = string.IsNullOrWhiteSpace(existingLdiffFolder)
                ? null
                : Path.Combine(existingLdiffFolder, diff.Id);
            if (officialChunk == null ||
                !await IsFileValidAsync(officialChunk, diff.PatchFileSize, diff.PatchFileMd5, ct))
                throw new LdiffPrerequisiteException(
                    $"官方增量包缺少或损坏: {diff.Id}。请先在官方启动器中完成预下载。");

            progress(diff.PatchFileSize);
            return officialChunk;
        }

        string? existing = FindExistingChunk(diff, chunkRoot, existingLdiffFolder);
        if (existing != null && await IsFileValidAsync(existing, diff.PatchFileSize, diff.PatchFileMd5, ct))
        {
            progress(diff.PatchFileSize);
            return existing;
        }

        string destination = Path.Combine(chunkRoot, diff.Id);
        string prefix = resource.DiffDownload?.UrlPrefix ??
                        throw new InvalidDataException("LDiff 资源缺少下载地址");
        string suffix = resource.DiffDownload?.UrlSuffix ?? string.Empty;
        string url = $"{prefix.TrimEnd('/')}/{Uri.EscapeDataString(diff.Id)}{suffix}";

        SetStatus($"正在下载 LDiff: {diff.Id}");
        await DownloadFileResumableAsync(url, destination, diff.PatchFileSize, progress, ct);
        if (!await IsFileValidAsync(destination, diff.PatchFileSize, diff.PatchFileMd5, ct))
        {
            TryDeleteFile(destination);
            throw new InvalidDataException($"LDiff 校验失败: {diff.Id}");
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
        if (expectedSize > 0 && existingLength >= expectedSize)
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
        if (existingLength > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            TryDeleteFile(destination);
            await DownloadFileResumableAsync(url, destination, expectedSize, progress, ct);
            return;
        }

        bool append = existingLength > 0 &&
                      response.StatusCode == HttpStatusCode.PartialContent &&
                      response.Content.Headers.ContentRange?.From == existingLength;
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

    private static IEnumerable<(global::SophonPatchFile Asset, global::SophonPatch Diff)> SelectDiffs(
        global::SophonPatchManifest manifest,
        string sourceVersion)
    {
        foreach (global::SophonPatchFile asset in manifest.Patches)
        {
            global::SophonPatchInfo? selected = asset.Patches.FirstOrDefault(candidate =>
                string.Equals(candidate.Tag, sourceVersion, StringComparison.OrdinalIgnoreCase));
            if (selected?.Patch != null && !string.IsNullOrWhiteSpace(selected.Patch.Id))
                yield return (asset, selected.Patch);
        }
    }

    private static IEnumerable<global::SophonPatchDeleteFile> SelectDeletions(
        global::SophonPatchManifest manifest,
        string sourceVersion)
    {
        global::SophonPatchDeleteTag? selected = manifest.DeleteTags.FirstOrDefault(candidate =>
            string.Equals(candidate.Tag, sourceVersion, StringComparison.OrdinalIgnoreCase));
        return selected?.DeleteCollection?.DeleteFiles ?? Enumerable.Empty<global::SophonPatchDeleteFile>();
    }

    private async Task<bool> ApplyAssetAsync(
        string gameRoot,
        string workRoot,
        string backupRoot,
        global::SophonPatchFile asset,
        global::SophonPatch diff,
        string chunkPath,
        bool isRecovery,
        CancellationToken ct)
    {
        string targetPath = ResolveUnderRoot(gameRoot, asset.File);
        if (await IsFileValidAsync(targetPath, asset.Size, asset.Md5, ct))
            return false;

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        string targetBackupPath = ResolveUnderRoot(backupRoot, asset.File);
        string temporaryOutput = targetPath + $".nahida-{Guid.NewGuid():N}.tmp";
        string? temporaryHdiff = null;

        try
        {
            if (string.IsNullOrWhiteSpace(diff.OriginalFileName))
            {
                if (diff.PatchLength != asset.Size)
                    throw new InvalidDataException($"新增文件分片大小不匹配: {asset.File}");
                await CopySliceAsync(chunkPath, temporaryOutput, diff.PatchOffset, diff.PatchLength, ct);
            }
            else
            {
                string sourcePath = ResolveUnderRoot(gameRoot, diff.OriginalFileName);
                string sourceBackupPath = ResolveUnderRoot(backupRoot, diff.OriginalFileName);
                bool sourceIsTarget = string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase);
                string patchSource;

                if (sourceIsTarget)
                {
                    patchSource = await PrepareInPlaceSourceAsync(targetPath, sourceBackupPath,
                        diff.OriginalFileSize, diff.OriginalFileMd5, asset.File, ct);
                }
                else
                {
                    patchSource = await FindValidPatchSourceAsync(sourcePath, sourceBackupPath,
                        diff.OriginalFileSize, diff.OriginalFileMd5, diff.OriginalFileName, ct);
                }

                temporaryHdiff = Path.Combine(workRoot, $"{Guid.NewGuid():N}.hdiff");
                await CopySliceAsync(chunkPath, temporaryHdiff, diff.PatchOffset, diff.PatchLength, ct);
                ct.ThrowIfCancellationRequested();

                var patcher = new HDiffPatch();
                patcher.Initialize(temporaryHdiff);
                patcher.Patch(patchSource, temporaryOutput, true, ct, false, true);
            }

            await VerifyFileAsync(temporaryOutput, asset.Size, asset.Md5, ct);
            ct.ThrowIfCancellationRequested();
            CommitPatchedFile(temporaryOutput, targetPath, targetBackupPath,
                asset.File, isRecovery);
            return true;
        }
        finally
        {
            TryDeleteFile(temporaryOutput);
            if (temporaryHdiff != null)
                TryDeleteFile(temporaryHdiff);
        }
    }

    private static async Task ApplyDeletionAsync(
        string gameRoot,
        string backupRoot,
        global::SophonPatchDeleteFile deletion,
        CancellationToken ct)
    {
        string targetPath = ResolveUnderRoot(gameRoot, deletion.File);
        string backupPath = ResolveUnderRoot(backupRoot, deletion.File);
        if (!File.Exists(targetPath))
            return;

        if (!await IsFileValidAsync(targetPath, deletion.Size, deletion.Md5, ct))
            throw new LdiffPrerequisiteException(
                $"待删除的旧文件与官方清单不匹配: {deletion.File}。请先使用官方启动器修复游戏。");

        if (File.Exists(backupPath))
        {
            if (!await IsFileValidAsync(backupPath, deletion.Size, deletion.Md5, ct))
                File.Delete(backupPath);
            else
            {
                File.Delete(targetPath);
                return;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
        File.Move(targetPath, backupPath);
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

        if (File.Exists(targetPath) && isRecovery)
            SetStatus($"检测到上次修补文件异常，正在清理: {assetName}");
        File.Move(patchedFile, targetPath, true);
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
        global::SophonPatch diff,
        string chunkRoot,
        string? existingLdiffFolder)
    {
        ValidateChunkName(diff.Id);
        if (!string.IsNullOrWhiteSpace(existingLdiffFolder))
        {
            string candidate = Path.Combine(existingLdiffFolder, diff.Id);
            if (File.Exists(candidate))
                return candidate;
        }

        string owned = Path.Combine(chunkRoot, diff.Id);
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

    public static bool HasPendingUpdate(string gameRoot)
    {
        if (string.IsNullOrWhiteSpace(gameRoot))
            return false;

        string stateRoot = Path.Combine(gameRoot, ".nahida", "ldiff");
        if (!Directory.Exists(stateRoot))
            return false;

        try
        {
            foreach (string path in Directory.EnumerateFiles(
                         stateRoot, "patch-state.json", SearchOption.AllDirectories))
            {
                try
                {
                    LdiffPatchJournal? journal = JsonSerializer.Deserialize(
                        File.ReadAllText(path), LdiffPatchJournalJsonContext.Default.LdiffPatchJournal);
                    if (journal?.Completed == true)
                        continue;
                    if (journal?.AllOperationsApplied == true &&
                        string.Equals(ReadInstalledVersion(gameRoot), journal.TargetVersion,
                            StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (journal == null || journal.Completed != true)
                        return true;
                }
                catch
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            return true;
        }
    }

    private static string? ReadInstalledVersion(string gameRoot)
    {
        string configPath = Path.Combine(gameRoot, "config.ini");
        if (!File.Exists(configPath))
            return null;

        foreach (string line in File.ReadLines(configPath))
        {
            if (line.StartsWith("game_version=", StringComparison.OrdinalIgnoreCase))
                return line["game_version=".Length..].Trim();
        }

        return null;
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
        global::SophonPatchFile asset,
        global::SophonPatch diff)
    {
        if (File.Exists(ResolveUnderRoot(backupRoot, asset.File)))
            return true;
        return !string.IsNullOrWhiteSpace(diff.OriginalFileName) &&
               File.Exists(ResolveUnderRoot(backupRoot, diff.OriginalFileName));
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
                if (existing?.SchemaVersion is 1 or 2 &&
                    string.Equals(existing.SourceVersion, update.SourceVersion,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.TargetVersion, update.TargetVersion,
                        StringComparison.OrdinalIgnoreCase))
                {
                    existing.CompletedAssets = new HashSet<string>(
                        existing.CompletedAssets ?? Enumerable.Empty<string>(),
                        StringComparer.OrdinalIgnoreCase);
                    existing.CompletedDeletions = new HashSet<string>(
                        existing.CompletedDeletions ?? Enumerable.Empty<string>(),
                        StringComparer.OrdinalIgnoreCase);
                    existing.SchemaVersion = 2;
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
        string temporaryPath = configPath + ".nahida.tmp";
        try
        {
            File.WriteAllLines(temporaryPath, lines);
            File.Move(temporaryPath, configPath, true);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private void SetStatus(string status)
    {
        LogService.Info(status);
        StatusChanged?.Invoke(status);
    }

    private void ReportPreparationProgress(long current, long total)
    {
        double phaseProgress = Math.Clamp((double)current / Math.Max(1, total), 0, 1);
        ProgressChanged?.Invoke(phaseProgress * 0.5, current, total);
    }

    private void ReportApplyProgress(int current, int total)
    {
        double phaseProgress = Math.Clamp((double)current / Math.Max(1, total), 0, 1);
        ProgressChanged?.Invoke(0.5 + phaseProgress * 0.5, 0, 0);
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
