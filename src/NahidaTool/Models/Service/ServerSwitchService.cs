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
public partial class ServerSwitchService
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
    private async Task<bool> IsCacheValidAsync(ServerRegionType region, string version, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string cacheDir = GetCacheDirectory(region, version);
        string cacheInfoPath = Path.Combine(cacheDir, "cache_info.json");

        if (!File.Exists(cacheInfoPath))
            return false;

        try
        {
            string json = await File.ReadAllTextAsync(cacheInfoPath, cancellationToken);
            var cacheInfo = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.ServerSwitchCacheInfo);

            string expectedRegion = region == ServerRegionType.CN ? "CN" : "OS";
            if (cacheInfo == null || cacheInfo.Version != version || cacheInfo.Region != expectedRegion ||
                cacheInfo.Files is not { Count: > 0 } || cacheInfo.FileChecksums == null)
                return false;

            // 缓存元数据和文件内容都必须匹配，避免复用损坏或被改写的缓存。
            foreach (var file in cacheInfo.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!cacheInfo.FileChecksums.TryGetValue(file, out string? expectedChecksum))
                    return false;

                string filePath = Path.Combine(cacheDir, file);
                if (!IsPathInsideDirectory(cacheDir, filePath) || !File.Exists(filePath))
                    return false;

                string actualChecksum = await CalculateFileChecksumAsync(filePath, cancellationToken);
                if (!string.Equals(actualChecksum, expectedChecksum, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
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

    private static async Task<string> CalculateFileChecksumAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);
        using var md5 = MD5.Create();
        byte[] hash = await md5.ComputeHashAsync(stream, cancellationToken);
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
            InstalledGameInfo installation;
            try
            {
                installation = await GameInstallationReader.ReadAsync(gamePath, cancellationToken);
            }
            catch (GameInstallationDetectionException ex)
            {
                throw new InvalidOperationException(Lang.GameInstallationIssueMessage(ex.Issue), ex);
            }
            var currentRegion = installation.Region;
            string currentVersion = installation.Version;

            if (currentRegion == targetRegion)
            {
                throw new Exception($"游戏已经是{(targetRegion == ServerRegionType.CN ? "国服" : "国际服")}");
            }

            NotifyDetail($"当前服务器: {(currentRegion == ServerRegionType.CN ? "国服" : "国际服")}", dispatcherQueue);
            NotifyDetail($"当前版本: {currentVersion}", dispatcherQueue);
            NotifyDetail($"目标服务器: {(targetRegion == ServerRegionType.CN ? "国服" : "国际服")}", dispatcherQueue);

            // 2. 检查缓存
            NotifyStatus("正在检查本地缓存...", dispatcherQueue);
            bool cacheValid = await IsCacheValidAsync(targetRegion, currentVersion, cancellationToken);
            string cacheDir = GetCacheDirectory(targetRegion, currentVersion);

            if (!cacheValid)
            {
                NotifyDetail("缓存无效或不存在，需要下载资源", dispatcherQueue);
                ClearInvalidCache(cacheDir);
                await DownloadServerFilesAsync(targetRegion, currentVersion, cacheDir, dispatcherQueue, cancellationToken);
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

    #region File Replacement

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
