using System;
using System.Threading;
using System.Threading.Tasks;
using NahidaTool.Models.Config;
using NahidaTool.Models.Enum;
using NahidaTool.Models.Event;

namespace NahidaTool.Models.Service;

public partial class ServerSwitchService
{
    private readonly object _previewLock = new();
    private bool _preloadStarted;
    private CancellationTokenSource? _preloadCts;
    private CancellationTokenSource? _previewCts;
    private (ServerRegionType Region, string Version)? _previewKey;
    private Task<long>? _previewTask;
    private DateTime _previewCreatedUtc;
    private static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(5);

    internal void StartDownloadSizePreload()
    {
        lock (_previewLock)
        {
            if (_preloadStarted) return;
            _preloadStarted = true;
            GameInstallPathChangedMessage.PathChanged += OnPreviewInstallationChanged;
        }
        QueueDownloadSizePreload();
    }

    internal void StopDownloadSizePreload()
    {
        lock (_previewLock)
        {
            _preloadStarted = false;
            GameInstallPathChangedMessage.PathChanged -= OnPreviewInstallationChanged;
            _preloadCts?.Cancel();
            InvalidateDownloadSizePreview();
        }
    }

    private void OnPreviewInstallationChanged()
    {
        InvalidateDownloadSizePreview();
        QueueDownloadSizePreload();
    }

    private void QueueDownloadSizePreload()
    {
        lock (_previewLock)
        {
            if (!_preloadStarted) return;
            _preloadCts?.Cancel();
            var request = new CancellationTokenSource();
            _preloadCts = request;
            // 配置读取、磁盘检测和清单处理都不占用首屏 UI 线程。
            _ = Task.Run(() => PreloadDownloadSizeAsync(request));
        }
    }

    private async Task PreloadDownloadSizeAsync(CancellationTokenSource request)
    {
        try
        {
            request.Token.ThrowIfCancellationRequested();
            string path = AppSettings.Load().GameInstallPath?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(path)) return;
            using var detection = CancellationTokenSource.CreateLinkedTokenSource(request.Token);
            detection.CancelAfter(TimeSpan.FromSeconds(5));
            CancellationToken token = detection.Token;
            var installed = await Task.Run(() => GameInstallationReader.ReadAsync(path, token), token)
                .WaitAsync(token).ConfigureAwait(false);
            request.Token.ThrowIfCancellationRequested();
            var target = installed.Region == ServerRegionType.CN ? ServerRegionType.OS : ServerRegionType.CN;
            await GetRequiredDownloadSizeAsync(target, installed.Version, request.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (GameInstallationDetectionException ex)
        {
            LogService.Debug($"转服资源预加载未开始: {ex.Issue}");
        }
        catch (Exception ex)
        {
            LogService.Warn($"转服资源预加载未完成: {ex.GetType().Name}");
        }
        finally
        {
            lock (_previewLock)
            {
                if (ReferenceEquals(_preloadCts, request)) _preloadCts = null;
                request.Dispose();
            }
        }
    }

    internal Task<long> GetRequiredDownloadSizeAsync(ServerRegionType targetRegion, string version,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_previewLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = (targetRegion, version);
            if (_previewKey != key || _previewTask == null || _previewTask.IsCanceled || _previewTask.IsFaulted ||
                DateTime.UtcNow - _previewCreatedUtc >= PreviewLifetime)
            {
                _previewCts?.Cancel();
                var request = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                _previewCts = request;
                _previewKey = key;
                _previewCreatedUtc = DateTime.UtcNow;
                _previewTask = Task.Run(() => CalculateDownloadSizePreviewAsync(targetRegion, version, request));
                _ = ObserveDownloadSizePreviewAsync(_previewTask);
            }
            // 页面离开只取消自己的等待，不能终止启动时已开始的共享计算。
            return _previewTask.WaitAsync(cancellationToken);
        }
    }

    internal void InvalidateDownloadSizePreview()
    {
        lock (_previewLock)
        {
            _previewCts?.Cancel();
            _previewKey = null;
            _previewTask = null;
        }
    }

    private async Task<long> CalculateDownloadSizePreviewAsync(ServerRegionType targetRegion, string version,
        CancellationTokenSource request)
    {
        try
        {
            return await CalculateRequiredDownloadSizeAsync(targetRegion, version, request.Token)
                .WaitAsync(request.Token).ConfigureAwait(false);
        }
        finally
        {
            // 取消与释放使用同一把锁，避免路径切换与任务结束并发访问已释放的 CTS。
            lock (_previewLock)
            {
                if (ReferenceEquals(_previewCts, request)) _previewCts = null;
                request.Dispose();
            }
        }
    }

    private static async Task ObserveDownloadSizePreviewAsync(Task<long> calculation)
    {
        try { await calculation.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LogService.Warn($"计算转服下载大小失败: {ex.GetType().Name}"); }
    }
}
