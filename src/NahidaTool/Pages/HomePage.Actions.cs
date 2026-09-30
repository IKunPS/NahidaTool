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
    #region Button Events

    private async void StartGameButton_Click(object sender, RoutedEventArgs e)
    {
        // 未安装客户端：弹窗让用户选择下载版本或定位客户端
        // 下载中：点击切换暂停/恢复
        if (_gameState is GameState.Downloading)
        {
            var ds = DownloadService.Instance;
            if (!ds.IsDownloading)
            {
                // 下载已结束但状态未恢复（如下载失败后未及时刷新），先恢复按钮状态
                RefreshGameStatus();
                return;
            }

            if (ds.IsPaused)
                ds.ResumeDownload();
            else
                ds.PauseDownload();
            UpdateDownloadButtonStates();
            return;
        }

        if (_foundGamePath == null)
        {
            if (HasPartialClientDownload())
            {
                await DownloadClientAsync(_lastSettings.GameVersion, _lastSettings.VoiceLanguage);
                return;
            }

            await ShowNoClientDialogAsync();
            return;
        }

        try
        {
            bool kunMode = KunServerConfig.IsEnabled(_lastSettings);
            if (kunMode && !System.Enum.IsDefined(_lastSettings.KunServer))
                throw new InvalidOperationException(Lang.HomePage_KunServerInvalid);

            // 启用了代理时，校验地址格式
            if (_lastSettings.EnableProxy && !kunMode)
            {
                var addr = _lastSettings.ProxyAddress?.Trim() ?? string.Empty;
                if (!string.IsNullOrEmpty(addr)
                    && !addr.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    && !addr.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                {
                    ProxyErrorText.Visibility = Visibility.Visible;
                    await new NoticeDialog(Lang.HomePage_UrlFormatError, Lang.HomePage_ProxyUrlFormatStartMessage, warning: true)
                    {
                        CloseButtonText = Lang.HomePage_OK,
                        XamlRoot = XamlRoot
                    }.ShowAsync();
                    return;
                }
            }

            StartGameButton.IsEnabled = false;
            Rect_AccentBackground.Visibility = Visibility.Collapsed;
            UpdateButtonForeground();

            if (_lastSettings.EnableProxy)
            {
                var started = await ProxyService.StartAsync();
                if (started)
                    ProxyStatusText.Text = Lang.HomePage_ProxyRunning;
                else if (kunMode)
                    throw new InvalidOperationException(Lang.HomePage_KunProxyFailed);
            }

            if (kunMode)
                ProxyStatusText.Text = Lang.HomePage_KunCheckingVersion;
            var process = await GameLauncherService.StartGameAsync(_currentRegion);

            if (process != null)
            {
                if (kunMode)
                    ProxyStatusText.Text = Lang.HomePage_ProxyRunning;
                _gameProcess = process;
                UpdateGameState(GameState.GameIsRunning);
                StartMonitoringGameProcess();

                // 启动游戏后操作
                ApplyAfterStartAction();
            }
            else
            {
                ProxyService.Stop();
                ProxyStatusText.Text = Lang.HomePage_ProxyNotStarted;
                RefreshGameStatus();
            }
        }
        catch (KunVersionMismatchException ex)
        {
            LogService.Warn($"Kun 版本不匹配: 客户端={ex.ClientVersion}, 服务器={ex.ServerVersion}");
            ProxyService.Stop();
            ProxyStatusText.Text = Lang.HomePage_ProxyNotStarted;
            RefreshGameStatus();

            var dialog = new KunCompatibilityDialog(
                KunCompatibilityIssue.Version, ex.ClientVersion, ex.ServerVersion)
            {
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                KunServerComboBox.Focus(FocusState.Programmatic);
                KunServerComboBox.IsDropDownOpen = true;
            }
        }
        catch (KunClientRegionException ex)
        {
            LogService.Warn($"Kun 客户端服区不匹配: {ex.DetectedRegion?.ToString() ?? "Unknown"}");
            ProxyService.Stop();
            ProxyStatusText.Text = Lang.HomePage_ProxyNotStarted;
            RefreshGameStatus();

            string currentRegion = ex.DetectedRegion == ServerRegionType.CN
                ? Lang.ServerSwitchPage_CN
                : Lang.DownloadPage_UnknownVersion;
            var dialog = new KunCompatibilityDialog(
                KunCompatibilityIssue.Region, currentRegion, Lang.ServerSwitchPage_OS)
            {
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && App.MainWindow is MainWindow mainWindow)
                mainWindow.NavigateToServerSwitch();
        }
        catch (InvalidOperationException ex)
        {
            LogService.Warn($"启动游戏失败(操作异常): {ex.Message}");
            ProxyService.Stop();
            ProxyStatusText.Text = Lang.HomePage_ProxyNotStarted;
            RefreshGameStatus();
            await new LaunchErrorDialog(ex.Message)
            {
                XamlRoot = XamlRoot
            }.ShowAsync();
        }
        catch (FileNotFoundException ex)
        {
            LogService.Warn($"启动游戏失败(文件未找到): {ex.Message}");
            ProxyService.Stop();
            ProxyStatusText.Text = Lang.HomePage_ProxyNotStarted;
            _foundGamePath = null;
            RefreshGameStatus();
        }
        catch (Exception ex)
        {
            LogService.Error("启动游戏失败", ex);
            ProxyService.Stop();
            ProxyStatusText.Text = Lang.HomePage_ProxyNotStarted;
            RefreshGameStatus();
        }
    }

    private async Task DoLocateGameAsync()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            var folderPath = await FolderPickerHelper.PickFolderAsync(hwnd, Lang.HomePage_SelectGameDir);

            if (!string.IsNullOrEmpty(folderPath))
            {
                if (GameLauncherService.IsValidInstallPath(folderPath, _currentRegion))
                {
                    GameLauncherService.SaveInstallPath(folderPath);
                    _lastSettings = AppSettings.Load();
                    GameInstallPathChangedMessage.Send();
                    RefreshGameStatus();
                }
                else
                {
                    var exeName = GameLauncherService.GetExeName(_currentRegion);
                    await new NoticeDialog(Lang.HomePage_InvalidPath, string.Format(Lang.HomePage_InvalidPathMessage, exeName), warning: true)
                    {
                        CloseButtonText = Lang.HomePage_OK,
                        XamlRoot = XamlRoot
                    }.ShowAsync();
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Error("定位游戏目录失败", ex);
        }
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isSettingsDialogOpen) return;
        _isSettingsDialogOpen = true;
        try
        {
            // 每次打开对话框前从磁盘加载最新配置，确保与对话框内 SaveBgImmediately 写入的值一致
            _lastSettings = AppSettings.Load();
            var dialog = new HomeSettingDialog { XamlRoot = XamlRoot };
            dialog.Initialize(_lastSettings);
            await dialog.ShowAsync();
            dialog.SaveToSettings(_lastSettings);
            _currentRegion = _lastSettings.Region;
            _currentVoiceLanguage = _lastSettings.VoiceLanguage;
            _downloadPath = _lastSettings.DownloadPath;

            DownloadService.Instance.Initialize(_downloadPath);
            _apiService.SetRegion(_currentRegion);

            // Refresh download info if region/version changed
            var newVersion = _lastSettings.GameVersion ?? string.Empty;
            if (_requestedVersion != newVersion)
            {
                _requestedVersion = newVersion;
                string? tag = !string.IsNullOrEmpty(_requestedVersion) ? _requestedVersion : null;
                _ = RefreshBuildDataAsync(tag, silentMode: true);
            }
            else if (_selectedBuildData == null)
            {
                var tag = !string.IsNullOrEmpty(_requestedVersion) ? _requestedVersion : null;
                _ = RefreshBuildDataAsync(tag, silentMode: true);
            }

            RefreshGameStatus();
            RefreshProxyInput();
        }
        finally
        {
            _isSettingsDialogOpen = false;
        }
    }

    #endregion

}
