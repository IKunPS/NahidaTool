using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NahidaTool.Models;
using NahidaTool.Models.Config;
using NahidaTool.Models.Enum;
using NahidaTool.Models.Event;
using NahidaTool.Models.Helper;
using NahidaTool.Models.Service;
using Windows.UI;

namespace NahidaTool.Pages;

/// <summary>
/// 选择安装路径弹窗：毛玻璃深色 UI，含语言多选/快捷方式/容量信息
/// </summary>
public sealed partial class DownloadGameDialog : ContentDialog
{
    private bool _isInitializing;
    private AppSettings? _settings;

    /// <summary>
    /// 用户是否点击了"开始安装"
    /// </summary>
    public bool Confirmed { get; private set; }

    /// <summary>
    /// 安装路径
    /// </summary>
    public string InstallPath => DownloadPathText.Text?.Trim() ?? string.Empty;

    /// <summary>
    /// 指定版本（空 = 最新）
    /// </summary>
    public string GameVersion => GameVersionTextBox.Text?.Trim() ?? string.Empty;

    /// <summary>
    /// 当前选择的服务器区域（国服/国际服）
    /// </summary>
    public ServerRegionType Region { get; private set; } = ServerRegionType.CN;

    /// <summary>
    /// 服务端已经返回有效的游戏本体资源后才允许开始安装。
    /// </summary>
    public bool ResourceReady { get; private set; }

    /// <summary>
    /// 空版本号请求会被解析为服务端当前版本，下载时使用这个确定值。
    /// </summary>
    public string ResolvedVersion { get; private set; } = string.Empty;

    /// <summary>
    /// 勾选的语言（Flags 组合）
    /// </summary>
    public VoiceLanguageType SelectedVoices { get; private set; }

    /// <summary>
    /// 用户点击"定位游戏"时触发（由 HomePage 关闭弹窗并走定位流程）
    /// </summary>
    public event EventHandler? LocateGameRequested;

    /// <summary>
    /// 服区变化时触发，HomePage 据此切换 API 区域并重新拉取容量
    /// </summary>
    public event EventHandler? RegionChanged;

    public DownloadGameDialog()
    {
        InitializeComponent();
        PrimaryButtonText = "";
        CloseButtonText = "";
        Loaded += (_, _) => ApplyAccentColor();
        AccentColorChangedMessage.AccentColorChanged += OnAccentColorChanged;
        Closed += (_, _) => AccentColorChangedMessage.AccentColorChanged -= OnAccentColorChanged;
    }

    /// <summary>
    /// 主题色变化时同步按钮颜色（仿 HomeSettingDialog）
    /// </summary>
    private void OnAccentColorChanged(Color color)
    {
        DispatcherQueue.TryEnqueue(ApplyAccentColor);
    }

    private void ApplyAccentColor()
    {
        var accentBg = Application.Current.Resources["AccentFillColorDefaultBrush"] as SolidColorBrush;
        if (accentBg == null) return;
        ChangeButton.Background = accentBg;
        StartInstallButton.Background = accentBg;
    }

    /// <summary>
    /// 用当前配置初始化控件
    /// </summary>
    public void Initialize(AppSettings settings)
    {
        _isInitializing = true;
        _settings = settings;

        Region = settings.Region;
        RegionComboBox.SelectedIndex = Region == ServerRegionType.CN ? 0 : 1;

        DownloadPathText.Text = settings.DownloadPath;
        GameVersionTextBox.Text = settings.GameVersion;
        _versionEdited = false;

        // 默认勾选当前语音语言（旧配置为单选）
        SelectedVoices = settings.VoiceLanguage;
        ChineseCheckBox.IsChecked = SelectedVoices.HasFlag(VoiceLanguageType.Chinese);
        EnglishCheckBox.IsChecked = SelectedVoices.HasFlag(VoiceLanguageType.English);
        JapaneseCheckBox.IsChecked = SelectedVoices.HasFlag(VoiceLanguageType.Japanese);
        KoreanCheckBox.IsChecked = SelectedVoices.HasFlag(VoiceLanguageType.Korean);

        _isInitializing = false;
        BeginResourceCheck();
    }

    // 各资源压缩/解压大小（字节），UpdateSizes 时缓存
    private long _gameCompressed;
    private long _gameUncompressed;
    private readonly Dictionary<VoiceLanguageType, (long Compressed, long Uncompressed)> _voiceSizes = new();

