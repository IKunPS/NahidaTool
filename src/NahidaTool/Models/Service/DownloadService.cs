using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Polly;
using Polly.Retry;

namespace NahidaTool.Models.Service;

public enum DownloadStage
{
    Idle,
    Preparing,
    CheckingFiles,
    Downloading,
    Paused,
}

public class DownloadService
{
    #region Singleton & Fields

    private static DownloadService? _instance;
    public static DownloadService Instance => _instance ??= new DownloadService();

    private const int BUFFER_SIZE = 65536; // 64KB
    private const int MD5_BUFFER_SIZE = 1 << 19; // 512KB
    private const string DOWNLOAD_MARKER_FILE = ".nahidatool.download";

    private ApiService _apiService;
    private volatile bool _isDownloading;
    private readonly object _downloadStateLock = new();
    private bool _isCancelled;
    private volatile bool _isPaused;
    private volatile DownloadStage _currentStage;
    private DownloadStage _stageBeforePause = DownloadStage.Downloading;
    private volatile TaskCompletionSource _pauseTcs = new();
    private string _downloadPath = string.Empty;
    private string _downloadRoot = string.Empty;
    private CancellationTokenSource? _globalCts;
    private DispatcherQueue? _uiDispatcher;

    // 进度追踪 (Interlocked — 参考 Starward GameInstallContext)
    private long _totalDownloadBytes;
    private long _totalWriteBytes;
    internal long _downloadedBytes;
    internal long _writtenBytes;

    // 速度追踪用字段
    internal long _networkDownloadBytes;
    internal long _storageWriteBytes;

    // 速度计时
    private long _lastDownloadedBytes;
    private long _lastWrittenBytes;
    private DateTime _lastSpeedTime;
    private System.Timers.Timer? _speedTimer;
    private DateTime _lastProgressNotifyTime;

    // 速率限制器 (参考 Starward TokenBucketRateLimiter)
    private TokenBucketRateLimiter _rateLimiter;

    // Polly 重试管道 (参考 Starward ResiliencePipeline)
    private readonly ResiliencePipeline _retryPipeline;

    public event EventHandler<string>? StatusChanged;
    public event EventHandler<double>? ProgressChanged;
    public event EventHandler<string>? ProgressTextChanged;
    public event EventHandler<string>? DetailAdded;
    public event EventHandler? DownloadCompleted;
    public event EventHandler? DownloadFailed;
    public event EventHandler<(double speedMbps, double writeSpeedMbps, TimeSpan remaining)>? SpeedUpdated;

    public bool IsDownloading => _isDownloading;
    public bool IsPaused => _isPaused;
    public DownloadStage CurrentStage => _currentStage;

    #endregion

    #region Constructor

