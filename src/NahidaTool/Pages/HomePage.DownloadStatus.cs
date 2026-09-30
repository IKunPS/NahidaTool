using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

using Windows.UI;
using NahidaTool.Models;
using NahidaTool.Models.Config;
using NahidaTool.Models.Enum;
using NahidaTool.Models.Event;
using NahidaTool.Models.Helper;
using NahidaTool.Models.Service;

namespace NahidaTool.Pages;

public sealed partial class HomePage
{
    #region Download Status
    private bool _isPointerOverCapsule;
    private bool _isActionButtonPointerOver;
    private bool _isSettingButtonPointerOver;

    /// <summary>
    /// 下载开始时弹出气泡提示版本（无进度条）
    /// </summary>
    private void ShowDownloadStartTip()
    {
        if (_selectedBuildData == null) return;

        DownloadPopupStateText.Text = Lang.DownloadPage_Downloading;
        DownloadTipProgressText.Text = "0%";
        DownloadPopupRemainTimeText.Text = "--:--:--";
        DownloadPopupBytesText.Text = string.Empty;
        DownloadPopupSpeedText.Text = string.Empty;
        DownloadInfoTip.IsOpen = true;

        // 几秒后自动关闭，避免遮挡
        _ = HideTipAfterDelayAsync();
    }

    private async Task HideTipAfterDelayAsync()
    {
        await Task.Delay(3000);
        // 鼠标仍悬停在胶囊上时不关闭（悬停显示由 PointerExited 管理）
        if (!_isPointerOverCapsule)
            DownloadInfoTip.IsOpen = false;
    }

    /// <summary>
    /// 更新胶囊按钮状态：下载中显示进度圆环，暂停时文字提示
    /// </summary>
    private void UpdateDownloadButtonStates()
    {
        var ds = DownloadService.Instance;
        bool active = ds.IsDownloading || _isPreparingDownload;
        bool paused = ds.IsDownloading && ds.IsPaused;

        // 下载中：显示圆环，文字随暂停状态变化
        NormalActionGrid.Visibility = !active || paused ? Visibility.Visible : Visibility.Collapsed;
        DownloadActionGrid.Visibility = active && !paused ? Visibility.Visible : Visibility.Collapsed;
        SettingsButton.Visibility = Visibility.Visible;
        if (active)
        {
            StartGameButton.IsEnabled = ds.IsDownloading;
            Rect_AccentBackground.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
            StartGameButtonText.Text = paused
                ? Lang.DownloadPage_ContinueDownload
                : string.Empty;
            string stageText = _isPreparingDownload ? Lang.DownloadPage_Preparing : GetDownloadStageText(ds);
            DownloadStateText.Text = stageText;
            DownloadPopupStateText.Text = stageText;
            UpdateDownloadHoverState(_isActionButtonPointerOver);
            if (!paused && string.IsNullOrEmpty(DownloadEtaText.Text))
                DownloadEtaText.Text = "--:--:--";
        }
        else
        {
            DownloadPercentText.Text = "0";
            DownloadProgressRing.Value = 0;
            DownloadEtaText.Text = "--:--:--";
            DownloadStatePanel.Visibility = Visibility.Visible;
            DownloadHoverActionText.Visibility = Visibility.Collapsed;
            StartGameButton.IsEnabled = _gameState is not GameState.GameIsRunning;
        }

        UpdateButtonForeground();
    }

    private void DownloadService_StatusChanged(object? sender, string status)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateDownloadButtonStates();
            var downloadService = DownloadService.Instance;
            string stageText = GetDownloadStageText(downloadService);
            DownloadStateText.Text = stageText;
            DownloadPopupStateText.Text = stageText;
        });
        // 状态文本已并入悬停气泡，无需额外处理
    }

    private void DownloadService_ProgressChanged(object? sender, double progress)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            DownloadProgressRing.Value = progress;
            DownloadPercentText.Text = $"{progress:F0}";
            DownloadTipProgressText.Text = $"{progress:F1}%";
        });
    }

    private void DownloadService_ProgressTextChanged(object? sender, string text)
    {
        DispatcherQueue.TryEnqueue(() => DownloadPopupBytesText.Text = ExtractDownloadBytesText(text));
        // 进度数字已显示在圆环内，忽略面板文本
    }

    private void DownloadService_DownloadCompleted(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            DownloadInfoTip.IsOpen = false;
            UpdateDownloadButtonStates();
            RefreshGameStatus();
        });
    }

    private void DownloadService_DownloadFailed(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            DownloadInfoTip.IsOpen = false;
            UpdateDownloadButtonStates();
            RefreshGameStatus();
        });
    }

    private void DownloadService_SpeedUpdated(object? sender, (double speedMbps, double writeSpeedMbps, TimeSpan remaining) e)
    {
        // 悬停气泡内显示速度与剩余时间（无进度条）
        DispatcherQueue.TryEnqueue(() =>
        {
            string remaining = e.remaining == TimeSpan.MaxValue
                ? "--:--:--"
                : e.remaining.ToString(@"hh\:mm\:ss");
            DownloadEtaText.Text = remaining;
            DownloadPopupRemainTimeText.Text = remaining;
            DownloadPopupSpeedText.Text = e.speedMbps >= 1
                ? $"{e.speedMbps:F2} MB/s"
                : $"{e.speedMbps * 1024:F2} KB/s";
        });
    }

    private static string ExtractDownloadBytesText(string text)
    {
        int start = text.IndexOf('(');
        int end = text.LastIndexOf(')');
        return start >= 0 && end > start ? text[(start + 1)..end] : text;
    }

    #endregion
}
