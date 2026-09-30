using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;
using Windows.System;
using Windows.UI;
using NahidaTool.Models.Config;
using NahidaTool.Models.Enum;
using NahidaTool.Models.Event;
using NahidaTool.Models.Helper;
using NahidaTool.Models.Service;
using System.Globalization;
using NahidaTool.Models;

namespace NahidaTool.Pages;

public sealed partial class HomeSettingDialog : ContentDialog
{
    private string _gameInstallPath = string.Empty;
    private ServerRegionType _region = ServerRegionType.CN;
    private bool _enableRSA = true;
    private bool _enableHookRSA;
    private bool _enableProxy;
    private bool _enableKun;
    private int _startGameAction;
    private bool _enableThirdPartyTool;
    private string _thirdPartyToolPath = string.Empty;
    private bool _enableCustomBg
    {
        get => _settings?.EnableCustomBg ?? false;
        set { if (_settings != null) _settings.EnableCustomBg = value; }
    }
    private string _customBg
    {
        get => _settings?.CustomBg ?? string.Empty;
        set { if (_settings != null) _settings.CustomBg = value; }
    }
    private int _videoBgVolume = 100;
    private int _notMuteVolume = 100;
    private string _startGameArgument = string.Empty;
    private AppSettings? _settings;
    private bool _isInitializing; // 初始化期间阻止 Toggled 事件级联
    private static readonly ConcurrentDictionary<string, string> _gameSizeCache = new();

