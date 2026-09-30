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

}
