using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using NahidaTool.Models.Enum;

namespace NahidaTool.Models.Service;

/// <summary>
/// 转服服务 - 处理国服/国际服切换
/// </summary>
public class ServerSwitchService
{
    #region Singleton & Fields

    private static ServerSwitchService? _instance;
    public static ServerSwitchService Instance => _instance ??= new ServerSwitchService();

    private readonly ApiService _apiService;
    private readonly ManifestParser _manifestParser;
    private int _isSwitching; // Interlocked + Volatile 保证线程安全

    // 游戏文件夹名称
    private const string CN_DATA_FOLDER = "YuanShen_Data";
    private const string OS_DATA_FOLDER = "GenshinImpact_Data";
    private const string CN_EXE = "YuanShen.exe";
    private const string OS_EXE = "GenshinImpact.exe";

    // 需要替换的文件列表 (相对于Data文件夹)
    private static readonly string[] FilesToReplace = new[]
    {
        "app.info",
        "globalgamemanagers",
        "globalgamemanagers.assets",
        "globalgamemanagers.assets.resS",
        "upload_crash.exe",
        @"Managed\Metadata\global-metadata.dat",
        @"Native\Data\Metadata\global-metadata.dat",
        @"Native\Data\Metadata\startup-metadata.dat",
        @"Plugins\cri_mana_vpx.dll",
        @"Plugins\cri_ware_unity.dll",
        @"Plugins\d3dcompiler_47.dll",
        @"Plugins\hpatchz.dll",
        @"Plugins\Mmoron.dll",
        @"Plugins\MTBenchmark_Windows.dll",
        @"Plugins\NamedPipeClient.dll",
        @"Plugins\UnityNativeChromaSDK.dll",
        @"Plugins\UnityNativeChromaSDK3.dll",
        @"StreamingAssets\20527480.blk",
        @"StreamingAssets\APMConfig.json"
    };

    // 根目录文件
    private static readonly string[] RootFilesToReplace = new[]
    {
        "Audio_Chinese_pkg_version",
        "mhypbase.dll",
        "pkg_version"
    };

    // 根目录文件 HashSet — O(1) 查找
    private static readonly HashSet<string> RootFileSet = new(
        RootFilesToReplace, StringComparer.OrdinalIgnoreCase);

    public event EventHandler<string>? StatusChanged;
    public event EventHandler<double>? ProgressChanged;
    public event EventHandler<string>? DetailAdded;
    public event EventHandler? SwitchCompleted;
    public event EventHandler<string>? SwitchFailed;

    public bool IsSwitching => Volatile.Read(ref _isSwitching) == 1;

    private ServerSwitchService()
    {
        _apiService = new ApiService();
        _manifestParser = new ManifestParser();
    }

    #endregion

    #region Detection