    /// <summary>
    /// 更新资源容量显示（由 HomePage 拉取构建信息后传入）
    /// </summary>
    public void UpdateSizes(long gameCompressed, long gameUncompressed,
        IReadOnlyDictionary<VoiceLanguageType, (long Compressed, long Uncompressed)> voiceSizes,
        string resolvedVersion)
    {
        _gameCompressed = gameCompressed;
        _gameUncompressed = gameUncompressed;
        _voiceSizes.Clear();
        foreach (var (lang, size) in voiceSizes)
            _voiceSizes[lang] = size;

        // 与官方一致：显示压缩大小（下载体积）
        GameSizeText.Text = FormatSize(gameCompressed);
        ChineseSizeText.Text = FormatSize(_voiceSizes.GetValueOrDefault(VoiceLanguageType.Chinese).Compressed);
        EnglishSizeText.Text = FormatSize(_voiceSizes.GetValueOrDefault(VoiceLanguageType.English).Compressed);
        JapaneseSizeText.Text = FormatSize(_voiceSizes.GetValueOrDefault(VoiceLanguageType.Japanese).Compressed);
        KoreanSizeText.Text = FormatSize(_voiceSizes.GetValueOrDefault(VoiceLanguageType.Korean).Compressed);

        _isInitializing = true;
        UpdateVoiceAvailability(ChineseCheckBox, VoiceLanguageType.Chinese);
        UpdateVoiceAvailability(EnglishCheckBox, VoiceLanguageType.English);
        UpdateVoiceAvailability(JapaneseCheckBox, VoiceLanguageType.Japanese);
        UpdateVoiceAvailability(KoreanCheckBox, VoiceLanguageType.Korean);
        _isInitializing = false;
        UpdateSelectedVoices();

        ResolvedVersion = resolvedVersion;
        ResourceReady = gameCompressed > 0 && gameUncompressed > 0;
        StartInstallButton.IsEnabled = ResourceReady;
        ValidationText.Text = ResourceReady ? string.Empty : Lang.DownloadPage_NoResourceInfo;
        RefreshSummary();
    }

    public void BeginResourceCheck()
    {
        ResourceReady = false;
        ResolvedVersion = string.Empty;
        StartInstallButton.IsEnabled = false;
        GameSizeText.Text = "--";
        ChineseSizeText.Text = "--";
        EnglishSizeText.Text = "--";
        JapaneseSizeText.Text = "--";
        KoreanSizeText.Text = "--";
        SummarySizeText.Text = string.Empty;
        ValidationText.Text = string.Format(Lang.DownloadPage_GettingResourceInfo,
            Lang.DownloadDialog_GameResource);
    }

    public void SetResourceError(string message)
    {
        ResourceReady = false;
        ResolvedVersion = string.Empty;
        StartInstallButton.IsEnabled = false;
        ValidationText.Text = message;
    }

