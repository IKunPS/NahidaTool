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
    #region Pointer Events

    private void StartGameGrid_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _isPointerOverCapsule = true;
        if (_gameState is GameState.GameIsRunning)
        {
            Popup_GameInfo.IsOpen = true;
        }
        else if (_gameState is GameState.Downloading)
        {
            // 悬停立即弹出下载信息气泡（版本/速度，无进度条）
            DownloadInfoTip.IsOpen = true;
        }
        UpdateButtonForeground();
    }

    private void StartGameGrid_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _isPointerOverCapsule = false;
        _isActionButtonPointerOver = false;
        _isSettingButtonPointerOver = false;
        UpdateDownloadHoverState(false);
        Popup_GameInfo.IsOpen = false;
        DownloadInfoTip.IsOpen = false;
        UpdateButtonForeground();
    }

    private void StartGameButton_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _isActionButtonPointerOver = true;
        UpdateDownloadHoverState(true);
        UpdateButtonForeground();
    }

    private void StartGameButton_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _isActionButtonPointerOver = false;
        UpdateDownloadHoverState(false);
        UpdateButtonForeground();
    }

    private void SettingsButton_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _isSettingButtonPointerOver = true;
        UpdateButtonForeground();
    }

    private void SettingsButton_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _isSettingButtonPointerOver = false;
        UpdateButtonForeground();
    }

    /// <summary>
    /// 构建下载气泡标题（版本号不带 v）
    /// </summary>
    private void UpdateDownloadHoverState(bool pointerOver)
    {
        var downloadService = DownloadService.Instance;
        bool showHoverAction = pointerOver && downloadService.IsDownloading && !downloadService.IsPaused;
        DownloadStatePanel.Visibility = showHoverAction ? Visibility.Collapsed : Visibility.Visible;
        DownloadHoverActionText.Visibility = showHoverAction ? Visibility.Visible : Visibility.Collapsed;
        DownloadHoverActionText.Text = downloadService.IsPaused
            ? Lang.DownloadPage_ContinueDownload
            : Lang.DownloadPage_PauseDownload;
    }

    #endregion

    #region Proxy Input

    private void OnProxySettingChanged()
    {
        _lastSettings = AppSettings.Load();
        DispatcherQueue.TryEnqueue(RefreshProxyInput);
    }

    private void RefreshProxyInput()
    {
        bool showProxy = _lastSettings.EnableProxy;
        ProxyCard.Visibility = showProxy ? Visibility.Visible : Visibility.Collapsed;

        if (showProxy)
        {
            ProxyStatusText.Text = Lang.HomePage_ProxyNotStarted;
            bool kunMode = KunServerConfig.IsEnabled(_lastSettings);
            ProxyAddressLabel.Text = kunMode ? Lang.HomePage_KunServerLabel : Lang.HomePage_ProxyAddress;
            ProxyAddressTextBox.Visibility = kunMode ? Visibility.Collapsed : Visibility.Visible;
            KunServerComboBox.Visibility = kunMode ? Visibility.Visible : Visibility.Collapsed;
            ProxyErrorText.Visibility = Visibility.Collapsed;

            _isUpdatingProxyInput = true;
            try
            {
                if (kunMode)
                {
                    KunServerComboBox.SelectedIndex = _lastSettings.KunServer switch
                    {
                        KunServerType.Story => 0,
                        KunServerType.Command => 1,
                        _ => -1
                    };
                }
                else
                {
                    ProxyAddressTextBox.Text = _lastSettings.ProxyAddress;
                }
            }
            finally
            {
                _isUpdatingProxyInput = false;
            }
        }
    }

    private bool _suppressProxyError;
    private bool _isUpdatingProxyInput;

    private void KunServerComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingProxyInput || !KunServerConfig.IsEnabled(_lastSettings) || KunServerComboBox.SelectedIndex is not (0 or 1))
            return;

        KunServerType server = KunServerComboBox.SelectedIndex switch
        {
            0 => KunServerType.Story,
            _ => KunServerType.Command
        };
        _lastSettings = AppSettings.Update(settings => settings.KunServer = server);
    }

    private void ProxyAddressTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdatingProxyInput || KunServerConfig.IsEnabled(_lastSettings))
            return;
        string proxyAddress = ProxyAddressTextBox.Text;
        _lastSettings = AppSettings.Update(settings => settings.ProxyAddress = proxyAddress);

        // 输入时隐藏错误提示，失焦时再校验
        if (!_suppressProxyError && ProxyErrorText.Visibility == Visibility.Visible)
            ProxyErrorText.Visibility = Visibility.Collapsed;
    }

    private async void ProxyAddressTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (KunServerConfig.IsEnabled(_lastSettings))
            return;
        var text = ProxyAddressTextBox.Text?.Trim() ?? string.Empty;

        // 空内容不校验
        if (string.IsNullOrEmpty(text))
        {
            ProxyErrorText.Visibility = Visibility.Collapsed;
            return;
        }

        if (!text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            && !text.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            ProxyErrorText.Visibility = Visibility.Visible;

            _suppressProxyError = true;
            await new NoticeDialog(Lang.HomePage_UrlFormatError, Lang.HomePage_ProxyUrlFormatContent, warning: true)
            {
                CloseButtonText = Lang.HomePage_OK,
                XamlRoot = XamlRoot
            }.ShowAsync();
            _suppressProxyError = false;
        }
        else
        {
            ProxyErrorText.Visibility = Visibility.Collapsed;
        }
    }

    #endregion

    #region Game Process Monitoring

    private void CheckGameRunning()
    {
        try
        {
            _gameProcess = GameLauncherService.GetRunningProcess(_currentRegion);
            if (_gameProcess != null)
            {
                UpdateGameState(GameState.GameIsRunning);
                StartMonitoringGameProcess();
            }
        }
        catch (Exception ex)
        {
            LogService.Error("检查游戏运行状态失败", ex);
        }
    }

    private void StartMonitoringGameProcess()
    {
        // 优化: 使用 Process.Exited 事件替代每秒轮询
        StopMonitoringGameProcess();

        if (_gameProcess == null) return;

        _gameProcess.EnableRaisingEvents = true;
        _gameProcess.Exited += OnGameProcessExited;
    }

    private void StopMonitoringGameProcess()
    {
        if (_gameProcess != null)
        {
            _gameProcess.EnableRaisingEvents = false;
            _gameProcess.Exited -= OnGameProcessExited;
        }
    }

    private void OnGameProcessExited(object? sender, EventArgs e)
    {
        var process = _gameProcess;
        ProxyService.Stop();

        try { process?.Dispose(); } catch (Exception ex) { LogService.Debug($"释放游戏进程失败: {ex.Message}"); }
        _gameProcess = null;

        try { RsaService.CleanupRsaFromGameDirectory(_lastSettings.GameInstallPath); } catch (Exception ex) { LogService.Debug($"清理RSA DLL失败: {ex.Message}"); }

        DispatcherQueue.TryEnqueue(() =>
        {
            ProxyStatusText.Text = Lang.HomePage_ProxyNotStarted;
            RefreshGameStatus();
            if (App.MainWindow is MainWindow mw)
                mw.Show();
        });
    }

    #endregion

}
