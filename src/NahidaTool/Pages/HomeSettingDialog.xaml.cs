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
        PrimaryButtonText = "";
        CloseButtonText = "";
        this.Loaded += (_, _) => ApplyAccentColor();
        AccentColorChangedMessage.AccentColorChanged += OnAccentColorChanged;
        LanguageChangedMessage.LanguageChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged()
    {
        DispatcherQueue.TryEnqueue(() => this.Bindings.Update());
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

        // 直接从 settings 读取，不经过 property，确保 UI 反映磁盘上的真实值
        CustomBgToggleSwitch.IsOn = settings.EnableCustomBg;
        CustomBgPathText.Text = settings.CustomBg;
        VolumeSlider.Value = _videoBgVolume;
        UpdateVolumeIcon();
        ChangeCustomBgButton.IsEnabled = settings.EnableCustomBg;
        CustomBgPathGrid.Visibility = string.IsNullOrEmpty(settings.CustomBg) ? Visibility.Collapsed : Visibility.Visible;

        ProxyToggleSwitch.IsOn = _enableProxy;

        _isInitializing = false;
    }

    private void RefreshBasicInfo()
    {
        bool hasPath = !string.IsNullOrEmpty(_gameInstallPath);

        LocateGameButton.Visibility = hasPath ? Visibility.Collapsed : Visibility.Visible;
        UninstallGameButton.Visibility = hasPath ? Visibility.Visible : Visibility.Collapsed;
        InstallPathGrid.Visibility = hasPath ? Visibility.Visible : Visibility.Collapsed;
        GameSizeButton.Visibility = hasPath ? Visibility.Visible : Visibility.Collapsed;
        RelocateButton.Visibility = hasPath ? Visibility.Visible : Visibility.Collapsed;

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
        settings.StartGameAction = _startGameAction;
        settings.EnableThirdPartyTool = _enableThirdPartyTool;
        settings.ThirdPartyToolPath = _thirdPartyToolPath;
        settings.EnableCustomBg = CustomBgToggleSwitch.IsOn;
        settings.CustomBg = CustomBgPathText.Text ?? string.Empty;
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
        }
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

    private void ProxyToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _enableProxy = ProxyToggleSwitch.IsOn;
        if (_settings != null)
        {
            _settings.EnableProxy = _enableProxy;
            _settings.Save();
        }
        ProxySettingChangedMessage.Send();
    }

    private void UninstallCaButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProxyService.IsRunning)
        {
            LogService.Info("代理运行中，无法卸载 CA 证书");
            return;
        }

        bool success = ProxyService.DestroyCertificate();
        LogService.Info(success ? "CA 证书已卸载" : "CA 证书卸载失败");
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

    #region Custom Background
    
    public static readonly string BgFolder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "bg"));

    public static readonly HashSet<string> SupportedBgExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp",
        ".mp4", ".webm", ".avi", ".wmv", ".mov"
    };

    /// <summary>
    /// 将文件复制到 bg 文件夹并返回新路径。自动处理同名文件（添加序号）。
    /// 如果源文件已在 bg 文件夹中，直接返回原路径。
    /// </summary>
    public static string CopyFileToBgFolder(string sourcePath)
    {
        try
        {
            Directory.CreateDirectory(BgFolder);

            // 源文件已在 bg 文件夹中，无需复制
            string sourceDir = Path.GetDirectoryName(sourcePath) ?? "";
            if (string.Equals(sourceDir, BgFolder, StringComparison.OrdinalIgnoreCase))
                return sourcePath;

            string ext = Path.GetExtension(sourcePath);
            string nameWithoutExt = Path.GetFileNameWithoutExtension(sourcePath);
            string destPath = Path.Combine(BgFolder, $"{nameWithoutExt}{ext}");

            // 如果同名文件已存在且内容不同，添加数字序号
            if (File.Exists(destPath))
            {
                try
                {
                    var sourceInfo = new FileInfo(sourcePath);
                    var destInfo = new FileInfo(destPath);
                    if (sourceInfo.Length == destInfo.Length && sourceInfo.LastWriteTime == destInfo.LastWriteTime)
                        return destPath; // 同一个文件，无需复制
                }
                catch { }

                for (int i = 1; i < 1000; i++)
                {
                    destPath = Path.Combine(BgFolder, $"{nameWithoutExt}_{i}{ext}");
                    if (!File.Exists(destPath))
                        break;
                }
            }

            File.Copy(sourcePath, destPath, overwrite: true);
            LogService.Info($"背景文件已复制: {sourcePath} → {destPath}");
            return destPath;
        }
        catch (Exception ex)
        {
            LogService.Error("复制背景文件失败", ex);
            return sourcePath; // 回退到原始路径
        }
    }

    private void CustomBgToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _enableCustomBg = CustomBgToggleSwitch.IsOn;
        ChangeCustomBgButton.IsEnabled = _enableCustomBg;
        SaveBgImmediately();
    }

    private async void ChangeCustomBg_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ChangeBgErrorText.Visibility = Visibility.Collapsed;
            ChangeBgErrorText.Text = "";
            var filePath = await PickImageOrVideoFileAsync();
            if (!string.IsNullOrEmpty(filePath))
            {
                if (!await ValidateCustomBgFileAsync(filePath))
                {
                    ChangeBgErrorText.Text = Lang.HomeSettingDialog_CannotDecode;
                    ChangeBgErrorText.Visibility = Visibility.Visible;
                    return;
                }
                // 复制到 bg 文件夹，确保持久化
                string persistedPath = CopyFileToBgFolder(filePath);
                _customBg = persistedPath;
                _enableCustomBg = true;
                CustomBgToggleSwitch.IsOn = true;
                CustomBgPathText.Text = persistedPath;
                CustomBgPathGrid.Visibility = Visibility.Visible;
                ChangeCustomBgButton.IsEnabled = true;
                SaveBgImmediately();
            }
        }
        catch (Exception ex)
        {
            ChangeBgErrorText.Text = Lang.HomeSettingDialog_UnknownError;
            ChangeBgErrorText.Visibility = Visibility.Visible;
            LogService.Error("发生未知错误", ex);
        }
    }

    private async void OpenCustomBg_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (File.Exists(_customBg))
                await Launcher.LaunchUriAsync(new Uri(_customBg));
        }
        catch (Exception ex)
        {
            LogService.Debug($"打开自定义背景失败: {ex.Message}");
        }
    }

    private void DeleteCustomBg_Click(object sender, RoutedEventArgs e)
    {
        _customBg = string.Empty;
        CustomBgPathText.Text = string.Empty;
        CustomBgPathGrid.Visibility = Visibility.Collapsed;
        CustomBgToggleSwitch.IsOn = false;
        _enableCustomBg = false;
        ChangeCustomBgButton.IsEnabled = false;
        ChangeBgErrorText.Visibility = Visibility.Collapsed;
        ChangeBgErrorText.Text = "";
        SaveBgImmediately();
    }

    private void VolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        _videoBgVolume = (int)e.NewValue;
        if (_videoBgVolume > 0)
            _notMuteVolume = _videoBgVolume;
        UpdateVolumeIcon();
        NotifyVideoVolume();
    }

    private void MuteBg_Click(object sender, RoutedEventArgs e)
    {
        if (_videoBgVolume > 0)
        {
            _notMuteVolume = _videoBgVolume;
            _videoBgVolume = 0;
        }
        else
        {
            _videoBgVolume = _notMuteVolume;
        }
        VolumeSlider.Value = _videoBgVolume;
        UpdateVolumeIcon();
        NotifyVideoVolume();
    }

    private void UpdateVolumeIcon()
    {
        VolumeIcon.Glyph = _videoBgVolume switch
        {
            > 66 => "\uE995",
            > 33 => "\uE994",
            > 1 => "\uE993",
            _ => "\uE992",
        };
    }

    #endregion

    #region Helpers

    private void SaveBgImmediately()
    {
        if (_settings == null) return;
        _settings.VideoBgVolume = _videoBgVolume;
        _settings.Save();
        BackgroundChangedMessage.Send();
    }

    private void NotifyVideoVolume()
    {
        if (_settings != null)
        {
            _settings.VideoBgVolume = _videoBgVolume;
            _settings.Save();
        }
        if (App.MainWindow is MainWindow mw)
            mw.UpdateVideoVolume(_videoBgVolume);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private async Task ShowErrorDialogAsync(string message)
    {
        GeneralErrorText.Text = message;
        GeneralErrorText.Visibility = Visibility.Visible;
        await Task.CompletedTask;
    }

    /// <summary>
    private static async Task<bool> ValidateCustomBgFileAsync(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return false;

        try
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp")
            {
                using var stream = File.OpenRead(filePath);
                var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(
                    stream.AsRandomAccessStream());
                return decoder.FrameCount > 0;
            }
            if (ext is ".mp4" or ".webm" or ".avi" or ".wmv" or ".mov")
            {
                using var fileStream = File.OpenRead(filePath);
                return fileStream.Length > 0;
            }
            return false;
        }
        catch (Exception ex)
        {
            LogService.Debug($"验证自定义背景文件失败 ({filePath}): {ex.Message}");
            return false;
        }
    }

    private async Task<string?> PickImageOrVideoFileAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".bmp");
            picker.FileTypeFilter.Add(".gif");
            picker.FileTypeFilter.Add(".webp");
            picker.FileTypeFilter.Add(".mp4");
            picker.FileTypeFilter.Add(".webm");
            picker.FileTypeFilter.Add(".avi");
            picker.FileTypeFilter.Add(".wmv");
            picker.FileTypeFilter.Add(".mov");
            var file = await picker.PickSingleFileAsync();
            return file?.Path;
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            LogService.Warn($"FileOpenPicker 初始化失败，回退到 Win32 文件选择器: {ex.Message}");
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            return await FolderPickerHelper.PickFileAsync(hwnd, "图片和视频|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.mp4;*.webm;*.avi;*.wmv;*.mov");
        }
        catch (Exception ex)
        {
            LogService.Error("选择自定义背景文件失败", ex);
            return null;
        }
    }

    #endregion
}