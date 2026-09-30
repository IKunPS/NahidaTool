using System;
using System.Threading.Tasks;
using Windows.Graphics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NahidaTool.Frameworks;
using NahidaTool.Pages;
using Windows.UI;
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
        DisposeBackground();
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
        await new ChangelogDialog { XamlRoot = Content.XamlRoot }.ShowAsync();
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
                NavigateToServerSwitch();
                break;
            case "Document":
                if (ContentFrame.Content is not Pages.SettingPages.DocumentSettingPage)
                    ContentFrame.Navigate(typeof(Pages.SettingPages.DocumentSettingPage));
                break;
        }
    }

    public void NavigateToServerSwitch()
    {
        MainNavView.SelectedItem = NavigationViewItem_ServerSwitch;
        if (ContentFrame.Content is not ServerSwitchPage)
            ContentFrame.Navigate(typeof(ServerSwitchPage));
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
