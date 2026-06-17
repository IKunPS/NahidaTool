using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

public sealed partial class HomePage : Page
{
    private ServerRegionType _currentRegion = ServerRegionType.CN;
    private string? _foundGamePath;
    private AppSettings _lastSettings = new();

    private Process? _gameProcess;

    private GameState _gameState = GameState.None;
    private bool _isSettingsDialogOpen;

    #region Download Fields

    private ApiService _apiService;
    private List<BuildData> _buildDataList;
    private BuildData? _selectedBuildData;
    private BuildData? _selectedVoiceBuildData;
    private string _downloadPath;
    private VoiceLanguageType _currentVoiceLanguage = VoiceLanguageType.Chinese;
    private string _requestedVersion = string.Empty;
    private bool _hasPartialDownload;
    private CancellationTokenSource? _refreshCts;

    #endregion

    public HomePage()
    {
        InitializeComponent();
        this.NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;

        _apiService = new ApiService();
        _apiService.SetRegion(_currentRegion);
        _buildDataList = new List<BuildData>();
        _downloadPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "NahidaTool");

        RegisterDownloadServiceEvents();

        AccentColorChangedMessage.AccentColorChanged += OnAccentColorChanged;
        LanguageChangedMessage.LanguageChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            this.Bindings.Update();
            RefreshGameStatus();
            RefreshProxyInput();
            UpdateDownloadButtonStates();
        });
    }

    private void OnAccentColorChanged(Color color)
    {
        DispatcherQueue.TryEnqueue(UpdateButtonForeground);
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _lastSettings = AppSettings.Load();
        _currentRegion = _lastSettings.Region;
        _currentVoiceLanguage = _lastSettings.VoiceLanguage;

        _downloadPath = _lastSettings.DownloadPath;
        DownloadService.Instance.Initialize(_downloadPath);
        _hasPartialDownload = DownloadService.Instance.HasPartialDownload(_downloadPath);

        _apiService.SetRegion(_currentRegion);

        // If version changed, refresh download info silently
        var newVersion = _lastSettings.GameVersion ?? string.Empty;
        if (_requestedVersion != newVersion || _buildDataList.Count == 0)
        {
            _requestedVersion = newVersion;
            string? tag = !string.IsNullOrEmpty(_requestedVersion) ? _requestedVersion : null;
            _ = RefreshBuildDataAsync(tag, silentMode: true);
        }

        RefreshGameStatus();
        RefreshProxyInput();
        CheckGameRunning();
        UpdateDownloadButtonStates();

        GameInstallPathChangedMessage.PathChanged -= OnGameInstallPathChanged;
        GameInstallPathChangedMessage.PathChanged += OnGameInstallPathChanged;
        ProxySettingChangedMessage.ProxySettingChanged -= OnProxySettingChanged;
        ProxySettingChangedMessage.ProxySettingChanged += OnProxySettingChanged;
    }

    private void OnGameInstallPathChanged()
    {
        _lastSettings = AppSettings.Load();
        DispatcherQueue.TryEnqueue(RefreshGameStatus);
    }

    #region Post-Start Actions

    private void ApplyAfterStartAction()
    {
        var action = (StartGameActionType)_lastSettings.StartGameAction;
        if (App.MainWindow is not MainWindow mw) return;

        switch (action)
        {
            case StartGameActionType.Minimize:
                mw.Minimize();
                break;
            case StartGameActionType.Hide:
                mw.Hide();
                break;
        }
    }

    #endregion

    #region Game State

    private enum GameState
    {
        None,
        StartGame,
        GameIsRunning,
        InstallGame,
    }

    private void RefreshGameStatus()
    {
        try
        {
            var running = GameLauncherService.GetRunningProcess(_currentRegion);
            if (running != null)
            {
                _gameProcess = running;
                UpdateGameState(GameState.GameIsRunning);
                StartMonitoringGameProcess();
                return;
            }

            var settings = AppSettings.Load();
            if (GameLauncherService.IsValidInstallPath(settings.GameInstallPath, _currentRegion))
                _foundGamePath = settings.GameInstallPath;
            else
                _foundGamePath = null;

            if (_foundGamePath != null)
                UpdateGameState(GameState.StartGame);
            else
                UpdateGameState(GameState.InstallGame);
        }
        catch (Exception ex)
        {
            LogService.Error("刷新游戏状态失败", ex);
            UpdateGameState(GameState.StartGame);
        }
    }

    private void UpdateGameState(GameState state)
    {
        _gameState = state;

        StartGameButton.IsEnabled = state is not GameState.GameIsRunning;

        bool accentVisible = StartGameButton.IsEnabled;
        Rect_AccentBackground.Visibility = accentVisible ? Visibility.Visible : Visibility.Collapsed;

        UpdateButtonForeground();

        switch (state)
        {
            case GameState.StartGame:
                StartGameButtonText.Text = Lang.HomePage_StartGame;
                break;
            case GameState.GameIsRunning:
                StartGameButtonText.Text = Lang.HomePage_GameRunning;
                RunningInfoPopupText.Text = _gameProcess != null
                    ? $"{_gameProcess.ProcessName}.exe ({_gameProcess.Id})"
                    : "";
                break;
            case GameState.InstallGame:
                StartGameButtonText.Text = Lang.HomePage_LocateGame;
                break;
        }
    }

    private void UpdateButtonForeground()
    {
        var accentVisible = Rect_AccentBackground.Visibility == Visibility.Visible;

        StartGameButton.Foreground = StartGameButton.IsEnabled && accentVisible
            ? (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"]
            : (Brush)Application.Current.Resources["TextOnAccentFillColorDisabledBrush"];

        SettingsButton.Foreground = accentVisible
            ? (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"]
            : (Brush)Application.Current.Resources["TextOnAccentFillColorDisabledBrush"];
    }

    #endregion

    #region Button Events

    private async void StartGameButton_Click(object sender, RoutedEventArgs e)
    {
        if (_foundGamePath == null)
        {
            await DoLocateGameAsync();
            return;
        }

        try
        {
            // 启用了代理时，校验地址格式
            if (_lastSettings.EnableProxy)
            {
                var addr = _lastSettings.ProxyAddress?.Trim() ?? string.Empty;
                if (!string.IsNullOrEmpty(addr)
                    && !addr.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    && !addr.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                {
                    ProxyErrorText.Visibility = Visibility.Visible;
                    await new ContentDialog
                    {
                        Title = Lang.HomePage_UrlFormatError,
                        Content = Lang.HomePage_ProxyUrlFormatStartMessage,
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
            }

            var process = await GameLauncherService.StartGameAsync(_currentRegion);

            if (process != null)
            {
                _gameProcess = process;
                UpdateGameState(GameState.GameIsRunning);
                StartMonitoringGameProcess();

                // 启动游戏后操作
                ApplyAfterStartAction();
            }
        }
        catch (InvalidOperationException ex)
        {
            LogService.Warn($"启动游戏失败(操作异常): {ex.Message}");
            ProxyService.Stop();
            ProxyStatusText.Text = Lang.HomePage_ProxyNotStarted;
            StartGameButton.IsEnabled = true;
            Rect_AccentBackground.Visibility = Visibility.Visible;
            UpdateButtonForeground();
        }
        catch (FileNotFoundException ex)
        {
            LogService.Warn($"启动游戏失败(文件未找到): {ex.Message}");
            ProxyService.Stop();
            ProxyStatusText.Text = Lang.HomePage_ProxyNotStarted;
            StartGameButtonText.Text = Lang.HomePage_LocateGame;
            StartGameButton.IsEnabled = true;
            Rect_AccentBackground.Visibility = Visibility.Visible;
            _foundGamePath = null;
            UpdateButtonForeground();
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
                    await new ContentDialog
                    {
                        Title = Lang.HomePage_InvalidPath,
                        Content = string.Format(Lang.HomePage_InvalidPathMessage, exeName),
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
            _hasPartialDownload = DownloadService.Instance.HasPartialDownload(_downloadPath);

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

    #region Pointer Events

    private void StartGameGrid_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_gameState is GameState.GameIsRunning)
            Popup_GameInfo.IsOpen = true;
    }

    private void StartGameGrid_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        Popup_GameInfo.IsOpen = false;
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
            ProxyAddressTextBox.Text = _lastSettings.ProxyAddress;
        }
    }

    private bool _suppressProxyError;

    private void ProxyAddressTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _lastSettings.ProxyAddress = ProxyAddressTextBox.Text;
        _lastSettings.Save();

        // 输入时隐藏错误提示，失焦时再校验
        if (!_suppressProxyError && ProxyErrorText.Visibility == Visibility.Visible)
            ProxyErrorText.Visibility = Visibility.Collapsed;
    }

    private async void ProxyAddressTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
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
            await new ContentDialog
            {
                Title = Lang.HomePage_UrlFormatError,
                Content = Lang.HomePage_ProxyUrlFormatContent,
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

    #region Download

    private void RegisterDownloadServiceEvents()
    {
        var ds = DownloadService.Instance;
        ds.StatusChanged += DownloadService_StatusChanged;
        ds.ProgressChanged += DownloadService_ProgressChanged;
        ds.ProgressTextChanged += DownloadService_ProgressTextChanged;
        ds.DownloadCompleted += DownloadService_DownloadCompleted;
        ds.DownloadFailed += DownloadService_DownloadFailed;
        ds.SpeedUpdated += DownloadService_SpeedUpdated;
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        // Toggle download section — only visible when build data is available
        if (_selectedBuildData == null) return;

        bool visible = DownloadSection.Visibility == Visibility.Visible;
        DownloadSection.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;

        if (!visible)
            UpdateDownloadButtonStates();
    }

    private async void PauseResumeButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var ds = DownloadService.Instance;

            if (!ds.IsDownloading)
            {
                if (_selectedBuildData == null) return;

                ds.Initialize(_downloadPath);
                var task = ds.StartDownloadAsync(_selectedBuildData, _selectedVoiceBuildData, DispatcherQueue);
                UpdateDownloadButtonStates();
                await task;
                UpdateDownloadButtonStates();
            }
            else if (!ds.IsPaused)
            {
                ds.PauseDownload();
                UpdateDownloadButtonStates();
            }
            else
            {
                ds.ResumeDownload();
                UpdateDownloadButtonStates();
            }
        }
        catch (Exception ex)
        {
            LogService.Error("下载操作失败", ex);
            UpdateDownloadButtonStates();
        }
    }

    private async Task RefreshBuildDataAsync(string? Tag = null, bool silentMode = false)
    {
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = new CancellationTokenSource();
        var ct = _refreshCts.Token;

        try
        {
            if (!string.IsNullOrEmpty(Tag))
                _requestedVersion = Tag;

            var ds = DownloadService.Instance;
            if (!silentMode && ds.IsDownloading)
                ds.CancelDownload();

            if (!silentMode)
            {
                _selectedBuildData = null;
                _selectedVoiceBuildData = null;
                DownloadProgressBar.Value = 0;
                UpdateDownloadButtonStates();
            }

            var buildResponse = await _apiService.GetBuildInfoAsync(Tag, ct);
            ct.ThrowIfCancellationRequested();

            _buildDataList = buildResponse.Data?.Manifests ?? new List<BuildData>();
            foreach (var bd in _buildDataList)
                bd.Tag = buildResponse.Data?.Tag;

            if (_buildDataList.Count > 0)
            {
                _selectedBuildData = _buildDataList.FirstOrDefault(b => b.MatchingField == "game")
                                     ?? _buildDataList[0];
                UpdateVoicePackInfo();
                UpdateResourceInfo();
                UpdateDownloadButtonStates();

                // Notify version to settings
                if (!string.IsNullOrEmpty(_selectedBuildData.Tag))
                {
                    var version = _selectedBuildData.Tag;
                    _lastSettings.GameVersion = version;
                    _lastSettings.Save();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Error("RefreshBuildData failed", ex);
            if (!silentMode)
            {
                _selectedBuildData = null;
                _selectedVoiceBuildData = null;
                UpdateDownloadButtonStates();
            }
        }
    }

    private void UpdateVoicePackInfo()
    {
        if (_currentVoiceLanguage == VoiceLanguageType.None)
        {
            _selectedVoiceBuildData = null;
            return;
        }
        string matchingField = ServerConfig.VoicePackages.GetMatchingField(_currentVoiceLanguage);
        _selectedVoiceBuildData = _buildDataList.FirstOrDefault(b => b.MatchingField == matchingField);
    }

    private void UpdateResourceInfo()
    {
        if (_selectedBuildData == null) return;

        long totalCompressedSize = _selectedBuildData.Stats?.CompressedSize ?? 0;

        if (_selectedVoiceBuildData != null)
        {
            totalCompressedSize += _selectedVoiceBuildData.Stats?.CompressedSize ?? 0;
        }

        string voiceName = ServerConfig.VoicePackages.GetDisplayName(_currentVoiceLanguage);
        string displayVersion = !string.IsNullOrEmpty(_requestedVersion)
            ? _requestedVersion
            : (_selectedBuildData.Tag ?? "?");

        DownloadVersionText.Text = $"v{displayVersion} ({ServerConfig.GetRegionDisplayName(_currentRegion)})";
        DownloadStatsText.Text = $"{Lang.DownloadPage_CompressedSize}: {FormatFileSize(totalCompressedSize)} ({voiceName})";
    }

    private static string FormatFileSize(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        int idx = 0;
        double size = bytes;
        while (size >= 1024 && idx < suffixes.Length - 1)
        {
            size /= 1024;
            idx++;
        }
        return $"{size:0.00} {suffixes[idx]}";
    }

    private void UpdateDownloadButtonStates()
    {
        var ds = DownloadService.Instance;
        bool active = ds.IsDownloading;
        bool panelVisible = DownloadSection.Visibility == Visibility.Visible;

        // Show install button only when build data is available
        InstallButton.Visibility = _selectedBuildData != null
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (_selectedBuildData != null)
        {
            if (_hasPartialDownload)
                ToolTipService.SetToolTip(InstallButton, Lang.DownloadPage_ContinueDownload);
            else
                ToolTipService.SetToolTip(InstallButton, Lang.DownloadPage_StartDownload);
        }

        if (!active)
        {
            if (panelVisible)
            {
                // Panel open but not downloading yet — show start button
                PauseResumeButtonText.Text = Lang.DownloadPage_StartDownload;
                PauseResumeButton.IsEnabled = true;
                DownloadProgressBar.Value = 0;
                DownloadPercentText.Visibility = Visibility.Collapsed;
            }
            return;
        }

        if (ds.IsPaused)
        {
            PauseResumeButtonText.Text = Lang.DownloadPage_ContinueDownload;
        }
        else
        {
            PauseResumeButtonText.Text = Lang.DownloadPage_PauseDownload;
        }
    }

    private void DownloadService_StatusChanged(object? sender, string status)
    {
        // Panel visibility is user-controlled, no forced show
    }

    private void DownloadService_ProgressChanged(object? sender, double progress)
    {
        DispatcherQueue.TryEnqueue(() => DownloadProgressBar.Value = progress);
    }

    private void DownloadService_ProgressTextChanged(object? sender, string text)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            DownloadPercentText.Text = text;
            if (string.IsNullOrEmpty(text) || text == "0%")
                DownloadPercentText.Visibility = Visibility.Collapsed;
            else
                DownloadPercentText.Visibility = Visibility.Visible;
        });
    }

    private void DownloadService_DownloadCompleted(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            DownloadSpeedText.Text = "--";
            DownloadRemainingText.Text = "--";
            _hasPartialDownload = false;
            DownloadSection.Visibility = Visibility.Collapsed;
            UpdateDownloadButtonStates();
        });
    }

    private void DownloadService_DownloadFailed(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _hasPartialDownload = DownloadService.Instance.HasPartialDownload(_downloadPath);
            UpdateDownloadButtonStates();
        });
    }

    private void DownloadService_SpeedUpdated(object? sender, (double speedMbps, double writeSpeedMbps, TimeSpan remaining) e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            DownloadSpeedText.Text = string.Format(Lang.DownloadPage_SpeedFormat, e.speedMbps, e.writeSpeedMbps);
            if (e.remaining == TimeSpan.MaxValue)
                DownloadRemainingText.Text = string.Format(Lang.DownloadPage_RemainingFormat, Lang.DownloadPage_Calculating);
            else
                DownloadRemainingText.Text = string.Format(Lang.DownloadPage_RemainingFormat, e.remaining.ToString(@"hh\:mm\:ss"));
        });
    }

    #endregion
}