    /// <summary>
    /// 设置私服推荐版本：更新“留空表示最新版本”提示行，并在用户未手动输入版本号时自动填入推荐版本
    /// </summary>
    public void SetRecommendedVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version)) return;

        VersionHintText.Text = string.Format(Lang.DownloadDialog_RecommendedVersion, version.Trim());

        // 用户没有手动输入过版本号时，自动填入推荐版本（会触发 VersionChanged 重新拉取容量）
        if (!_versionEdited)
        {
            GameVersionTextBox.Text = version.Trim();
        }
    }

    private void UpdateVoiceAvailability(CheckBox checkBox, VoiceLanguageType language)
    {
        bool available = _voiceSizes.GetValueOrDefault(language).Compressed > 0;
        checkBox.IsEnabled = available;
        if (!available)
            checkBox.IsChecked = false;
    }

    // 官方所需空间 = 解压大小总和 + 1GiB 余量（覆盖下载缓存/临时文件）
    private const long ExtraSpaceBuffer = 1L << 30;

    /// <summary>
    /// 刷新容量汇总（下载资源=勾选项压缩大小，所需空间=勾选项解压大小+1GiB余量）
    /// </summary>
    private void RefreshSummary()
    {
        long compressed = _gameCompressed;
        long uncompressed = _gameUncompressed;
        if (ChineseCheckBox.IsChecked == true)
        {
            var s = _voiceSizes.GetValueOrDefault(VoiceLanguageType.Chinese);
            compressed += s.Compressed;
            uncompressed += s.Uncompressed;
        }
        if (EnglishCheckBox.IsChecked == true)
        {
            var s = _voiceSizes.GetValueOrDefault(VoiceLanguageType.English);
            compressed += s.Compressed;
            uncompressed += s.Uncompressed;
        }
        if (JapaneseCheckBox.IsChecked == true)
        {
            var s = _voiceSizes.GetValueOrDefault(VoiceLanguageType.Japanese);
            compressed += s.Compressed;
            uncompressed += s.Uncompressed;
        }
        if (KoreanCheckBox.IsChecked == true)
        {
            var s = _voiceSizes.GetValueOrDefault(VoiceLanguageType.Korean);
            compressed += s.Compressed;
            uncompressed += s.Uncompressed;
        }

        SummarySizeText.Text =
            $"{string.Format(Lang.DownloadDialog_DownloadSize, FormatSize(compressed))}  |  {string.Format(Lang.DownloadDialog_RequiredSpace, FormatSize(uncompressed + ExtraSpaceBuffer))}";
    }

    private long RequiredSpace => _gameUncompressed +
                                  _voiceSizes.Where(pair => SelectedVoices.HasFlag(pair.Key))
                                      .Sum(pair => pair.Value.Uncompressed) + ExtraSpaceBuffer;

    /// <summary>
    /// 与官方一致：按二进制换算（1024³）显示 GB
    /// </summary>
    private static string FormatSize(long bytes)
    {
        return $"{bytes / 1073741824.0:0.00}GB";
    }

    /// <summary>
    /// 版本输入变化时触发，HomePage 据此重新拉取容量
    /// </summary>
    public event EventHandler? VersionChanged;

    // 版本是否被用户手动修改（用于切换服区时决定是否清空自动填入的版本）
    private bool _versionEdited;

    private void GameVersionTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing) return;
        _versionEdited = true;
        VersionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RegionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;

        var newRegion = RegionComboBox.SelectedIndex == 1 ? ServerRegionType.OS : ServerRegionType.CN;
        if (newRegion == Region) return;
        Region = newRegion;

        // 版本号尚未手动修改时清空，让 HomePage 拉取新服区的最新版本
        if (!_versionEdited)
        {
            _isInitializing = true;
            GameVersionTextBox.Text = string.Empty;
            _isInitializing = false;
        }

        RegionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void VoiceCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        UpdateSelectedVoices();
        RefreshSummary();
    }

    private void UpdateSelectedVoices()
    {
        SelectedVoices = VoiceLanguageType.None;
        if (ChineseCheckBox.IsChecked == true) SelectedVoices |= VoiceLanguageType.Chinese;
        if (EnglishCheckBox.IsChecked == true) SelectedVoices |= VoiceLanguageType.English;
        if (JapaneseCheckBox.IsChecked == true) SelectedVoices |= VoiceLanguageType.Japanese;
        if (KoreanCheckBox.IsChecked == true) SelectedVoices |= VoiceLanguageType.Korean;
    }

    private async void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            var folderPath = await FolderPickerHelper.PickFolderAsync(hwnd, Lang.DownloadDialog_Title);
            if (!string.IsNullOrEmpty(folderPath))
                DownloadPathText.Text = folderPath;
        }
        catch (Exception ex)
        {
            LogService.Error("选择安装路径失败", ex);
        }
    }

    private void LocateGameButton_Click(object sender, RoutedEventArgs e)
    {
        // 通知 HomePage 走定位流程（由外部关闭本弹窗）
        LocateGameRequested?.Invoke(this, EventArgs.Empty);
    }

    private void StartInstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ResourceReady)
        {
            ValidationText.Text = Lang.DownloadPage_NoResourceInfo;
            return;
        }

        if (!TryValidateInstallPath())
            return;

        Confirmed = true;
        Hide();
    }

    private bool TryValidateInstallPath()
    {
        try
        {
            string path = InstallPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                ValidationText.Text = Lang.DownloadDialog_InvalidPath;
                return false;
            }

            path = Path.GetFullPath(path);
            string? root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root) ||
                string.Equals(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                ValidationText.Text = Lang.DownloadDialog_InvalidPath;
                return false;
            }

            Directory.CreateDirectory(path);
            var drive = new DriveInfo(root);
            if (!drive.IsReady)
            {
                ValidationText.Text = Lang.DownloadDialog_InvalidPath;
                return false;
            }

            // 容量信息尚未加载时不以默认的 1 GiB 估算阻断安装。
            if (RequiredSpace > ExtraSpaceBuffer && drive.AvailableFreeSpace < RequiredSpace)
            {
                ValidationText.Text = string.Format(Lang.DownloadDialog_InsufficientSpace,
                    FormatSize(RequiredSpace), FormatSize(drive.AvailableFreeSpace));
                return false;
            }

            DownloadPathText.Text = path;
            ValidationText.Text = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            ValidationText.Text = Lang.DownloadDialog_InvalidPath;
            return false;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }
}