    /// <summary>
    /// 检测游戏当前服务器
    /// </summary>
    public async Task<(ServerRegionType region, string version, string error)> DetectCurrentServerAsync(string gamePath)
    {
        try
        {
            // 首先尝试找到Data文件夹
            string? dataFolder = null;
            string cnDataPath = Path.Combine(gamePath, CN_DATA_FOLDER);
            string osDataPath = Path.Combine(gamePath, OS_DATA_FOLDER);

            if (Directory.Exists(cnDataPath))
            {
                dataFolder = cnDataPath;
            }
            else if (Directory.Exists(osDataPath))
            {
                dataFolder = osDataPath;
            }
            else
            {
                return (ServerRegionType.CN, string.Empty, "未找到游戏Data文件夹");
            }

            // 读取APMConfig.json
            string apmConfigPath = Path.Combine(dataFolder, "StreamingAssets", "APMConfig.json");
            if (!File.Exists(apmConfigPath))
            {
                return (ServerRegionType.CN, string.Empty, "未找到APMConfig.json");
            }

            string json = await File.ReadAllTextAsync(apmConfigPath);
            var apmConfig = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.APMConfig);

            if (apmConfig == null)
            {
                return (ServerRegionType.CN, string.Empty, "无法解析APMConfig.json");
            }

            var region = apmConfig.IsCN ? ServerRegionType.CN : ServerRegionType.OS;
            var version = apmConfig.GetVersionNumber();

            return (region, version, string.Empty);
        }
        catch (Exception ex)
        {
            return (ServerRegionType.CN, string.Empty, $"检测失败: {ex.Message}");
        }
    }

    #endregion

    #region Cache

    /// <summary>
    /// 获取缓存目录 (程序上级目录的 ServerSwitchCache)
    /// </summary>
    private string GetCacheDirectory(ServerRegionType region, string version)
    {
        // 缓存放到 Assets 目录下
        string appDir = AppContext.BaseDirectory;
        string cacheDir = Path.Combine(appDir, "Assets/ServerSwitchCache",
            region == ServerRegionType.CN ? "CN" : "OS", version);
        return cacheDir;
    }

    /// <summary>
    /// 检查缓存是否存在且有效
    /// </summary>
    private async Task<bool> IsCacheValidAsync(ServerRegionType region, string version)
    {
        string cacheDir = GetCacheDirectory(region, version);
        string cacheInfoPath = Path.Combine(cacheDir, "cache_info.json");

        if (!File.Exists(cacheInfoPath))
            return false;

        try
        {
            string json = await File.ReadAllTextAsync(cacheInfoPath);
            var cacheInfo = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.ServerSwitchCacheInfo);

            string expectedRegion = region == ServerRegionType.CN ? "CN" : "OS";
            if (cacheInfo == null || cacheInfo.Version != version || cacheInfo.Region != expectedRegion ||
                cacheInfo.Files == null || cacheInfo.FileChecksums == null)
                return false;

            // 缓存元数据和文件内容都必须匹配，避免复用损坏或被改写的缓存。
            foreach (var file in cacheInfo.Files)
            {
                if (!cacheInfo.FileChecksums.TryGetValue(file, out string? expectedChecksum))
                    return false;

                string filePath = Path.Combine(cacheDir, file);
                if (!IsPathInsideDirectory(cacheDir, filePath) || !File.Exists(filePath))
                    return false;

                string actualChecksum = await CalculateFileChecksumAsync(filePath);
                if (!string.Equals(actualChecksum, expectedChecksum, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            LogService.Debug($"检查缓存有效性失败: {ex.Message}");
            return false;
        }
    }

    private static bool IsPathInsideDirectory(string directory, string path)
    {
        string fullDirectory = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> CalculateFileChecksumAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);
        using var md5 = MD5.Create();
        byte[] hash = await md5.ComputeHashAsync(stream);
        return Convert.ToHexString(hash);
    }

    private static void ClearInvalidCache(string cacheDir)
    {
        if (Directory.Exists(cacheDir))
            Directory.Delete(cacheDir, recursive: true);
    }

    #endregion

    #region Switch

    /// <summary>
    /// 执行转服操作
    /// </summary>
    public async Task SwitchServerAsync(string gamePath, ServerRegionType targetRegion,
        DispatcherQueue dispatcherQueue, CancellationToken cancellationToken = default)
    {
        // 优化: CAS 保证只有一个切换任务执行
        if (Interlocked.CompareExchange(ref _isSwitching, 1, 0) != 0)
            return;

        ServerSwitchTransaction? transaction = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 1. 检测当前服务器
            NotifyStatus("正在检测当前服务器...", dispatcherQueue);
            var (currentRegion, currentVersion, error) = await DetectCurrentServerAsync(gamePath);

            if (!string.IsNullOrEmpty(error))
            {
                throw new Exception(error);
            }

            if (currentRegion == targetRegion)
            {
                throw new Exception($"游戏已经是{(targetRegion == ServerRegionType.CN ? "国服" : "国际服")}");
            }

            NotifyDetail($"当前服务器: {(currentRegion == ServerRegionType.CN ? "国服" : "国际服")}", dispatcherQueue);
            NotifyDetail($"当前版本: {currentVersion}", dispatcherQueue);
            NotifyDetail($"目标服务器: {(targetRegion == ServerRegionType.CN ? "国服" : "国际服")}", dispatcherQueue);

            // 2. 检查缓存
            NotifyStatus("正在检查本地缓存...", dispatcherQueue);
            bool cacheValid = await IsCacheValidAsync(targetRegion, currentVersion);
            string cacheDir = GetCacheDirectory(targetRegion, currentVersion);

            if (!cacheValid)
            {
                NotifyDetail("缓存无效或不存在，需要下载资源", dispatcherQueue);
                ClearInvalidCache(cacheDir);
                await DownloadServerFilesAsync(targetRegion, currentVersion, cacheDir, dispatcherQueue);
            }
            else
            {
                NotifyDetail("使用本地缓存", dispatcherQueue);
            }

            // 3. 备份并替换文件
            NotifyStatus("正在替换文件...", dispatcherQueue);
            transaction = new ServerSwitchTransaction(gamePath);
            await ReplaceFilesAsync(gamePath, currentRegion, targetRegion, cacheDir, dispatcherQueue, transaction);

            // 4. 重命名文件夹
            NotifyStatus("正在重命名文件夹...", dispatcherQueue);
            await RenameFoldersAsync(gamePath, currentRegion, targetRegion, dispatcherQueue, transaction);

            // 5. 删除旧exe
            NotifyStatus("正在清理旧文件...", dispatcherQueue);
            await DeleteOldExeAsync(gamePath, currentRegion, dispatcherQueue, transaction);

            transaction.Commit();

            NotifyStatus("转服完成!", dispatcherQueue);
            NotifyProgress(100, dispatcherQueue);
            dispatcherQueue.TryEnqueue(() => SwitchCompleted?.Invoke(this, EventArgs.Empty));
        }
        catch (OperationCanceledException)
        {
            transaction?.Rollback();
            NotifyStatus("转服已取消", dispatcherQueue);
            dispatcherQueue.TryEnqueue(() => SwitchFailed?.Invoke(this, "操作被取消"));
        }
        catch (Exception ex)
        {
            transaction?.Rollback();
            NotifyStatus($"转服失败: {ex.Message}", dispatcherQueue);
            NotifyDetail($"错误详情: {ex.Message}", dispatcherQueue);
            dispatcherQueue.TryEnqueue(() => SwitchFailed?.Invoke(this, ex.Message));
        }
        finally
        {
            transaction?.Dispose();
            Volatile.Write(ref _isSwitching, 0);
        }
    }

    #endregion

    #region Download & Merge

    /// <summary>
    /// 从API下载服务器文件
    /// </summary>
    private async Task DownloadServerFilesAsync(ServerRegionType targetRegion, string version, string cacheDir,
        DispatcherQueue dispatcherQueue)
    {
        NotifyStatus("正在从服务器获取资源信息...", dispatcherQueue);

        // 设置API区域
        _apiService.SetRegion(targetRegion);

        // 获取构建信息 - 传递当前游戏版本作为tag，确保获取对应版本的资源
        var buildInfo = await _apiService.GetBuildInfoAsync(version);
        if (buildInfo?.Data?.Manifests == null || buildInfo.Data.Manifests.Count == 0)
        {
            throw new Exception("无法获取服务器构建信息");
        }

        // 找到游戏主体的BuildData
        var gameBuildData = buildInfo.Data.Manifests.FirstOrDefault(m =>
            m.MatchingField == "game" || string.IsNullOrEmpty(m.MatchingField));

        if (gameBuildData == null)
        {
            throw new Exception("无法找到游戏主体资源");
        }

        NotifyDetail($"构建ID: {buildInfo.Data.BuildId}", dispatcherQueue);
        NotifyDetail($"Tag: {buildInfo.Data.Tag}", dispatcherQueue);

        // 下载并解析Manifest
        NotifyStatus("正在下载Manifest...", dispatcherQueue);
        byte[] manifestData = await _apiService.DownloadManifestAsync(
            gameBuildData.ManifestDownload?.UrlPrefix ?? string.Empty,
            gameBuildData.Manifest?.Id ?? string.Empty);

        byte[] decompressedData = _manifestParser.DecompressZstd(manifestData);
        List<FileChunkInfo> allFiles = _manifestParser.ParseChunkManifest(decompressedData);

        NotifyDetail($"Manifest解析完成，共 {allFiles.Count} 个文件", dispatcherQueue);

        // 筛选需要下载的文件
        var filesToDownload = FilterFilesToDownload(allFiles, targetRegion);
        NotifyDetail($"需要下载 {filesToDownload.Count} 个转服文件", dispatcherQueue);

        if (filesToDownload.Count == 0)
        {
            throw new Exception("未找到需要下载的转服文件");
        }

        // 创建缓存目录
        Directory.CreateDirectory(cacheDir);
        string chunksDir = Path.Combine(cacheDir, "chunks");
        Directory.CreateDirectory(chunksDir);

        // 下载文件
        int totalChunks = filesToDownload.Sum(f => f.Chunks?.Count ?? 0);
        int downloadedChunks = 0;
        var downloadedFiles = new List<string>();
        var fileChecksums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var semaphore = new SemaphoreSlim(8);

        foreach (var fileInfo in filesToDownload)
        {
            NotifyDetail($"下载: {fileInfo.FilePath}", dispatcherQueue);

            var tasks = new List<Task>();
            foreach (var chunk in fileInfo.Chunks ?? new List<ChunkInfo>())
            {
                tasks.Add(DownloadChunkAsync(chunk, gameBuildData.ChunkDownload?.UrlPrefix ?? string.Empty,
                    chunksDir, semaphore, () =>
                    {
                        Interlocked.Increment(ref downloadedChunks);
                        double progress = totalChunks > 0 ? (double)downloadedChunks / totalChunks * 50 : 0;
                        NotifyProgress(progress, dispatcherQueue);
                    }));
            }

            await Task.WhenAll(tasks);

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

            string actualChecksum = await CalculateFileChecksumAsync(outputPath);
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
        await File.WriteAllTextAsync(cacheInfoPath, json);

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
    private List<FileChunkInfo> FilterFilesToDownload(List<FileChunkInfo> allFiles, ServerRegionType targetRegion)
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
        SemaphoreSlim semaphore, Action onCompleted)
    {
        if (chunk == null) return;

        await semaphore.WaitAsync();
        try
        {

            string chunkPath = Path.Combine(chunksDir, chunk.ChunkId ?? string.Empty);

            // 检查是否已存在且有效
            if (File.Exists(chunkPath) && new FileInfo(chunkPath).Length == chunk.CompressedSize)
            {
                onCompleted?.Invoke();
                return;
            }

            await _apiService.DownloadChunkToFileAsync(urlPrefix, chunk.ChunkId ?? string.Empty, chunkPath);
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

    /// <summary>
    /// 替换文件
    /// </summary>
    private async Task ReplaceFilesAsync(string gamePath, ServerRegionType currentRegion, ServerRegionType targetRegion,
        string cacheDir, DispatcherQueue dispatcherQueue, ServerSwitchTransaction transaction)
    {
        string currentDataFolder = currentRegion == ServerRegionType.CN ? CN_DATA_FOLDER : OS_DATA_FOLDER;
        string targetDataFolder = targetRegion == ServerRegionType.CN ? CN_DATA_FOLDER : OS_DATA_FOLDER;

        int totalFiles = FilesToReplace.Length + RootFilesToReplace.Length + 1; // +1 for exe
        int processedFiles = 0;

        // 替换根目录文件
        foreach (var rootFile in RootFilesToReplace)
        {
            string sourcePath = Path.Combine(cacheDir, rootFile);
            string destPath = Path.Combine(gamePath, rootFile);

            if (File.Exists(sourcePath))
            {
                await CopyFileAtomicallyAsync(sourcePath, destPath, transaction);
                NotifyDetail($"已替换: {rootFile}", dispatcherQueue);
            }

            processedFiles++;
            NotifyProgress(50 + (double)processedFiles / totalFiles * 30, dispatcherQueue);
        }

        // 替换Data目录文件
        foreach (var dataFile in FilesToReplace)
        {
            string sourcePath = Path.Combine(cacheDir, targetDataFolder, dataFile);
            string destPath = Path.Combine(gamePath, currentDataFolder, dataFile);

            if (File.Exists(sourcePath))
            {
                await CopyFileAtomicallyAsync(sourcePath, destPath, transaction);
                NotifyDetail($"已替换: {dataFile}", dispatcherQueue);
            }

            processedFiles++;
            NotifyProgress(50 + (double)processedFiles / totalFiles * 30, dispatcherQueue);
        }

        // 复制新的exe
        string targetExe = targetRegion == ServerRegionType.CN ? CN_EXE : OS_EXE;
        string exeSourcePath = Path.Combine(cacheDir, targetExe);
        string exeDestPath = Path.Combine(gamePath, targetExe);

        if (File.Exists(exeSourcePath))
        {
            await CopyFileAtomicallyAsync(exeSourcePath, exeDestPath, transaction);
            NotifyDetail($"已添加: {targetExe}", dispatcherQueue);
        }
    }

    /// <summary>
    /// 使用临时文件和原子替换复制缓存文件
    /// </summary>
    private static async Task CopyFileAtomicallyAsync(string sourcePath, string destPath,
        ServerSwitchTransaction transaction)
    {
        transaction.BackupFile(destPath);
        string? destDir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
        {
            Directory.CreateDirectory(destDir);
        }

        int maxRetries = 5;
        int retryDelayMs = 1000;
        Exception? lastException = null;

        for (int i = 0; i < maxRetries; i++)
        {
            string tempPath = destPath + $".nahidatool-{Guid.NewGuid():N}.tmp";
            try
            {
                if (File.Exists(destPath))
                    File.SetAttributes(destPath, FileAttributes.Normal);

                await using (var sourceStream = new FileStream(sourcePath,
                    FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 65536, useAsync: true))
                await using (var destStream = new FileStream(tempPath,
                    FileMode.Create, FileAccess.Write, FileShare.None,
                    bufferSize: 65536, useAsync: true))
                {
                    await sourceStream.CopyToAsync(destStream);
                    await destStream.FlushAsync();
                }

                File.Move(tempPath, destPath, overwrite: true);
                return;
            }
            catch (IOException ex) when (i < maxRetries - 1)
            {
                lastException = ex;
                await Task.Delay(retryDelayMs);
                retryDelayMs *= 2;
            }
            catch (UnauthorizedAccessException ex) when (i < maxRetries - 1)
            {
                lastException = ex;
                await Task.Delay(retryDelayMs);
                retryDelayMs *= 2;
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
        }

        throw new IOException($"无法安全替换文件: {destPath}", lastException);
    }

    #endregion

    #region Folder Operations

    /// <summary>
    /// 重命名文件夹
    /// </summary>
    private async Task RenameFoldersAsync(string gamePath, ServerRegionType currentRegion, ServerRegionType targetRegion,
        DispatcherQueue dispatcherQueue, ServerSwitchTransaction transaction)
    {
        string currentDataFolder = currentRegion == ServerRegionType.CN ? CN_DATA_FOLDER : OS_DATA_FOLDER;
        string targetDataFolder = targetRegion == ServerRegionType.CN ? CN_DATA_FOLDER : OS_DATA_FOLDER;

        string currentDataPath = Path.Combine(gamePath, currentDataFolder);
        string targetDataPath = Path.Combine(gamePath, targetDataFolder);

        // 检查当前Data文件夹是否存在
        if (!Directory.Exists(currentDataPath))
        {
            NotifyDetail($"警告: 未找到当前Data文件夹 {currentDataFolder}", dispatcherQueue);
            return;
        }

        // 重命名文件夹
        NotifyDetail($"正在将 {currentDataFolder} 重命名为 {targetDataFolder}...", dispatcherQueue);

        int maxRetries = 5;
        int retryDelayMs = 1000;

        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                // 递归清除文件夹及其内容的特殊属性
                NotifyDetail($"正在检查并清除文件属性 (尝试 {i + 1}/{maxRetries})...", dispatcherQueue);
                int clearedCount = ClearDirectoryAttributes(currentDataPath, dispatcherQueue);
                if (clearedCount > 0)
                {
                    NotifyDetail($"已清除 {clearedCount} 个文件/文件夹的特殊属性", dispatcherQueue);
                }

                // 如果目标文件夹存在，也清除其属性
                if (Directory.Exists(targetDataPath))
                {
                    int targetClearedCount = ClearDirectoryAttributes(targetDataPath, dispatcherQueue);
                    if (targetClearedCount > 0)
                    {
                        NotifyDetail($"已清除目标文件夹 {targetClearedCount} 个文件/文件夹的特殊属性", dispatcherQueue);
                    }

                    // 目标文件夹已存在，使用临时名称备份
                    transaction.BackupDirectory(targetDataPath);
                    NotifyDetail($"已备份现有文件夹 {targetDataFolder}", dispatcherQueue);
                }

                // 现在执行重命名
                Directory.Move(currentDataPath, targetDataPath);
                transaction.RecordDirectoryMove(currentDataPath, targetDataPath);
                NotifyDetail($"成功将 {currentDataFolder} 重命名为 {targetDataFolder}", dispatcherQueue);
                return; // 成功则返回
            }
            catch (IOException ex) when (i < maxRetries - 1)
            {
                string errorMsg = ex.Message.Contains("being used") || ex.Message.Contains("占用")
                    ? $"文件夹正在被占用 (尝试 {i + 1}/{maxRetries}): {ex.Message}\n建议: 关闭所有可能访问该文件夹的程序，如游戏、文件管理器等"
                    : $"文件夹重命名失败 (尝试 {i + 1}/{maxRetries}): {ex.Message}";
                NotifyDetail(errorMsg, dispatcherQueue);
                await Task.Delay(retryDelayMs);
                retryDelayMs *= 2; // 指数退避
            }
            catch (UnauthorizedAccessException ex) when (i < maxRetries - 1)
            {
                string errorMsg = $"文件夹重命名权限不足 (尝试 {i + 1}/{maxRetries}): {ex.Message}\n建议: 以管理员身份运行程序，或检查文件夹权限设置";
                NotifyDetail(errorMsg, dispatcherQueue);
                await Task.Delay(retryDelayMs);
                retryDelayMs *= 2;
            }
        }

        try
        {
            // 最后一次尝试前再次清除属性
            NotifyDetail("最后一次尝试，正在深度清除文件属性...", dispatcherQueue);
            ClearDirectoryAttributes(currentDataPath, dispatcherQueue);

            if (Directory.Exists(targetDataPath))
            {
                ClearDirectoryAttributes(targetDataPath, dispatcherQueue);

                // 目标文件夹已存在，使用临时名称备份
                string backupPath = $"{targetDataPath}_backup_{DateTime.Now:yyyyMMddHHmmss}";
                NotifyDetail($"目标文件夹 {targetDataFolder} 已存在，正在备份为 {Path.GetFileName(backupPath)}...", dispatcherQueue);
                Directory.Move(targetDataPath, backupPath);
                NotifyDetail($"已备份现有文件夹到 {Path.GetFileName(backupPath)}", dispatcherQueue);
            }

            Directory.Move(currentDataPath, targetDataPath);
            NotifyDetail($"成功将 {currentDataFolder} 重命名为 {targetDataFolder}", dispatcherQueue);
        }
        catch (IOException ex)
        {
            string errorMsg = ex.Message.Contains("being used") || ex.Message.Contains("占用")
                ? $"无法重命名文件夹 {currentDataFolder} 到 {targetDataFolder}: 文件夹正在被占用\n\n可能的原因和解决方案:\n1. 游戏正在运行 - 请完全关闭游戏\n2. 文件管理器正在打开该文件夹 - 请关闭所有相关窗口\n3. 杀毒软件或防火墙正在扫描 - 请暂时禁用或添加例外\n4. 其他程序正在访问文件夹 - 请关闭所有可能相关的程序\n\n建议: 以管理员身份运行程序，并确保所有可能访问该文件夹的程序都已关闭"
                : $"无法重命名文件夹 {currentDataFolder} 到 {targetDataFolder}: {ex.Message}";
            NotifyDetail($"警告: {errorMsg}", dispatcherQueue);
            throw new Exception(errorMsg);
        }
        catch (UnauthorizedAccessException)
        {
            string errorMsg =
                $"无法重命名文件夹 {currentDataFolder} 到 {targetDataFolder}: 权限不足\n\n可能的原因和解决方案:\n1. 程序没有足够的权限 - 请以管理员身份运行程序\n2. 文件夹权限设置问题 - 请检查文件夹属性中的安全设置\n3. 文件夹位于受保护的位置 - 如Program Files文件夹\n\n建议: 右键点击程序图标，选择'以管理员身份运行'，或移动游戏到非系统盘";
            NotifyDetail($"警告: {errorMsg}", dispatcherQueue);
            throw new Exception(errorMsg);
        }
        catch (Exception ex)
        {
            string errorMsg =
                $"无法重命名文件夹 {currentDataFolder} 到 {targetDataFolder}: {ex.Message}\n\n通用解决方案:\n1. 以管理员身份运行程序\n2. 关闭所有可能访问该文件夹的程序\n3. 检查文件夹是否被设置为只读\n4. 确保游戏文件夹位于非系统盘\n5. 暂时禁用杀毒软件和防火墙";
            NotifyDetail($"警告: {errorMsg}", dispatcherQueue);
            throw new Exception(errorMsg);
        }
    }

    /// <summary>
    /// 单次遍历递归清除目录及其内容的特殊文件属性（只读、隐藏、系统等）
    /// </summary>
    /// <returns>清除属性的文件/文件夹数量</returns>
    private int ClearDirectoryAttributes(string directoryPath, DispatcherQueue dispatcherQueue)
    {
        int clearedCount = 0;
        const FileAttributes attrsToRemove = FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System;

        try
        {
            DirectoryInfo dirInfo = new DirectoryInfo(directoryPath);
            if (!dirInfo.Exists) return 0;

            // 优化: 使用 EnumerationOptions 单次遍历所有条目，避免 GetDirectories + GetFiles 两次枚举
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.None,
                IgnoreInaccessible = true,
            };

            foreach (FileSystemInfo fsi in dirInfo.EnumerateFileSystemInfos("*", options))
            {
                try
                {
                    if ((fsi.Attributes & attrsToRemove) != 0)
                    {
                        fsi.Attributes &= ~attrsToRemove;
                        clearedCount++;
                    }
                }
                catch (Exception ex)
                {
                    LogService.Debug($"清除单个条目属性失败: {ex.Message}");
                }
            }

            // 清理根目录本身
            if ((dirInfo.Attributes & attrsToRemove) != 0)
            {
                dirInfo.Attributes &= ~attrsToRemove;
                clearedCount++;
            }
        }
        catch (Exception ex)
        {
            dispatcherQueue.TryEnqueue(() =>
                DetailAdded?.Invoke(this, $"清除文件属性时出现警告: {ex.Message}"));
        }

        return clearedCount;
    }

    #endregion

    #region Cleanup

    /// <summary>
    /// 删除旧的 exe；失败时由外层事务回滚
    /// </summary>
    private async Task DeleteOldExeAsync(string gamePath, ServerRegionType currentRegion, DispatcherQueue dispatcherQueue,
        ServerSwitchTransaction transaction)
    {
        string oldExe = currentRegion == ServerRegionType.CN ? CN_EXE : OS_EXE;
        string oldExePath = Path.Combine(gamePath, oldExe);

        if (!File.Exists(oldExePath))
        {
            return;
        }

        transaction.BackupFile(oldExePath);

        int maxRetries = 5;
        int retryDelayMs = 500;

        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                File.SetAttributes(oldExePath, FileAttributes.Normal);
                File.Delete(oldExePath);
                NotifyDetail($"已删除: {oldExe}", dispatcherQueue);
                return;
            }
            catch (IOException ex) when (i < maxRetries - 1)
            {
                NotifyDetail($"删除旧文件失败 (尝试 {i + 1}/{maxRetries}): {ex.Message}", dispatcherQueue);
                await Task.Delay(retryDelayMs);
                retryDelayMs *= 2;
            }
            catch (UnauthorizedAccessException ex) when (i < maxRetries - 1)
            {
                NotifyDetail($"删除旧文件权限不足 (尝试 {i + 1}/{maxRetries}): {ex.Message}", dispatcherQueue);
                await Task.Delay(retryDelayMs);
                retryDelayMs *= 2;
            }
            catch (Exception ex)
            {
                throw new IOException($"无法删除旧文件 {oldExe}", ex);
            }
        }

        throw new IOException($"无法删除旧文件 {oldExe}");
    }

    #endregion

    private sealed class ServerSwitchTransaction : IDisposable
    {
        private sealed record FileBackup(string Path, string BackupPath, bool Existed, FileAttributes Attributes);
        private sealed record DirectoryBackup(string Path, string BackupPath);
        private sealed record DirectoryMove(string SourcePath, string DestinationPath);

        private readonly string _gamePath;
        private readonly string _backupRoot;
        private readonly List<FileBackup> _fileBackups = new();
        private readonly List<DirectoryBackup> _directoryBackups = new();
        private readonly List<DirectoryMove> _directoryMoves = new();
        private readonly HashSet<string> _backedUpFiles = new(StringComparer.OrdinalIgnoreCase);
        private bool _committed;
        private bool _rolledBack;

        public ServerSwitchTransaction(string gamePath)
        {
            _gamePath = Path.GetFullPath(gamePath);
            _backupRoot = Path.Combine(_gamePath, $".nahidatool-switch-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_backupRoot);
        }

        public void BackupFile(string path)
        {
            string fullPath = Path.GetFullPath(path);
            string relativePath = GetRelativePath(fullPath);
            if (!_backedUpFiles.Add(fullPath))
                return;

            bool existed = File.Exists(fullPath);
            string backupPath = Path.Combine(_backupRoot, "files", relativePath);
            FileAttributes attributes = existed ? File.GetAttributes(fullPath) : FileAttributes.Normal;

            if (existed)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                File.Copy(fullPath, backupPath, overwrite: true);
            }

            _fileBackups.Add(new FileBackup(fullPath, backupPath, existed, attributes));
        }

        public void BackupDirectory(string path)
        {
            string fullPath = Path.GetFullPath(path);
            if (!Directory.Exists(fullPath))
                return;

            string backupPath = Path.Combine(_backupRoot, "directories", GetRelativePath(fullPath));
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            Directory.Move(fullPath, backupPath);
            _directoryBackups.Add(new DirectoryBackup(fullPath, backupPath));
        }

        public void RecordDirectoryMove(string sourcePath, string destinationPath)
        {
            _directoryMoves.Add(new DirectoryMove(Path.GetFullPath(sourcePath), Path.GetFullPath(destinationPath)));
        }

        public void Commit()
        {
            _committed = true;
            CleanupBackups();
        }

        public void Rollback()
        {
            if (_committed || _rolledBack)
                return;

            _rolledBack = true;

            foreach (DirectoryMove move in _directoryMoves.AsEnumerable().Reverse())
            {
                try
                {
                    if (Directory.Exists(move.DestinationPath) && !Directory.Exists(move.SourcePath))
                        Directory.Move(move.DestinationPath, move.SourcePath);
                }
                catch (Exception ex)
                {
                    LogService.Error($"还原目录重命名失败: {move.DestinationPath}", ex);
                }
            }

            foreach (FileBackup backup in _fileBackups.AsEnumerable().Reverse())
            {
                try
                {
                    if (File.Exists(backup.Path))
                    {
                        File.SetAttributes(backup.Path, FileAttributes.Normal);
                        File.Delete(backup.Path);
                    }

                    if (backup.Existed)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(backup.Path)!);
                        File.Copy(backup.BackupPath, backup.Path, overwrite: true);
                        File.SetAttributes(backup.Path, backup.Attributes);
                    }
                }
                catch (Exception ex)
                {
                    LogService.Error($"还原文件失败: {backup.Path}", ex);
                }
            }

            foreach (DirectoryBackup backup in _directoryBackups.AsEnumerable().Reverse())
            {
                try
                {
                    if (!Directory.Exists(backup.Path) && Directory.Exists(backup.BackupPath))
                        Directory.Move(backup.BackupPath, backup.Path);
                }
                catch (Exception ex)
                {
                    LogService.Error($"还原备份目录失败: {backup.Path}", ex);
                }
            }

            CleanupBackups();
        }

        public void Dispose()
        {
            if (_committed || _rolledBack)
                CleanupBackups();
        }

        private string GetRelativePath(string path)
        {
            if (!IsPathInsideDirectory(_gamePath, path))
                throw new InvalidOperationException($"转服文件不在游戏目录内: {path}");

            return Path.GetRelativePath(_gamePath, path);
        }

        private void CleanupBackups()
        {
            try
            {
                if (Directory.Exists(_backupRoot))
                    Directory.Delete(_backupRoot, recursive: true);
            }
            catch (Exception ex)
            {
                LogService.Warn($"清理转服备份失败: {ex.Message}");
            }
        }
    }

    #region Helpers

    private void NotifyStatus(string status, DispatcherQueue dispatcherQueue)
    {
        dispatcherQueue.TryEnqueue(() => StatusChanged?.Invoke(this, status));
    }

    private void NotifyProgress(double progress, DispatcherQueue dispatcherQueue)
    {
        dispatcherQueue.TryEnqueue(() => ProgressChanged?.Invoke(this, progress));
    }

    private void NotifyDetail(string detail, DispatcherQueue dispatcherQueue)
    {
        dispatcherQueue.TryEnqueue(() => DetailAdded?.Invoke(this, detail));
    }

    #endregion
}