    public HomeSettingDialog()
    {
        InitializeComponent();
        InitializeResponsiveLayout();
        PrimaryButtonText = "";
        CloseButtonText = "";
        this.Loaded += (_, _) => ApplyAccentColor();
        AccentColorChangedMessage.AccentColorChanged += OnAccentColorChanged;
        LanguageChangedMessage.LanguageChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged()
    {
        DispatcherQueue.TryEnqueue(() => { Bindings.Update(); BackgroundPositionControl.UpdateLanguage(); });
    }

    private void OnAccentColorChanged(Color color)
    {
        DispatcherQueue.TryEnqueue(ApplyAccentColor);
    }

    private void ApplyAccentColor()
    {
        var accentBg = Application.Current.Resources["AccentFillColorDefaultBrush"] as SolidColorBrush;
        var accentText = Application.Current.Resources["AccentTextFillColorPrimaryBrush"] as SolidColorBrush;
        if (accentBg == null) return;

        // AccentButtonStyle buttons
        ChangeThirdPartyToolButton.Background = accentBg;
        ChangeCustomBgButton.Background = accentBg;

        // RelocateButton has an accent-colored TextBlock inside
        if (accentText != null && RelocateButton.Content is TextBlock relocateText)
        {
            relocateText.Foreground = accentText;
        }
    }

    public void Initialize(AppSettings settings)
    {
        _isInitializing = true;
        _settings = settings;
        GeneralErrorText.Visibility = Visibility.Collapsed;
        GeneralErrorText.Text = "";

        _region = settings.Region;
        _gameInstallPath = settings.GameInstallPath;
        _enableRSA = settings.EnableRSA;
        _enableHookRSA = settings.EnableHookRSA;
        _enableProxy = settings.EnableProxy;
        _enableKun = settings.EnableKun;
        _startGameAction = settings.StartGameAction;
        _enableThirdPartyTool = settings.EnableThirdPartyTool;
        _thirdPartyToolPath = settings.ThirdPartyToolPath;
        _enableCustomBg = settings.EnableCustomBg;
        _customBg = settings.CustomBg;
        _videoBgVolume = settings.VideoBgVolume;
        _notMuteVolume = _videoBgVolume > 0 ? _videoBgVolume : 100;
        _startGameArgument = settings.StartGameArgument;

        RefreshBasicInfo();

        RSAToggleSwitch.IsOn = _enableRSA;
        HookRSAToggleSwitch.IsOn = _enableHookRSA;
        HookRSAToggleSwitch.IsEnabled = _enableRSA;

        StartArgumentTextBox.Text = _startGameArgument;
        ComboBox_AfterStartAction.SelectedIndex = Math.Clamp(_startGameAction, 0, 2);
        ThirdPartyToolSwitch.IsOn = _enableThirdPartyTool;
        ChangeThirdPartyToolButton.IsEnabled = _enableThirdPartyTool;
        ThirdPartyPathText.Text = _thirdPartyToolPath;
        ThirdPartyPathGrid.Visibility = string.IsNullOrEmpty(_thirdPartyToolPath) ? Visibility.Collapsed : Visibility.Visible;

        InitializeBackgroundSettings();

        ProxyToggleSwitch.IsOn = _enableProxy;
        KunToggleSwitch.IsOn = _enableKun;
        KunToggleSwitch.IsEnabled = _enableProxy;

        _isInitializing = false;
    }

    private void RefreshBasicInfo()
    {
        bool hasPath = !string.IsNullOrEmpty(_gameInstallPath);

        LocateGameButton.Visibility = hasPath ? Visibility.Collapsed : Visibility.Visible;
        UninstallSection.Visibility = hasPath ? Visibility.Visible : Visibility.Collapsed;
        UninstallGameButton.Visibility = hasPath ? Visibility.Visible : Visibility.Collapsed;
        Grid_UninstallWarning.Visibility = Visibility.Collapsed;
        InstallPathGrid.Visibility = hasPath ? Visibility.Visible : Visibility.Collapsed;
        GameSizeButton.Visibility = hasPath ? Visibility.Visible : Visibility.Collapsed;
        RelocateButton.Visibility = hasPath ? Visibility.Visible : Visibility.Collapsed;
        LdiffUpdateButton.Visibility = hasPath ? Visibility.Visible : Visibility.Collapsed;

        if (hasPath)
        {
            InstallPathText.Text = _gameInstallPath;
            GameSizeText.Text = Lang.DownloadPage_Calculating;
            _ = RefreshGameSizeAsync(_gameInstallPath);
        }
    }

    private async Task RefreshGameSizeAsync(string path)
    {
        try
        {
            string sizeText = await Task.Run(() => GetGameSize(path));
            // 计算期间路径可能已改变，仅当路径仍匹配时才更新
            if (_gameInstallPath == path)
                GameSizeText.Text = sizeText;
        }
        catch (Exception ex)
        {
            LogService.Debug($"异步计算游戏大小失败: {ex.Message}");
        }
    }

    private static string GetGameSize(string? path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            return Lang.DownloadPage_UnknownVersion;

        if (_gameSizeCache.TryGetValue(path, out string? cached))
            return cached;

        try
        {
            long size = 0;
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try
                {
                    size += file.Length;
                }
                catch
                {
                    // 忽略无权限或被占用的文件
                }
            }

            var gb = (double)size / (1 << 30);
            var result = $"{gb:F2} GB";
            _gameSizeCache[path] = result;
            return result;
        }
        catch (Exception ex)
        {
            LogService.Debug($"计算游戏大小失败: {ex.Message}");
            return Lang.DownloadPage_UnknownVersion;
        }
    }

    public void SaveToSettings(AppSettings settings)
    {
        // _settings 已经通过 SaveBgImmediately 保持同步，这里确保最终一致性
        settings.GameInstallPath = _gameInstallPath;
        settings.EnableRSA = _enableRSA;
        settings.EnableHookRSA = _enableHookRSA;
        settings.EnableProxy = _enableProxy;
        settings.EnableKun = _enableKun;
        settings.StartGameAction = _startGameAction;
        settings.EnableThirdPartyTool = _enableThirdPartyTool;
        settings.ThirdPartyToolPath = _thirdPartyToolPath;
        settings.EnableCustomBg = CustomBgToggleSwitch.IsOn;
        settings.CustomBg = CustomBgPathText.Text ?? string.Empty;
        settings.BackgroundImageOffsetX = BackgroundPositionControl.HorizontalOffset;
        settings.BackgroundImageOffsetY = BackgroundPositionControl.VerticalOffset;
        settings.VideoBgVolume = _videoBgVolume;
        settings.StartGameArgument = _startGameArgument;
        settings.Save();
    }