    private DownloadService()
    {
        _apiService = new ApiService();
        _isDownloading = false;
        _isCancelled = false;
        _isPaused = false;
        _pauseTcs.SetResult();

        _rateLimiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            AutoReplenishment = true,
            QueueLimit = int.MaxValue,
            TokenLimit = int.MaxValue,
            ReplenishmentPeriod = TimeSpan.FromMilliseconds(100),
            TokensPerPeriod = int.MaxValue,
        });

        _retryPipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 5,
                BackoffType = DelayBackoffType.Linear,
                Delay = TimeSpan.FromSeconds(2),
            })
            .Build();
    }

    #endregion

    #region Rate Limiter

    /// <summary>
    /// 设置下载速率限制 (bytes/s)，0 表示不限速
    /// </summary>
    public int SetRateLimiter(int bytesPerSecond)
    {
        int result;
        if (bytesPerSecond <= 0)
        {
            _rateLimiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
            {
                AutoReplenishment = true,
                QueueLimit = int.MaxValue,
                TokenLimit = int.MaxValue,
                ReplenishmentPeriod = TimeSpan.FromMilliseconds(100),
                TokensPerPeriod = int.MaxValue,
            });
            result = 0;
        }
        else
        {
            int limit = Math.Clamp(bytesPerSecond / 10, BUFFER_SIZE, int.MaxValue);
            _rateLimiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
            {
                AutoReplenishment = true,
                QueueLimit = int.MaxValue,
                TokenLimit = limit,
                ReplenishmentPeriod = TimeSpan.FromMilliseconds(100),
                TokensPerPeriod = limit,
            });
            result = Math.Clamp(limit, 0, int.MaxValue / 10) * 10;
        }

        LogService.Debug($"下载限速设置为: {result} bytes/s");
        return result;
    }

    #endregion

    #region Public API

    public void Initialize(string downloadPath)
    {
        _downloadPath = downloadPath;
        LogService.Debug($"DownloadService 初始化: 下载路径={downloadPath}");
    }

    public static bool IsValidResource(BuildData? buildData)
    {
        return buildData != null
               && !string.IsNullOrWhiteSpace(buildData.Manifest?.Id)
               && !string.IsNullOrWhiteSpace(buildData.ManifestDownload?.UrlPrefix)
               && !string.IsNullOrWhiteSpace(buildData.ChunkDownload?.UrlPrefix);
    }

    /// <summary>
    /// 开始下载游戏本体和语音包
    /// </summary>
    public async Task<bool> StartDownloadAsync(BuildData? gameBuildData, BuildData? voiceBuildData,
        DispatcherQueue dispatcherQueue)
    {
        if (_isDownloading) return false;

        var buildDataList = new List<BuildData>();
        if (gameBuildData != null) buildDataList.Add(gameBuildData);
        if (voiceBuildData != null) buildDataList.Add(voiceBuildData);

        if (buildDataList.Count == 0 || buildDataList.Any(buildData => !IsValidResource(buildData)))
            return false;

        return await StartDownloadMultipleAsync(buildDataList, dispatcherQueue);
    }

    /// <summary>
    /// 开始下载单个资源包（向后兼容）
    /// </summary>
    public async Task<bool> StartDownloadAsync(BuildData? selectedBuildData, DispatcherQueue dispatcherQueue)
    {
        if (_isDownloading || selectedBuildData == null) return false;
        return await StartDownloadMultipleAsync(new List<BuildData> { selectedBuildData }, dispatcherQueue);
    }

    /// <summary>
    /// 下载游戏本体和多个语音包（弹窗多选语言）
    /// </summary>
    public async Task<bool> StartDownloadAsync(BuildData? gameBuildData, List<BuildData> voiceBuildDataList,
        DispatcherQueue dispatcherQueue)
    {
        if (_isDownloading) return false;

        var buildDataList = new List<BuildData>();
        if (gameBuildData != null) buildDataList.Add(gameBuildData);
        if (voiceBuildDataList != null) buildDataList.AddRange(voiceBuildDataList);

        if (buildDataList.Count == 0) return false;

        return await StartDownloadMultipleAsync(buildDataList, dispatcherQueue);
    }

    private async Task<bool> StartDownloadMultipleAsync(
        List<BuildData> buildDataList,
        DispatcherQueue dispatcherQueue)
    {
        if (buildDataList.Count == 0 || buildDataList.Any(buildData => !IsValidResource(buildData)))
            return false;

        lock (_downloadStateLock)
        {
            if (_isDownloading) return false;
            _isDownloading = true;
        }

        _globalCts = new CancellationTokenSource();
        var ct = _globalCts.Token;
        _uiDispatcher = dispatcherQueue; // 保存 DispatcherQueue 供速度计时器安全更新 UI

        _downloadRoot = _downloadPath;
        bool completedSuccessfully = false;
        bool downloadFailed = false;

        try
        {
            _isCancelled = false;
            _isPaused = false;
            _pauseTcs.TrySetResult();
            _currentStage = DownloadStage.Preparing;

            LogService.Info($"开始下载: 资源包数量={buildDataList.Count}, 下载路径={_downloadRoot}");
            foreach (var bd in buildDataList)
            {
                LogService.Debug($"  - 资源: {bd.MatchingField}, ManifestId={bd.Manifest?.Id}");
            }

            NotifyStatusChanged("正在准备下载...", dispatcherQueue);
            NotifyDetailAdded($"开始下载流程，共 {buildDataList.Count} 个资源包", dispatcherQueue);

            Directory.CreateDirectory(_downloadRoot);
            File.WriteAllText(Path.Combine(_downloadRoot, DOWNLOAD_MARKER_FILE), DateTimeOffset.UtcNow.ToString("O"));

            // 1. 下载并解析所有 Manifest
            ManifestParser manifestParser = new ManifestParser();
            var allFileChunkInfos = new List<(FileChunkInfo fileInfo, BuildData buildData)>();

            foreach (var buildData in buildDataList)
            {
                if (_isCancelled || ct.IsCancellationRequested)
                {
                    NotifyStatusChanged("下载已取消", dispatcherQueue);
                    return false;
                }

                await _pauseTcs.Task;
                if (_isCancelled || ct.IsCancellationRequested) return false;

                string packageName = buildData.CategoryName ?? buildData.MatchingField ?? "未知";
                NotifyStatusChanged($"正在下载Manifest文件 ({packageName})...", dispatcherQueue);
                NotifyDetailAdded($"开始下载Manifest文件: {buildData.Manifest?.Id} ({packageName})", dispatcherQueue);

                byte[] manifestData = await _apiService.DownloadManifestAsync(
                    buildData.ManifestDownload?.UrlPrefix ?? string.Empty,
                    buildData.Manifest?.Id ?? string.Empty);

                if (_isCancelled || ct.IsCancellationRequested) return false;

                NotifyDetailAdded($"Manifest文件下载完成 ({packageName})，大小: {FormatFileSize(manifestData.Length)}",
                    dispatcherQueue);

                byte[] decompressedData = manifestParser.DecompressZstd(manifestData);
                manifestData = null!;
                NotifyDetailAdded($"Manifest文件解压缩完成 ({packageName})，大小: {FormatFileSize(decompressedData.Length)}",
                    dispatcherQueue);

                List<FileChunkInfo> fileChunkInfos = manifestParser.ParseChunkManifest(decompressedData);
                decompressedData = null!;

                foreach (var fileInfo in fileChunkInfos)
                {
                    allFileChunkInfos.Add((fileInfo, buildData));
                }

                NotifyDetailAdded($"Manifest解析完成 ({packageName})，共 {fileChunkInfos.Count} 个文件",
                    dispatcherQueue);
            }

            if (_isCancelled || ct.IsCancellationRequested)
            {
                NotifyStatusChanged("下载已取消", dispatcherQueue);
                return false;
            }

            int totalFiles = allFileChunkInfos.Count;
            int totalChunks = allFileChunkInfos.Sum(f => f.fileInfo.Chunks?.Count ?? 0);
            long totalSize = allFileChunkInfos.Sum(f => f.fileInfo.Chunks?.Sum(c => c.UncompressedSize) ?? 0);

            if (totalFiles == 0)
                throw new InvalidDataException("资源清单中没有可下载文件");

            NotifyDetailAdded(
                $"所有Manifest解析完成，共 {totalFiles} 个文件，{totalChunks} 个Chunk，总大小: {FormatFileSize(totalSize)}",
                dispatcherQueue);

            // 2. 校验已存在的文件 (Parallel.ForEachAsync 限制并发为4)
            _currentStage = DownloadStage.CheckingFiles;
            NotifyStatusChanged("正在检查已下载的文件...", dispatcherQueue);
            NotifyDetailAdded($"开始校验本地文件，共 {totalFiles} 个文件需要检查", dispatcherQueue);
            NotifyProgressChanged(0, dispatcherQueue);
            NotifyProgressTextChanged("0%", dispatcherQueue);

            var filesToDownload = new List<(FileChunkInfo fileInfo, BuildData buildData)>();
            int skippedFiles = 0;
            long skippedDownloadBytes = 0;
            long skippedWriteBytes = 0;
            int checkedFiles = 0;
            int validFiles = 0;

            await Parallel.ForEachAsync(allFileChunkInfos,
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                async (item, innerCt) =>
                {
                    var (fileChunkInfo, buildData) = item;
                    string outputPath = GetSafeOutputPath(_downloadRoot, fileChunkInfo.FilePath);
                    bool needDownload = true;
                    // 检查已经完成的目标文件，支持断点续传和重复安装。
                    if (File.Exists(outputPath))
                    {
                        try
                        {
                            string existingChecksum = await CalculateChecksumAsync(outputPath);
                            if (string.Equals(existingChecksum, fileChunkInfo.CheckSum,
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                needDownload = false;
                                Interlocked.Increment(ref validFiles);
                            }
                        }
                        catch (Exception ex)
                        {
                            LogService.Debug($"文件MD5校验失败，将重新处理 ({fileChunkInfo.FilePath}): {ex.Message}");
                        }
                    }

                    if (needDownload)
                    {
                        lock (filesToDownload)
                            filesToDownload.Add((fileChunkInfo, buildData));
                    }
                    else
                    {
                        Interlocked.Increment(ref skippedFiles);
                        long downloadBytes = fileChunkInfo.Chunks?.Sum(c => c.CompressedSize) ?? 0;
                        long writeBytes = fileChunkInfo.Chunks?.Sum(c => c.UncompressedSize) ?? 0;
                        Interlocked.Add(ref skippedDownloadBytes, downloadBytes);
                        Interlocked.Add(ref skippedWriteBytes, writeBytes);
                    }

                    int currentChecked = Interlocked.Increment(ref checkedFiles);
                    if (currentChecked % 100 == 0 || currentChecked == totalFiles)
                    {
                        int currentValid = validFiles;
                        double progress = (double)currentChecked / totalFiles * 100;
                        dispatcherQueue.TryEnqueue(() =>
                        {
                            StatusChanged?.Invoke(this,
                                $"正在校验文件... {currentChecked}/{totalFiles} ({progress:F1}%)");
                            ProgressChanged?.Invoke(this, progress);
                            ProgressTextChanged?.Invoke(this,
                                $"{progress:F1}% - 已复用: {currentValid}, 需处理: {currentChecked - currentValid}");
                        });
                    }
                });

            allFileChunkInfos = null!;

            NotifyDetailAdded($"文件校验完成: 已验证 {validFiles} 个, 需下载 {filesToDownload.Count} 个", dispatcherQueue);

            if (_isCancelled || ct.IsCancellationRequested)
            {
                NotifyStatusChanged("下载已取消", dispatcherQueue);
                return false;
            }

            if (skippedFiles > 0)
            {
                NotifyDetailAdded(
                    $"已跳过 {skippedFiles} 个已下载的文件 (下载节省: {FormatFileSize(skippedDownloadBytes)}, 写入节省: {FormatFileSize(skippedWriteBytes)})",
                    dispatcherQueue);
            }

            if (filesToDownload.Count == 0)
            {
                NotifyStatusChanged("所有文件已下载完成！", dispatcherQueue);
                NotifyDetailAdded("检测到所有文件已存在且校验通过，无需重新下载", dispatcherQueue);
                NotifyProgressChanged(100, dispatcherQueue);
                NotifyProgressTextChanged($"100% ({FormatFileSize(totalSize)}/{FormatFileSize(totalSize)})",
                    dispatcherQueue);
                completedSuccessfully = true;
                return true;
            }

            // 3. 计算总下载/写入量并开始文件级并行下载
            _totalDownloadBytes = filesToDownload.Sum(f =>
                f.fileInfo.Chunks?.Sum(c => c.CompressedSize) ?? 0);
            _totalWriteBytes = filesToDownload.Sum(f =>
                f.fileInfo.Chunks?.Sum(c => c.UncompressedSize) ?? 0);

            // 已验证文件计入总进度。
            _totalDownloadBytes += skippedDownloadBytes;
            _totalWriteBytes += skippedWriteBytes;
            _downloadedBytes = skippedDownloadBytes;
            _writtenBytes = skippedWriteBytes;
            _networkDownloadBytes = 0;
            _storageWriteBytes = 0;
            _lastDownloadedBytes = _downloadedBytes;
            _lastWrittenBytes = _writtenBytes;
            _lastSpeedTime = DateTime.Now;
            _lastProgressNotifyTime = DateTime.UtcNow;

            // 启动速度定时器
            _speedTimer = new System.Timers.Timer(1000);
            _speedTimer.Elapsed += OnSpeedTimerElapsed;
            _speedTimer.Start();

            long remainingDownloadBytes =
                filesToDownload.Sum(f => f.fileInfo.Chunks?.Sum(c => c.CompressedSize) ?? 0);
            long remainingWriteBytes = filesToDownload.Sum(f =>
                f.fileInfo.Chunks?.Sum(c => c.UncompressedSize) ?? 0);

            NotifyDetailAdded(
                $"需要下载 {filesToDownload.Count} 个文件，下载量: {FormatFileSize(remainingDownloadBytes)}，写入量: {FormatFileSize(remainingWriteBytes)}",
                dispatcherQueue);

            _currentStage = DownloadStage.Downloading;
            NotifyStatusChanged("正在下载文件...", dispatcherQueue);

            // 4. 文件级并行下载 (参考 Starward ExecuteInstallTaskDownloadModeChunkAsync)
            await Parallel.ForEachAsync(filesToDownload,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Min(8, Environment.ProcessorCount), // 上限 8，防止 CPU 过载
                    CancellationToken = ct
                },
                async (item, fileCt) =>
                {
                    // 暂停检查
                    await _pauseTcs.Task;
                    if (_isCancelled || ct.IsCancellationRequested) return;

                    // Polly 重试包装
                    await _retryPipeline.ExecuteAsync(
                        async token => await DownloadChunksToFileAsync(
                            item.buildData,
                            item.fileInfo,
                            token),
                        fileCt);
                });

            if (_isCancelled || ct.IsCancellationRequested)
            {
                NotifyStatusChanged("下载已取消", dispatcherQueue);
                return false;
            }

            NotifyStatusChanged("所有文件下载完成！", dispatcherQueue);
            NotifyDetailAdded($"所有文件下载和合并完成，新下载 {filesToDownload.Count} 个文件，跳过 {skippedFiles} 个已存在文件",
                dispatcherQueue);

            LogService.Info($"下载完成: 新下载={filesToDownload.Count}个文件, 跳过={skippedFiles}个文件");
            completedSuccessfully = true;
            return true;
        }
        catch (OperationCanceledException)
        {
            if (!_isCancelled)
            {
                LogService.Info("下载操作已取消");
                NotifyStatusChanged("下载已取消", dispatcherQueue);
            }
            return false;
        }
        catch (Exception ex)
        {
            if (!_isCancelled)
            {
                LogService.Error("下载失败", ex);
                NotifyStatusChanged($"下载失败: {ex.Message}", dispatcherQueue);
                NotifyDetailAdded($"错误: {ex.Message}", dispatcherQueue);
                downloadFailed = true;
            }
            else
            {
                LogService.Info("下载已被用户取消");
                NotifyStatusChanged("下载已取消", dispatcherQueue);
            }
            return false;
        }
        finally
        {
            lock (_downloadStateLock)
            {
                _isDownloading = false;
            }
            _isCancelled = false;
            if (_speedTimer != null)
            {
                _speedTimer.Stop();
                _speedTimer.Elapsed -= OnSpeedTimerElapsed;
                _speedTimer.Dispose();
                _speedTimer = null;
            }

            _globalCts?.Dispose();
            _globalCts = null;
            _isPaused = false;
            _currentStage = DownloadStage.Idle;

            if (completedSuccessfully)
            {
                try { File.Delete(Path.Combine(_downloadRoot, DOWNLOAD_MARKER_FILE)); }
                catch (Exception ex) { LogService.Debug($"清理下载标记失败: {ex.Message}"); }
            }

            _downloadRoot = _downloadPath;

            if (completedSuccessfully)
                DownloadCompleted?.Invoke(this, EventArgs.Empty);
            else if (downloadFailed)
                DownloadFailed?.Invoke(this, EventArgs.Empty);
        }
    }

    #endregion

    #region Chunk Download Pipeline

    /// <summary>
    /// 下载一个文件的所有 Chunk 并边下边合并到目标文件 (参考 Starward DownloadChunksToFileAsync)
    /// </summary>
    private async Task DownloadChunksToFileAsync(
        BuildData buildData,
        FileChunkInfo fileInfo,
        CancellationToken cancellationToken)
    {
        string urlPrefix = buildData.ChunkDownload?.UrlPrefix ?? string.Empty;
        string outputPath = GetSafeOutputPath(_downloadRoot, fileInfo.FilePath);
        string tmpPath = outputPath + "_tmp";

        long writeBytes = fileInfo.Chunks?.Sum(x => x.UncompressedSize) ?? 0;

        // 检查文件是否已完整存在。规划阶段已经把它计为“跳过”，这里仅作并发安全兜底，
        // 不再把整文件大小伪装成网络下载量。
        if (File.Exists(outputPath)
            && await CheckFileMD5Async(outputPath, writeBytes, fileInfo.CheckSum ?? "", cancellationToken))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var fileStreamOptions = new FileStreamOptions
        {
            Access = FileAccess.ReadWrite,
            Mode = FileMode.OpenOrCreate,
            Options = FileOptions.SequentialScan,
            Share = FileShare.ReadWrite | FileShare.Delete,
        };

        using FileStream fs = File.Open(tmpPath, fileStreamOptions);

        long downloadDelta = 0;
        long writeDelta = 0;

        try
        {
            // 优化: 复用 ApiService 共享连接池，不再每文件 new HttpClient
            using HttpClient httpClient = _apiService.CreateDownloadClient();

            // 优化: 从 ArrayPool 租用下载缓冲区，chunk 循环中复用
            byte[] netBuffer = ArrayPool<byte>.Shared.Rent(BUFFER_SIZE);
            try
            {
            foreach (var chunk in fileInfo.Chunks ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _pauseTcs.Task;

                long offset = chunk.Offset;
                long uncompressedSize = chunk.UncompressedSize;
                long compressedSize = chunk.CompressedSize;

                fs.Position = offset;

                // 下载 + 解压 + 写入流水线
                string url = $"{urlPrefix.TrimEnd('/')}/{chunk.ChunkId}";
                HttpRequestMessage request = new(HttpMethod.Get, url)
                {
                    VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
                };

                using HttpResponseMessage response = await httpClient.SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();

                using Stream httpStream = await response.Content.ReadAsStreamAsync(cancellationToken);

                Pipe pipe = new();
                using var decompressor = new Zstandard.Net.ZstandardStream(
                    pipe.Reader.AsStream(),
                    System.IO.Compression.CompressionMode.Decompress,
                    leaveOpen: true);

                long lastFsPosition = fs.Position;

                // 启动解压→写入任务
                Task writeFileTask = decompressor.CopyToAsync(fs, cancellationToken);

                int read;
                while ((read = await httpStream.ReadAsync(
                    netBuffer.AsMemory(0, BUFFER_SIZE), cancellationToken)) > 0)
                {
                    // 速率限制
                    RateLimitLease lease = await _rateLimiter.AcquireAsync(read, cancellationToken);
                    while (!lease.IsAcquired)
                    {
                        await Task.Delay(1, cancellationToken);
                        lease = await _rateLimiter.AcquireAsync(read, cancellationToken);
                    }

                    await pipe.Writer.WriteAsync(netBuffer.AsMemory(0, read), cancellationToken);

                    Interlocked.Add(ref _downloadedBytes, read);
                    Interlocked.Add(ref _networkDownloadBytes, read);
                    downloadDelta += read;

                    long p = fs.Position;
                    long add = p - lastFsPosition;
                    Interlocked.Add(ref _writtenBytes, add);
                    Interlocked.Add(ref _storageWriteBytes, add);
                    writeDelta += add;
                    lastFsPosition = p;
                }

                await pipe.Writer.CompleteAsync();
                await writeFileTask;

                long remainWrite = fs.Position - lastFsPosition;
                Interlocked.Add(ref _writtenBytes, remainWrite);
                Interlocked.Add(ref _storageWriteBytes, remainWrite);
                writeDelta += remainWrite;
            }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(netBuffer);
            }
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            // 超时：回滚本文件的进度
            Interlocked.Add(ref _downloadedBytes, -downloadDelta);
            Interlocked.Add(ref _writtenBytes, -writeDelta);
            throw ex.InnerException;
        }
        catch (Exception ex)
        {
            // 出错：回滚本文件的进度并记录日志
            Interlocked.Add(ref _downloadedBytes, -downloadDelta);
            Interlocked.Add(ref _writtenBytes, -writeDelta);
            LogService.Error($"下载Chunk失败 (文件: {fileInfo.FilePath}): {ex.Message}", ex);
            throw;
        }

        await fs.DisposeAsync();

        // MD5 校验合并后的文件
        if (await CheckFileMD5Async(tmpPath, writeBytes, fileInfo.CheckSum ?? "", cancellationToken))
        {
            File.Move(tmpPath, outputPath, true);
        }
        else
        {
            File.Delete(tmpPath);
            Interlocked.Add(ref _downloadedBytes, -downloadDelta);
            Interlocked.Add(ref _writtenBytes, -writeDelta);
            throw new InvalidDataException(
                $"文件MD5校验失败: {fileInfo.FilePath}, 期望: {fileInfo.CheckSum}");
        }
    }

    #endregion

    #region MD5 / Checksum

    private static string GetSafeOutputPath(string root, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidDataException($"资源清单包含无效路径: {relativePath}");

        string fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"资源清单路径越界: {relativePath}");

        return fullPath;
    }

    /// <summary>
    /// 检查文件 MD5 (参考 Starward CheckFileMD5Async)
    /// </summary>
    private async Task<bool> CheckFileMD5Async(string path, long expectedSize, string expectedMd5,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return false;
        if (new FileInfo(path).Length != expectedSize) return false;

        var fsOptions = new FileStreamOptions
        {
            Access = FileAccess.Read,
            BufferSize = MD5_BUFFER_SIZE,
            Mode = FileMode.Open,
            Options = FileOptions.SequentialScan,
            Share = FileShare.ReadWrite | FileShare.Delete,
        };

        using FileStream fs = File.Open(path, fsOptions);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(MD5_BUFFER_SIZE);
        try
        {
            using MD5 md5Hash = MD5.Create();
            int read;
            while ((read = await fs.ReadAsync(buffer.AsMemory(0, MD5_BUFFER_SIZE), cancellationToken)) > 0)
            {
                md5Hash.TransformBlock(buffer, 0, read, null, 0);
            }
            md5Hash.TransformFinalBlock(buffer, 0, 0);
            if (md5Hash.Hash is null) return false;
            return string.Equals(expectedMd5, Convert.ToHexStringLower(md5Hash.Hash), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<string> CalculateChecksumAsync(string filePath)
    {
        var fsOptions = new FileStreamOptions
        {
            Access = FileAccess.Read,
            BufferSize = MD5_BUFFER_SIZE,
            Mode = FileMode.Open,
            Options = FileOptions.SequentialScan,
            Share = FileShare.ReadWrite | FileShare.Delete,
        };

        using FileStream fs = File.Open(filePath, fsOptions);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(MD5_BUFFER_SIZE);
        try
        {
            using MD5 md5Hash = MD5.Create();
            int read;
            while ((read = await fs.ReadAsync(buffer.AsMemory(0, MD5_BUFFER_SIZE))) > 0)
            {
                md5Hash.TransformBlock(buffer, 0, read, null, 0);
            }
            md5Hash.TransformFinalBlock(buffer, 0, 0);
            if (md5Hash.Hash is null) return string.Empty;
            return Convert.ToHexStringLower(md5Hash.Hash);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    #endregion

    #region Speed Timer

    private void OnSpeedTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        var now = DateTime.Now;
        var elapsed = now - _lastSpeedTime;
        if (elapsed.TotalSeconds < 0.5) return;

        // 节流: 每 200ms 最多触发一次 UI 更新
        if ((now - _lastProgressNotifyTime).TotalMilliseconds < 200) return;
        _lastProgressNotifyTime = now;

        long downloaded = Interlocked.Read(ref _downloadedBytes);
        long written = Interlocked.Read(ref _writtenBytes);

        long downloadDelta = downloaded - _lastDownloadedBytes;
        long writeDelta = written - _lastWrittenBytes;

        double downloadSpeedBytesPerSec = downloadDelta / elapsed.TotalSeconds;
        double writeSpeedBytesPerSec = writeDelta / elapsed.TotalSeconds;

        _lastDownloadedBytes = downloaded;
        _lastWrittenBytes = written;
        _lastSpeedTime = now;

        double speedMbps = downloadSpeedBytesPerSec / (1024 * 1024);
        double writeSpeedMbps = writeSpeedBytesPerSec / (1024 * 1024);

        long remainingDownload = _totalDownloadBytes - downloaded;
        TimeSpan remaining = downloadSpeedBytesPerSec > 0
            ? TimeSpan.FromSeconds(remainingDownload / downloadSpeedBytesPerSec)
            : TimeSpan.MaxValue;

        double progress = _totalDownloadBytes > 0 ? (double)downloaded / _totalDownloadBytes * 100 : 0;
        string progressText = $"{progress:F1}% ({FormatFileSize(downloaded)}/{FormatFileSize(_totalDownloadBytes)})";

        // 优化: 所有 UI 更新通过 DispatcherQueue 安全执行
        _uiDispatcher?.TryEnqueue(() =>
        {
            SpeedUpdated?.Invoke(this, (speedMbps, writeSpeedMbps, remaining));
            ProgressChanged?.Invoke(this, Math.Min(progress, 100));
            ProgressTextChanged?.Invoke(this, progressText);
        });
    }

    #endregion

    #region Pause / Resume / Cancel

    public void PauseDownload()
    {
        if (!_isDownloading || _isPaused) return;

        _stageBeforePause = _currentStage;
        _isPaused = true;
        _currentStage = DownloadStage.Paused;
        _pauseTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SpeedUpdated?.Invoke(this, (0, 0, TimeSpan.MaxValue));
        StatusChanged?.Invoke(this, Lang.DownloadPage_Paused);
    }

    public void ResumeDownload()
    {
        if (!_isPaused) return;

        _isPaused = false;
        _currentStage = _stageBeforePause is DownloadStage.Idle or DownloadStage.Paused
            ? DownloadStage.Downloading
            : _stageBeforePause;
        _pauseTcs.TrySetResult();
        StatusChanged?.Invoke(this, Lang.DownloadPage_Downloading);
    }

    /// <summary>
    /// 取消下载 (区别于暂停，取消后不能继续)
    /// </summary>
    public void CancelDownload()
    {
        _isCancelled = true;
        _globalCts?.Cancel();
        ResumeDownload(); // 让等待中的暂停闸门释放
    }

    /// <summary>
    /// 检测下载目录是否存在未完成的下载
    /// </summary>
    public bool HasPartialDownload(string downloadPath)
    {
        if (!Directory.Exists(downloadPath)) return false;

        try
        {
            if (File.Exists(Path.Combine(downloadPath, DOWNLOAD_MARKER_FILE)))
                return true;

            // 兼容引入下载标记前已经产生的断点文件。
            return Directory.EnumerateFiles(downloadPath, "*_tmp", SearchOption.AllDirectories).Any();
        }
        catch (Exception ex)
        {
            LogService.Debug($"检查断点下载失败: {ex.Message}");
            return false;
        }
    }

    #endregion

    #region Helpers

    private string FormatFileSize(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        int suffixIndex = 0;
        double size = bytes;

        while (size >= 1024 && suffixIndex < suffixes.Length - 1)
        {
            size /= 1024;
            suffixIndex++;
        }

        return $"{size:0.00} {suffixes[suffixIndex]}";
    }

    private void NotifyStatusChanged(string status, DispatcherQueue dispatcherQueue)
    {
        dispatcherQueue.TryEnqueue(() => { StatusChanged?.Invoke(this, status); });
    }

    private void NotifyProgressChanged(double progress, DispatcherQueue dispatcherQueue)
    {
        dispatcherQueue.TryEnqueue(() => { ProgressChanged?.Invoke(this, progress); });
    }

    private void NotifyProgressTextChanged(string progressText, DispatcherQueue dispatcherQueue)
    {
        dispatcherQueue.TryEnqueue(() => { ProgressTextChanged?.Invoke(this, progressText); });
    }

    private void NotifyDetailAdded(string detail, DispatcherQueue dispatcherQueue)
    {
        dispatcherQueue.TryEnqueue(() => { DetailAdded?.Invoke(this, detail); });
    }

    #endregion
}
