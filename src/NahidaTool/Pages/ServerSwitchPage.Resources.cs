using System;
using System.Threading;
using System.Threading.Tasks;
using NahidaTool.Models;
using NahidaTool.Models.Enum;
using NahidaTool.Models.Service;

namespace NahidaTool.Pages;

public sealed partial class ServerSwitchPage
{
    private CancellationTokenSource? _downloadSizeCts;
    private Task? _downloadSizeTask;
    private long? _requiredDownloadBytes;
    private bool _isCalculatingDownloadSize;
    private bool _downloadSizeFailed;

    private void CancelDownloadSizeCalculation()
    {
        _downloadSizeCts?.Cancel();
        _downloadSizeCts = null;
        _downloadSizeTask = null;
        _requiredDownloadBytes = null;
        _isCalculatingDownloadSize = false;
        _downloadSizeFailed = false;
    }

    private async Task CalculateDownloadSizeAsync(string path, InstalledGameInfo installed)
    {
        var request = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _downloadSizeCts = request;
        _isCalculatingDownloadSize = true;
        RefreshDownloadSizeDisplay();
        bool IsCurrent() => _isActive && ReferenceEquals(_downloadSizeCts, request) &&
            _installation == installed && string.Equals(path, _gamePath, StringComparison.OrdinalIgnoreCase);
        try
        {
            var target = installed.Region == ServerRegionType.CN ? ServerRegionType.OS : ServerRegionType.CN;
            CancellationToken token = request.Token;
            long bytes = await Task.Run(() =>
                _switchService.GetRequiredDownloadSizeAsync(target, installed.Version, token), token).WaitAsync(token);
            if (IsCurrent())
                _requiredDownloadBytes = bytes;
        }
        catch (OperationCanceledException)
        {
            if (IsCurrent())
                _downloadSizeFailed = true;
        }
        catch (Exception ex)
        {
            if (IsCurrent())
                _downloadSizeFailed = true;
            LogService.Warn($"计算转服下载大小失败: {ex.GetType().Name}");
        }
        finally
        {
            if (IsCurrent())
            {
                _downloadSizeCts = null;
                _isCalculatingDownloadSize = false;
                RefreshDownloadSizeDisplay();
            }
            request.Dispose();
        }
    }

    private void RefreshDownloadSizeDisplay() => DownloadSizeText.Text = GetDownloadSizeDescription();

    private string GetDownloadSizeDescription()
    {
        if (_isCalculatingDownloadSize)
            return Lang.ServerSwitchPage_DownloadSizeCalculating;
        if (_requiredDownloadBytes is 0)
            return Lang.ServerSwitchPage_DownloadSizeCached;
        if (_requiredDownloadBytes is { } bytes)
            return string.Format(Lang.ServerSwitchPage_DownloadSize, FormatDownloadSize(bytes));
        return _downloadSizeFailed ? Lang.ServerSwitchPage_DownloadSizeUnavailable
            : Lang.ServerSwitchPage_DownloadSizePending;
    }

    private static string FormatDownloadSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }
}