    #region Navigation

    private void NavigationView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        try
        {
            // 切换标签页时清除错误提示
            GeneralErrorText.Visibility = Visibility.Collapsed;

            if (args.InvokedItemContainer?.Tag is string index && int.TryParse(index, out int target))
            {
                FlipView_Settings.SelectedIndex = target;
            }
        }
        catch (Exception ex)
        {
            LogService.Debug($"导航标签页切换失败: {ex.Message}");
        }
    }

    private void FlipView_Settings_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var grid = VisualTreeHelper.GetChild(FlipView_Settings, 0);
            if (grid != null)
            {
                var count = VisualTreeHelper.GetChildrenCount(grid);
                for (int i = 0; i < count; i++)
                {
                    var child = VisualTreeHelper.GetChild(grid, i);
                    if (child is Button button)
                    {
                        button.IsHitTestVisible = false;
                        button.Opacity = 0;
                    }
                    else if (child is ScrollViewer scrollViewer)
                    {
                        scrollViewer.PointerWheelChanged += (_, args) => args.Handled = true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Debug($"FlipView加载设置失败: {ex.Message}");
        }
    }

    #endregion

    #region General

    private async void LocateGamePath_Click(object sender, RoutedEventArgs e)
    {
        GeneralErrorText.Visibility = Visibility.Collapsed;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        var folderPath = await FolderPickerHelper.PickFolderAsync(hwnd, Lang.HomePage_SelectGameDir);
        if (string.IsNullOrEmpty(folderPath)) return;

        if (GameLauncherService.IsValidInstallPath(folderPath, _region))
        {
            _gameInstallPath = folderPath;
            if (_settings != null)
                _settings.GameInstallPath = folderPath;
            GameLauncherService.SaveInstallPath(folderPath);
            RefreshBasicInfo();
            GameInstallPathChangedMessage.Send();
        }
        else
        {
            GeneralErrorText.Text = string.Format(Lang.HomePage_InvalidPathMessage, GameLauncherService.GetExeName(_region));
            GeneralErrorText.Visibility = Visibility.Visible;
        }
    }

    private async void LdiffUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settings == null || string.IsNullOrWhiteSpace(_gameInstallPath))
            return;

        var xamlRoot = XamlRoot;
        var apiService = new ApiService();
        apiService.SetRegion(_region);
        Hide();

        var dialog = new LdiffUpdateDialog(apiService, _settings)
        {
            XamlRoot = xamlRoot,
        };
        await dialog.ShowAsync();
    }

    private void ShowUninstallWarning_Click(object sender, RoutedEventArgs e)
    {
        GeneralErrorText.Visibility = Visibility.Collapsed;

        if (Directory.Exists(_gameInstallPath))
        {
            var installPath = Path.GetFullPath(_gameInstallPath);
            if (Path.GetPathRoot(_gameInstallPath) == _gameInstallPath)
            {
                _ = ShowErrorDialogAsync(Lang.HomeSettingDialog_CannotDeleteRoot);
                return;
            }
            var baseFolder = AppContext.BaseDirectory.TrimEnd('/', '\\');
            if (baseFolder.StartsWith(installPath, StringComparison.OrdinalIgnoreCase))
            {
                _ = ShowErrorDialogAsync(Lang.HomeSettingDialog_NahidaInGameFolder);
                return;
            }
            Grid_UninstallWarning.Visibility = Visibility.Visible;
            Grid_UninstallWarning.UpdateLayout();
            CancelUninstallButton.Focus(FocusState.Programmatic);
            CancelUninstallButton.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        }
    }

    private void CancelUninstall_Click(object sender, RoutedEventArgs e)
    {
        Grid_UninstallWarning.Visibility = Visibility.Collapsed;
        UninstallGameButton.Focus(FocusState.Programmatic);
    }

    private async void UninstallGame_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var running = GameLauncherService.GetRunningProcess(_region);
            if (running != null)
            {
                await ShowErrorDialogAsync(Lang.HomeSettingDialog_GameIsRunning);
                return;
            }

            if (Directory.Exists(_gameInstallPath))
            {
                Directory.Delete(_gameInstallPath, true);
                _gameInstallPath = string.Empty;
                if (_settings != null)
                    _settings.GameInstallPath = string.Empty;
                GameLauncherService.SaveInstallPath("");
            }

            Grid_UninstallWarning.Visibility = Visibility.Collapsed;
            RefreshBasicInfo();
            GameInstallPathChangedMessage.Send();
        }
        catch (Exception ex)
        {
            await ShowErrorDialogAsync(string.Format(Lang.HomeSettingDialog_UninstallFailed, ex.Message));
        }
    }

    private async void OpenInstallFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Directory.Exists(_gameInstallPath))
                await Launcher.LaunchUriAsync(new Uri(_gameInstallPath));
        }
        catch (Exception ex)
        {
            LogService.Debug($"打开安装文件夹失败: {ex.Message}");
        }
    }

    private void DeleteInstallPath_Click(object sender, RoutedEventArgs e)
    {
        _gameInstallPath = string.Empty;
        if (_settings != null)
            _settings.GameInstallPath = string.Empty;
        GameLauncherService.SaveInstallPath("");
        RefreshBasicInfo();
        GameInstallPathChangedMessage.Send();
    }

    private void RSAToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _enableRSA = RSAToggleSwitch.IsOn;
        HookRSAToggleSwitch.IsEnabled = _enableRSA;
    }

    private void HookRSAToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _enableHookRSA = HookRSAToggleSwitch.IsOn;
    }

    #endregion

    #region Launch Arguments

    private void StartArgumentTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _startGameArgument = StartArgumentTextBox.Text;
    }

    private void ComboBox_AfterStartAction_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ComboBox_AfterStartAction.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            if (int.TryParse(tag, out int value))
                _startGameAction = value;
        }
    }

    private void ThirdPartyToolSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _enableThirdPartyTool = ThirdPartyToolSwitch.IsOn;
        ChangeThirdPartyToolButton.IsEnabled = _enableThirdPartyTool;
        if (!_enableThirdPartyTool)
        {
            _thirdPartyToolPath = string.Empty;
            ThirdPartyPathText.Text = string.Empty;
            ThirdPartyPathGrid.Visibility = Visibility.Collapsed;
        }
    }

    private async void ChangeThirdPartyPath_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            var filePath = await FolderPickerHelper.PickFileAsync(hwnd, "程序文件|*.exe;*.bat;*.cmd;*.lnk");
            if (!string.IsNullOrEmpty(filePath))
            {
                _thirdPartyToolPath = filePath;
                ThirdPartyPathText.Text = filePath;
                ThirdPartyPathGrid.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            LogService.Debug($"选择第三方工具路径失败: {ex.Message}");
        }
    }

    private async void OpenThirdPartyToolFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (File.Exists(_thirdPartyToolPath))
            {
                var folder = Path.GetDirectoryName(_thirdPartyToolPath);
                if (Directory.Exists(folder))
                {
                    var file = await StorageFile.GetFileFromPathAsync(_thirdPartyToolPath);
                    var options = new FolderLauncherOptions();
                    options.ItemsToSelect.Add(file);
                    await Launcher.LaunchFolderPathAsync(folder, options);
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Debug($"打开第三方工具目录失败: {ex.Message}");
        }
    }

    private void DeleteThirdPartyPath_Click(object sender, RoutedEventArgs e)
    {
        _thirdPartyToolPath = string.Empty;
        ThirdPartyPathText.Text = string.Empty;
        ThirdPartyPathGrid.Visibility = Visibility.Collapsed;
    }

    #endregion

}
