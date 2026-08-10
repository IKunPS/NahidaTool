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
    private CancellationTokenSource? _refreshCts;
    private CancellationTokenSource? _dialogSizeRefreshCts;
    private bool _isPreparingDownload;

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
        Downloading,     // 下载客户端中
    }

    private void RefreshGameStatus()
    {
        try
        {
            var settings = AppSettings.Load();
            _lastSettings = settings;
            _foundGamePath = GameLauncherService.IsValidInstallPath(settings.GameInstallPath, _currentRegion)
                ? settings.GameInstallPath
                : null;

            var running = GameLauncherService.GetRunningProcess(_currentRegion);
            if (running != null)
            {
                _gameProcess = running;
                UpdateGameState(GameState.GameIsRunning);
                StartMonitoringGameProcess();
                return;
            }

            // 已存在可启动的客户端时，下载不抢占启动按钮；
            // 只有在没有有效安装路径时才用下载状态填充胶囊。
            if (_foundGamePath != null)
            {
                UpdateGameState(GameState.StartGame);
                return;
            }

            var downloadService = DownloadService.Instance;
            if (downloadService.IsDownloading || _isPreparingDownload)
            {
                UpdateGameState(GameState.Downloading);
                return;
            }

            UpdateGameState(GameState.InstallGame);
        }
        catch (Exception ex)
        {
            LogService.Error("刷新游戏状态失败", ex);
            _foundGamePath = null;
            UpdateGameState(GameState.InstallGame);
        }
    }

    private void UpdateGameState(GameState state)
    {
        _gameState = state;

        bool downloadActive = state is GameState.Downloading;
        StartGameButton.IsEnabled = state is not GameState.GameIsRunning;

        bool accentVisible = StartGameButton.IsEnabled && !downloadActive;
        Rect_AccentBackground.Visibility = accentVisible ? Visibility.Visible : Visibility.Collapsed;
        NormalActionGrid.Visibility = downloadActive ? Visibility.Collapsed : Visibility.Visible;
        DownloadActionGrid.Visibility = downloadActive ? Visibility.Visible : Visibility.Collapsed;
        GameActionProgressRing.Visibility = Visibility.Collapsed;
        SettingsButton.Visibility = Visibility.Visible;

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
                StartGameButtonText.Text = HasPartialClientDownload()
                    ? Lang.DownloadPage_ContinueDownload
                    : Lang.DownloadDialog_StartInstall;
                break;
            case GameState.Downloading:
                // 下载：圆环+暂停文字由 UpdateDownloadButtonStates 管理
                UpdateDownloadButtonStates();
                break;
        }

    }

    private void UpdateButtonForeground()
    {
        var accentBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        var disabledBrush = (Brush)Application.Current.Resources["TextOnAccentFillColorDisabledBrush"];
        var primaryBrush = (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"];
        bool accentVisible = Rect_AccentBackground.Visibility == Visibility.Visible;

        StartGameButton.Foreground = (StartGameButton.IsEnabled, accentVisible, _isActionButtonPointerOver) switch
        {
            (false, _, _) => disabledBrush,
            (true, false, true) => accentBrush,
            (true, false, false) => disabledBrush,
            _ => primaryBrush,
        };
        SettingsButton.Foreground = (!accentVisible, _isSettingButtonPointerOver) switch
        {
            (true, true) => accentBrush,
            (true, false) => disabledBrush,
            _ => primaryBrush,
        };
    }

    #endregion

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
            else
            {
                ProxyService.Stop();
                ProxyStatusText.Text = Lang.HomePage_ProxyNotStarted;
                RefreshGameStatus();
            }
        }
        catch (InvalidOperationException ex)
        {
            LogService.Warn($"启动游戏失败(操作异常): {ex.Message}");
            ProxyService.Stop();
            ProxyStatusText.Text = Lang.HomePage_ProxyNotStarted;
            RefreshGameStatus();
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
            ProxyAddressTextBox.Text = _lastSettings.ProxyAddress;
        }
    }

    private bool _suppressProxyError;

    private void ProxyAddressTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string proxyAddress = ProxyAddressTextBox.Text;
        _lastSettings = AppSettings.Update(settings => settings.ProxyAddress = proxyAddress);

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

    /// <summary>
    /// 未安装客户端：弹出"选择安装路径"毛玻璃弹窗（语言多选/快捷方式/容量）
    /// </summary>
    private async Task ShowNoClientDialogAsync()
    {
        var dialog = new DownloadGameDialog { XamlRoot = XamlRoot };
        dialog.Initialize(_lastSettings);

        // "已安装？定位游戏"回调：关闭弹窗并走定位流程
        dialog.LocateGameRequested += (_, _) =>
        {
            dialog.Hide();
            _ = DoLocateGameAsync();
        };

        // 版本变化：立即保存到配置并重新拉取容量
        dialog.VersionChanged += (_, _) =>
        {
            _lastSettings = AppSettings.Update(settings => settings.GameVersion = dialog.GameVersion);
            _ = RefreshDialogSizesAsync(dialog);
        };
        // 服区变化时切换 API 区域并重新拉取容量
        dialog.RegionChanged += (_, _) =>
        {
            _apiService.SetRegion(dialog.Region);
            _ = RefreshDialogSizesAsync(dialog);
        };
        dialog.Closed += (_, _) => CancelDialogSizeRefresh();

        // 异步拉取构建信息填充容量（失败静默，不影响弹窗）
        _ = RefreshDialogSizesAsync(dialog);
        // 异步获取私服当前版本并推荐到弹窗（失败静默）
        _ = RecommendServerVersionAsync(dialog);

        await dialog.ShowAsync();
        if (!dialog.Confirmed) return;

    // 保存对话框选择到配置（优先保存输入框中的版本号，空版本回退到服务端解析的最新版）
    string downloadTag = string.IsNullOrWhiteSpace(dialog.GameVersion)
        ? dialog.ResolvedVersion
        : dialog.GameVersion;
    _lastSettings = AppSettings.Update(settings =>
    {
        settings.Region = dialog.Region;
        settings.DownloadPath = dialog.InstallPath;
        settings.GameVersion = downloadTag;
        settings.VoiceLanguage = dialog.SelectedVoices;
    });
    _currentRegion = dialog.Region;
    _apiService.SetRegion(_currentRegion);
    _downloadPath = dialog.InstallPath;
    _currentVoiceLanguage = dialog.SelectedVoices;

        DownloadService.Instance.Initialize(_downloadPath);

        // 下载游戏：空版本 = 最新；多语音按勾选下载
        await DownloadClientAsync(downloadTag, dialog.SelectedVoices);
    }

    /// <summary>
    /// 从私服状态接口获取当前版本并推荐到下载弹窗（失败静默，不影响弹窗使用）
    /// </summary>
    private async Task RecommendServerVersionAsync(DownloadGameDialog dialog)
    {
        try
        {
            string? version = await PrivateServerService.GetServerVersionAsync(_lastSettings);
            if (!string.IsNullOrWhiteSpace(version))
                dialog.SetRecommendedVersion(version);
        }
        catch (Exception ex)
        {
            LogService.Debug($"推荐私服版本失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 拉取构建信息并刷新弹窗容量显示
    /// </summary>
    private async Task RefreshDialogSizesAsync(DownloadGameDialog dialog)
    {
        var requestCts = new CancellationTokenSource();
        var previousCts = Interlocked.Exchange(ref _dialogSizeRefreshCts, requestCts);
        previousCts?.Cancel();

        string requestedVersion = dialog.GameVersion;
        dialog.BeginResourceCheck();
        try
        {
            await Task.Delay(250, requestCts.Token);
            var buildResponse = await _apiService.GetBuildInfoAsync(
                string.IsNullOrEmpty(requestedVersion) ? null : requestedVersion, requestCts.Token);

            if (requestCts.IsCancellationRequested ||
                !string.Equals(dialog.GameVersion, requestedVersion, StringComparison.Ordinal))
                return;
            var builds = buildResponse.Data?.Manifests ?? new List<BuildData>();

            var game = FindGameResource(builds);
            if (!DownloadService.IsValidResource(game))
            {
                dialog.SetResourceError(Lang.DownloadPage_NoResourceInfo);
                return;
            }

            long gameCompressed = game?.Stats?.CompressedSize ?? 0;
            long gameUncompressed = game?.Stats?.UncompressedSize ?? 0;

            var voiceSizes = new Dictionary<VoiceLanguageType, (long Compressed, long Uncompressed)>();
            foreach (var lang in Enum.GetValues<VoiceLanguageType>())
            {
                if (lang == VoiceLanguageType.None) continue;
                var voice = builds.FirstOrDefault(b => b.MatchingField == ServerConfig.VoicePackages.GetMatchingField(lang));
                voiceSizes[lang] = (voice?.Stats?.CompressedSize ?? 0, voice?.Stats?.UncompressedSize ?? 0);
            }

            dialog.UpdateSizes(gameCompressed, gameUncompressed, voiceSizes,
                buildResponse.Data?.Tag ?? requestedVersion);
        }
        catch (OperationCanceledException) when (requestCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LogService.Warn($"刷新弹窗容量失败: {ex.Message}");
            if (!requestCts.IsCancellationRequested)
                dialog.SetResourceError(string.Format(Lang.DownloadPage_GetResourceFailed, ex.Message));
        }
        finally
        {
            Interlocked.CompareExchange(ref _dialogSizeRefreshCts, null, requestCts);
            requestCts.Dispose();
        }
    }

    private void CancelDialogSizeRefresh()
    {
        var cts = Interlocked.Exchange(ref _dialogSizeRefreshCts, null);
        cts?.Cancel();
    }

    /// <summary>
    /// 下载客户端：按版本拉取构建信息并开始安装下载（支持多语音）
    /// </summary>
    private async Task DownloadClientAsync(string tag, VoiceLanguageType voices = VoiceLanguageType.None)
    {
        _isPreparingDownload = true;
        try
        {
            UpdateGameState(GameState.Downloading);

            var buildResponse = await _apiService.GetBuildInfoAsync(
                string.IsNullOrEmpty(tag) ? null : tag);
            _buildDataList = buildResponse.Data?.Manifests ?? new List<BuildData>();
            foreach (var bd in _buildDataList)
                bd.Tag = buildResponse.Data?.Tag;

            if (_buildDataList.Count == 0)
            {
                ShowMessageAsync(Lang.DownloadPage_NoResourceInfo);
                RefreshGameStatus();
                return;
            }

            _selectedBuildData = FindGameResource(_buildDataList);
            if (!DownloadService.IsValidResource(_selectedBuildData))
            {
                ShowMessageAsync(Lang.DownloadPage_NoResourceInfo);
                return;
            }

            _selectedVoiceBuildData = null;
            UpdateVoicePackInfo();
            UpdateDownloadButtonStates();

            // 保存用户选择的游戏版本。
            string resolvedVersion = string.IsNullOrWhiteSpace(tag)
                ? (buildResponse.Data?.Tag ?? string.Empty)
                : tag;
            _lastSettings = AppSettings.Update(settings => settings.GameVersion = resolvedVersion);
            _requestedVersion = _lastSettings.GameVersion;
            ShowDownloadStartTip();

            // 收集勾选语言的语音包
            var voiceBuilds = new List<BuildData>();
            foreach (var lang in Enum.GetValues<VoiceLanguageType>())
            {
                if (lang == VoiceLanguageType.None || !voices.HasFlag(lang)) continue;
                var voice = _buildDataList.FirstOrDefault(b =>
                    b.MatchingField == ServerConfig.VoicePackages.GetMatchingField(lang));
                if (!DownloadService.IsValidResource(voice))
                {
                    ShowMessageAsync(string.Format(Lang.DownloadPage_GetResourceFailed,
                        ServerConfig.VoicePackages.GetDisplayName(lang)));
                    return;
                }
                voiceBuilds.Add(voice!);
            }

            var ds = DownloadService.Instance;
            ds.Initialize(_downloadPath);

            _isPreparingDownload = false;
            bool completed = await ds.StartDownloadAsync(_selectedBuildData, voiceBuilds, DispatcherQueue);
            if (completed)
            {
                if (GameLauncherService.IsValidInstallPath(_downloadPath, _currentRegion))
                {
                    string installedPath = Path.GetFullPath(_downloadPath);
                    _lastSettings = AppSettings.Update(settings => settings.GameInstallPath = installedPath);
                    _foundGamePath = _lastSettings.GameInstallPath;
                    GameInstallPathChangedMessage.Send();
                }
                else
                {
                    ShowMessageAsync(Lang.DownloadDialog_ClientMissingAfterDownload);
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Error("下载客户端失败", ex);
            ShowMessageAsync(string.Format(Lang.DownloadPage_GetResourceFailed, ex.Message));
        }
        finally
        {
            _isPreparingDownload = false;
            RefreshGameStatus();
        }
    }

    /// <summary>
    /// 消息通知气泡（长方形，右上角 X 关闭，无需确认键）
    /// </summary>
    private void ShowMessageAsync(string message)
    {
        var tip = new TeachingTip
        {
            XamlRoot = XamlRoot,
            Target = StartGameGrid,
            Title = message,
            PreferredPlacement = TeachingTipPlacementMode.Top,
            IsLightDismissEnabled = true,
        };
        tip.IsOpen = true;
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
                UpdateDownloadButtonStates();
            }

            var buildResponse = await _apiService.GetBuildInfoAsync(Tag, ct);
            ct.ThrowIfCancellationRequested();

            _buildDataList = buildResponse.Data?.Manifests ?? new List<BuildData>();
            foreach (var bd in _buildDataList)
                bd.Tag = buildResponse.Data?.Tag;

            _selectedBuildData = FindGameResource(_buildDataList);
            _selectedVoiceBuildData = null;
            if (DownloadService.IsValidResource(_selectedBuildData))
            {
                UpdateVoicePackInfo();
                UpdateDownloadButtonStates();
            }
            else
            {
                _selectedBuildData = null;
                UpdateDownloadButtonStates();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Error("RefreshBuildData failed", ex);
            _selectedBuildData = null;
            _selectedVoiceBuildData = null;
            UpdateDownloadButtonStates();
        }
    }

    private void UpdateVoicePackInfo()
    {
        if (_currentVoiceLanguage == VoiceLanguageType.None)
        {
            _selectedVoiceBuildData = null;
            return;
        }
        var selectedLanguage = Enum.GetValues<VoiceLanguageType>()
            .FirstOrDefault(language => language != VoiceLanguageType.None
                                        && _currentVoiceLanguage.HasFlag(language));
        if (selectedLanguage == VoiceLanguageType.None)
        {
            _selectedVoiceBuildData = null;
            return;
        }

        string matchingField = ServerConfig.VoicePackages.GetMatchingField(selectedLanguage);
        _selectedVoiceBuildData = _buildDataList.FirstOrDefault(b => b.MatchingField == matchingField);
    }

    private static BuildData? FindGameResource(IEnumerable<BuildData> builds)
    {
        return builds.FirstOrDefault(build =>
                   string.Equals(build.MatchingField, ServerConfig.VoicePackages.Game,
                       StringComparison.OrdinalIgnoreCase))
               ?? builds.FirstOrDefault(build => string.IsNullOrWhiteSpace(build.MatchingField));
    }

    private bool HasPartialClientDownload()
    {
        return !string.IsNullOrWhiteSpace(_downloadPath)
               && DownloadService.Instance.HasPartialDownload(_downloadPath);
    }

    private static string GetDownloadStageText(DownloadService downloadService)
    {
        if (downloadService.IsPaused) return Lang.DownloadPage_Paused;

        return downloadService.CurrentStage switch
        {
            DownloadStage.Preparing => Lang.DownloadPage_Preparing,
            DownloadStage.CheckingFiles => Lang.DownloadPage_CheckingFiles,
            _ => Lang.DownloadPage_Downloading,
        };
    }

    // 鼠标是否悬停在胶囊上（悬停时不自动关闭气泡）
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
