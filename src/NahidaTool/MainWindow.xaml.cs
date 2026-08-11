using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using NahidaTool.Frameworks;
using NahidaTool.Pages;
using Windows.Media.Core;
using Windows.Media.Playback;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Windows.UI;
using Microsoft.Graphics.Canvas;
using NahidaTool.Models.Config;
using NahidaTool.Models.Enum;
using NahidaTool.Models.Event;
using NahidaTool.Models.Helper;
using NahidaTool.Models.Service;

namespace NahidaTool;

public sealed partial class MainWindow : WindowEx
{
    private AppSettings _settings;
    private ApiService _apiService;
    private bool _changelogShown;

    public MainWindow()
    {
        InitializeComponent();
        InitializeMainWindow();
        LoadContent();
    }

    private void InitializeMainWindow()
    {
        Title = "NahidaTool";
        AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        CenterInScreen(1200, 676);
        AdaptTitleBarButtonColorToActuallTheme();
        SetDragRectangles(new RectInt32(0, 0, 100000, (int)(48 * UIScale)));

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.IsResizable = false;
            presenter.IsMinimizable = true;
        }
    }

    private void LoadContent()
    {
        _apiService = new ApiService();

        _settings = AppSettings.Load();
        LogService.Debug($"设置加载完成: 区域={_settings.Region}, 版本={_settings.GameVersion}");

        if (string.IsNullOrEmpty(_settings.GameVersion))
            _ = FetchAndSaveLatestVersionAsync();

        ContentFrame.Navigated += ContentFrame_Navigated;
        NavigateToInitialPage();
        Activated += MainWindow_Activated;

        BackgroundChangedMessage.BackgroundChanged -= OnBackgroundChanged;
        BackgroundChangedMessage.BackgroundChanged += OnBackgroundChanged;
        AccentColorChangedMessage.AccentColorChanged -= OnAccentColorChanged;
        AccentColorChangedMessage.AccentColorChanged += OnAccentColorChanged;
        RefreshBackground();
        RestoreAccentColor();

        // 参考 Starward：初始化系统托盘
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (App.Current is App app)
                app.EnsureSystemTray();
        });

        AppWindow.Closing += MainWindow_AppWindow_Closing;
        Closed += MainWindow_Closed;
    }

    private void NavigateToInitialPage()
    {
        bool shouldShowTutorial = !string.Equals(
            _settings.LastShownTutorialVersion,
            AppVersion.Current,
            StringComparison.OrdinalIgnoreCase);

        if (shouldShowTutorial &&
            ContentFrame.Navigate(typeof(Pages.SettingPages.DocumentSettingPage)))
        {
            MainNavView.SelectedItem = NavigationViewItem_Document;
            return;
        }

        MainNavView.SelectedItem = NavigationViewItem_Home;
        ContentFrame.Navigate(typeof(HomePage), _settings);
    }

    private void MainWindow_AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        var settings = AppSettings.Load();
        if (settings.CloseWindowOption == CloseWindowOption.Hide)
        {
            args.Cancel = true;
            StopVideoPlayer();
            Hide();
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        StopVideoPlayer();
        Environment.Exit(0);
    }

    private void OnBackgroundChanged()
    {
        DispatcherQueue.TryEnqueue(RefreshBackground);
    }

    /// <summary>
    /// 参考 Starward：短暂翻转 RequestedTheme 强制 WinUI 重新求值所有 ThemeResource 绑定。
    /// 只改 Content 的 RequestedTheme，不影响整个 Window。
    /// </summary>
    private void OnAccentColorChanged(Color _)
    {
        if (Content is not FrameworkElement root) return;

        // Dark → Light → Default：强制 ThemeResource 全树重新求值
        root.RequestedTheme = root.ActualTheme switch
        {
            ElementTheme.Light => ElementTheme.Dark,
            ElementTheme.Dark => ElementTheme.Light,
            _ => ElementTheme.Default,
        };
        root.RequestedTheme = ElementTheme.Default;
    }

    private bool _bgFirstActive = true;
    private string? _lastBgPath;
    private MediaPlayer? _bgMediaPlayer;
    private CancellationTokenSource? _bgFadeCts;
    private CanvasRenderTarget? _videoSurface;
    private CanvasImageSource? _videoImageSource;
    private readonly SemaphoreSlim _videoSemaphore = new(1, 1);

    public void RefreshBackground()
    {
        var settings = AppSettings.Load();
        string targetPath;
        if (settings.EnableCustomBg && !string.IsNullOrEmpty(settings.CustomBg) && File.Exists(settings.CustomBg))
            targetPath = settings.CustomBg;
        else
            targetPath = AppContext.BaseDirectory + "Assets/Nahida.png";

        if (string.Equals(_lastBgPath, targetPath, StringComparison.OrdinalIgnoreCase))
            return;
        _lastBgPath = targetPath;

        var ext = Path.GetExtension(targetPath).ToLowerInvariant();
        bool isVideo = ext is ".mp4" or ".webm" or ".avi" or ".wmv" or ".mov";

        _bgFadeCts?.Cancel();
        _bgFadeCts = new CancellationTokenSource();

        if (isVideo)
        {
            StartVideoPlayer(targetPath, settings.VideoBgVolume);
        }
        else
        {
            StopVideoPlayer();

            var currentActive = _bgFirstActive ? BackgroundImage1 : BackgroundImage2;
            var nextActive = _bgFirstActive ? BackgroundImage2 : BackgroundImage1;

            var newImage = new BitmapImage();
            var bgPath = targetPath;
            Action? doFade = null;
            doFade = () =>
            {
                newImage.ImageOpened -= (_, _) => doFade?.Invoke();
                if (_bgFadeCts!.IsCancellationRequested) return;
                currentActive.Opacity = 0;
                nextActive.Opacity = 1;
                _bgFirstActive = !_bgFirstActive;
            };
            newImage.ImageOpened += (_, _) => doFade?.Invoke();
            newImage.UriSource = new Uri(targetPath);
            nextActive.Source = newImage;
            _ = UpdateAccentColorFromImageAsync(bgPath);
        }
    }

    private void StartVideoPlayer(string file, int volume)
    {
        StopVideoPlayer();

        var active = _bgFirstActive ? BackgroundImage1 : BackgroundImage2;
        var inactive = _bgFirstActive ? BackgroundImage2 : BackgroundImage1;
        inactive.Opacity = 0;
        active.Opacity = 0;

        _bgMediaPlayer = new MediaPlayer
        {
            IsLoopingEnabled = true,
            Volume = Math.Clamp(volume, 0, 100) / 100.0,
            IsMuted = false,
            IsVideoFrameServerEnabled = true,
            Source = MediaSource.CreateFromUri(new Uri(file))
        };
        _bgMediaPlayer.CommandManager.IsEnabled = false;
        _bgMediaPlayer.SystemMediaTransportControls.IsEnabled = false;
        _bgMediaPlayer.VideoFrameAvailable += MediaPlayer_VideoFrameAvailable;
        _bgMediaPlayer.Play();
    }

    private void MediaPlayer_VideoFrameAvailable(MediaPlayer sender, object args)
    {
        if (_videoSemaphore.CurrentCount == 0)
            return;
        _videoSemaphore.Wait();
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                var active = _bgFirstActive ? BackgroundImage1 : BackgroundImage2;
                if (_videoSurface is null || _videoImageSource is null)
                {
                    _videoSurface?.Dispose();
                    int width = (int)sender.PlaybackSession.NaturalVideoWidth;
                    int height = (int)sender.PlaybackSession.NaturalVideoHeight;
                    if (width <= 0) width = 1920;
                    if (height <= 0) height = 1080;
                    _videoSurface = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, 96);
                    _videoImageSource = new CanvasImageSource(CanvasDevice.GetSharedDevice(), width, height, 96);
                    active.Source = _videoImageSource;
                    active.Opacity = 1;
                    _bgFirstActive = !_bgFirstActive;
                }
                sender.CopyFrameToVideoSurface(_videoSurface);
                using var ds = _videoImageSource.CreateDrawingSession(Microsoft.UI.Colors.Transparent);
                ds.DrawImage(_videoSurface);
            }
            catch (Exception ex)
            {
                LogService.Debug($"视频背景帧处理失败: {ex.Message}");
            }
            finally
            {
                _videoSemaphore.Release();
            }
        });
    }

    private void StopVideoPlayer()
    {
        if (_bgMediaPlayer != null)
        {
            _bgMediaPlayer.VideoFrameAvailable -= MediaPlayer_VideoFrameAvailable;
            _bgMediaPlayer.Pause();
            _bgMediaPlayer.Dispose();
            _bgMediaPlayer = null;
        }
        _videoSurface?.Dispose();
        _videoSurface = null;
        _videoImageSource = null;
    }

    private async Task UpdateAccentColorFromImageAsync(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return;

            Color? color = await Task.Run(async () =>
            {
                using var fs = File.OpenRead(filePath);
                var decoder = await BitmapDecoder.CreateAsync(fs.AsRandomAccessStream());
                int decodeWidth = (int)decoder.PixelWidth;
                int decodeHeight = (int)decoder.PixelHeight;

                if (decodeWidth > 1920 || decodeHeight > 1080)
                {
                    double scale = Math.Min(1920.0 / decodeWidth, 1080.0 / decodeHeight);
                    decodeWidth = (int)(decodeWidth * scale);
                    decodeHeight = (int)(decodeHeight * scale);
                }

                var pixelData = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    new BitmapTransform
                    {
                        ScaledWidth = (uint)decodeWidth,
                        ScaledHeight = (uint)decodeHeight,
                        InterpolationMode = BitmapInterpolationMode.Fant
                    },
                    ExifOrientationMode.IgnoreExifOrientation,
                    ColorManagementMode.DoNotColorManage);

                return AccentColorHelper.GetAccentColor(pixelData.DetachPixelData(), decodeWidth, decodeHeight);
            });

            if (color is not null)
            {
                AccentColorHelper.ChangeAppAccentColor(color);
                _settings = AppSettings.Update(settings => settings.AccentColor = color.Value.ToHex());
            }
        }
        catch (Exception ex)
        {
            LogService.Debug($"提取主题色失败: {ex.Message}");
        }
    }

    private void RestoreAccentColor()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_settings.AccentColor))
            {
                Color color = AccentColorHelper.ToColor(_settings.AccentColor);
                AccentColorHelper.ChangeAppAccentColor(color);
            }
        }
        catch (Exception ex)
        {
            LogService.Debug($"提取主题色失败: {ex.Message}");
        }
    }

    public void UpdateVideoVolume(int volume)
    {
        if (_bgMediaPlayer != null)
            _bgMediaPlayer.Volume = Math.Clamp(volume, 0, 100) / 100.0;
    }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (_changelogShown || args.WindowActivationState == WindowActivationState.Deactivated)
            return;

        _changelogShown = true;

        await Task.Delay(500);
        try
        {
            var updateService = new AppUpdateService(_settings);
            AppUpdateInfo? update = await updateService.CheckForUpdateAsync(AppVersion.Current);
            if (update != null && Content?.XamlRoot != null)
            {
                await new AppUpdateDialog(update, updateService)
                {
                    XamlRoot = Content.XamlRoot
                }.ShowAsync();
            }
        }
        catch (Exception ex)
        {
            LogService.Debug($"Application update check failed: {ex.Message}");
        }


        if (_settings.LastShownChangelogVersion != AppVersion.Current)
        {
            try
            {
                await ShowChangelogDialogAsync();
                _settings = AppSettings.Update(settings =>
                    settings.LastShownChangelogVersion = AppVersion.Current);
            }
            catch (Exception ex) { LogService.Error("显示更新日志弹窗失败", ex); }
        }
    }

    private async Task ShowChangelogDialogAsync()
    {
        if (Content?.XamlRoot == null) return;
        var changelog = string.Join("\n", new[]
        {
            "v0.1.4",
            "· 重做程序更新弹窗，统一主题样式，优化版本信息、更新说明与下载进度展示",
            "· 每个程序版本首次启动时自动打开教程页，并在文档页增加视频教程提示",
            "· 优化预下载版本资源获取，正式分支不可用时可安全回退到对应预下载分支",
            "· 强化 LDiff 更新前置检查，缺少或损坏官方增量包时提示先通过官方启动器预下载或修复",
            "· LDiff 新增修补记录、原文件备份与异常恢复，问题文件可清理后重新修补",
            "· 程序更新时自动暂存转服资源，删除旧版 app 后恢复，并支持更新中断后的再次恢复"
        });
        await new ContentDialog
        {
            Title = $"NahidaTool v{AppVersion.Current} 更新日志",
            Content = new ScrollViewer
            {
                Content = new TextBlock { Text = changelog, TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 22 },
                MaxHeight = 400
            },
            CloseButtonText = "我知道了",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot
        }.ShowAsync();
    }

    private void MainNavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.IsSettingsInvoked)
        {
            if (ContentFrame.Content is not SettingsPage)
                ContentFrame.Navigate(typeof(SettingsPage), _settings);
            return;
        }

        var tag = args.InvokedItemContainer?.Tag?.ToString() ?? "";
        switch (tag)
        {
            case "Home":
                if (ContentFrame.Content is not HomePage)
                    ContentFrame.Navigate(typeof(HomePage), _settings);
                break;
            case "ServerSwitch":
                if (ContentFrame.Content is not ServerSwitchPage)
                    ContentFrame.Navigate(typeof(ServerSwitchPage));
                break;
            case "Document":
                if (ContentFrame.Content is not Pages.SettingPages.DocumentSettingPage)
                    ContentFrame.Navigate(typeof(Pages.SettingPages.DocumentSettingPage));
                break;
        }
    }

    private void ContentFrame_Navigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        if (e.SourcePageType == typeof(HomePage))
        {
            Border_OverlayMask.Opacity = 0;
        }
        else
        {
            Border_OverlayMask.Opacity = 1;
        }

        if (e.SourcePageType == typeof(Pages.SettingPages.DocumentSettingPage) &&
            !string.Equals(
                _settings.LastShownTutorialVersion,
                AppVersion.Current,
                StringComparison.OrdinalIgnoreCase))
        {
            _settings = AppSettings.Update(settings =>
                settings.LastShownTutorialVersion = AppVersion.Current);
            LogService.Debug($"Tutorial page shown for app version {AppVersion.Current}");
        }

        if (e.SourcePageType == typeof(SettingsPage) && e.Content is SettingsPage settingsPage)
        {
            // 下载设置已移至主页下载游戏对话框，此处无需订阅
        }
    }

    private async Task FetchAndSaveLatestVersionAsync()
    {
        try
        {
            _apiService.SetRegion(_settings.Region);
            var buildResponse = await _apiService.GetBuildInfoAsync();
            var latestVersion = buildResponse.Data?.Tag ?? "";
            if (!string.IsNullOrEmpty(latestVersion))
            {
                _settings = AppSettings.Update(settings => settings.GameVersion = latestVersion);
                LogService.Debug($"获取到最新版本: {latestVersion}");
            }
        }
        catch (Exception ex) { LogService.Error("获取最新游戏版本失败", ex); }
    }

}
