using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Dispatching;
using System;
using System.IO;
using System.Text;
using NahidaTool.Models.Config;
using NahidaTool.Models.Enum;
using NahidaTool.Models.Event;
using NahidaTool.Models.Service;
using System.Globalization;
using NahidaTool.Models;

namespace NahidaTool.Pages;

public sealed partial class ServerSwitchPage : Page
{
    private string _gamePath = string.Empty;
    private ServerRegionType _currentRegion = ServerRegionType.CN;
    private string _currentVersion = string.Empty;
    private ServerRegionType? _switchTargetRegion;
    private readonly ServerSwitchService _switchService;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly StringBuilder _detailLog = new();

    public ServerSwitchPage()
    {
        this.InitializeComponent();
        _switchService = ServerSwitchService.Instance;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        // 启用导航缓存，避免每次切换页面都创建新实例
        this.NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;

        // 订阅事件（页面使用NavigationCacheMode.Required缓存，
        // 构造函数只执行一次，事件只订阅一次，无需取消订阅）
        _switchService.StatusChanged += OnStatusChanged;
        _switchService.ProgressChanged += OnProgressChanged;
        _switchService.DetailAdded += OnDetailAdded;
        _switchService.SwitchCompleted += OnSwitchCompleted;
        _switchService.SwitchFailed += OnSwitchFailed;

        GameInstallPathChangedMessage.PathChanged -= OnGameInstallPathChanged;
        GameInstallPathChangedMessage.PathChanged += OnGameInstallPathChanged;
        LanguageChangedMessage.LanguageChanged -= OnLanguageChanged;
        LanguageChangedMessage.LanguageChanged += OnLanguageChanged;

        // 从配置加载游戏路径
        LoadGamePath();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        LoadGamePath();
        DetectCurrentServer();
    }

    private void OnLanguageChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            this.Bindings.Update();
            RefreshServerDisplay();
        });
    }

    private void RefreshServerDisplay()
    {
        if (string.IsNullOrEmpty(_gamePath))
        {
            CurrentServerText.Text = Lang.ServerSwitchPage_NotSelectedPath;
            return;
        }

        if (string.IsNullOrEmpty(_currentVersion))
        {
            CurrentServerText.Text = Lang.ServerSwitchPage_Detecting;
            return;
        }

        string regionName = _currentRegion == ServerRegionType.CN
            ? Lang.ServerSwitchPage_CN_Display
            : Lang.ServerSwitchPage_OS_Display;
        CurrentServerText.Text = $"{regionName} - {Lang.DownloadPage_Version} {_currentVersion}";
    }

    private void OnGameInstallPathChanged()
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            LoadGamePath();
            DetectCurrentServer();
        });
    }

    private void LoadGamePath()
    {
        var settings = AppSettings.Load();
        if (!string.IsNullOrEmpty(settings.GameInstallPath) && Directory.Exists(settings.GameInstallPath))
            _gamePath = settings.GameInstallPath;
        else
            _gamePath = string.Empty;
    }

    private async void DetectCurrentServer()
    {
        if (string.IsNullOrEmpty(_gamePath))
        {
            _dispatcherQueue.TryEnqueue(() => CurrentServerText.Text = Lang.ServerSwitchPage_NotSelectedPath);
            return;
        }

        _dispatcherQueue.TryEnqueue(() => CurrentServerText.Text = Lang.ServerSwitchPage_Detecting);

        try
        {
            var (region, version, error) = await _switchService.DetectCurrentServerAsync(_gamePath).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(error))
            {
                _dispatcherQueue.TryEnqueue(() => CurrentServerText.Text = error);
                return;
            }

            _currentRegion = region;
            _currentVersion = version;

            _dispatcherQueue.TryEnqueue(RefreshServerDisplay);
        }
        catch (Exception ex)
        {
            _dispatcherQueue.TryEnqueue(() => CurrentServerText.Text = ex.Message);
        }
    }

    private void DetectButton_Click(object sender, RoutedEventArgs e)
    {
        DetectCurrentServer();
    }

    private async void ChinaServerButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_gamePath))
        {
            await ShowMessageAsync(Lang.ServerSwitchPage_Tips, Lang.ServerSwitchPage_NotSelectedPath);
            return;
        }

        if (_currentRegion == ServerRegionType.CN)
        {
            await ShowMessageAsync(Lang.ServerSwitchPage_Tips, Lang.ServerSwitchPage_AlreadyCN);
            return;
        }

        // 确认对话框
        var result = await ShowConfirmDialogAsync(Lang.ServerSwitchPage_ConfirmSwitch,
            string.Format(Lang.ServerSwitchPage_ConfirmSwitchToCN, _currentVersion));

        if (result)
        {
            StartSwitch(ServerRegionType.CN);
        }
    }

    private async void GlobalServerButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_gamePath))
        {
            await ShowMessageAsync(Lang.ServerSwitchPage_Tips, Lang.ServerSwitchPage_NotSelectedPath);
            return;
        }

        if (_currentRegion == ServerRegionType.OS)
        {
            await ShowMessageAsync(Lang.ServerSwitchPage_Tips, Lang.ServerSwitchPage_AlreadyOS);
            return;
        }

        // 确认对话框
        var result = await ShowConfirmDialogAsync(Lang.ServerSwitchPage_ConfirmSwitch,
            string.Format(Lang.ServerSwitchPage_ConfirmSwitchToOS, _currentVersion));

        if (result)
        {
            StartSwitch(ServerRegionType.OS);
        }
    }

    private async void StartSwitch(ServerRegionType targetRegion)
    {
        _switchTargetRegion = targetRegion;
        // 显示进度卡片
        ProgressCard.Visibility = Visibility.Visible;
        ChinaServerButton.IsEnabled = false;
        GlobalServerButton.IsEnabled = false;
        DetectButton.IsEnabled = false;

        // 清空日志
        _detailLog.Clear();
        DetailText.Text = string.Empty;
        SwitchProgressBar.Value = 0;
        ProgressPercentText.Text = "0%";
        StatusText.Text = Lang.ServerSwitchPage_Preparing;

        // 开始转服
        await _switchService.SwitchServerAsync(_gamePath, targetRegion, _dispatcherQueue);
    }

    private void OnStatusChanged(object? sender, string status)
    {
        try
        {
            StatusText.Text = status;
        }
        catch
        {
        }
    }

    private void OnProgressChanged(object? sender, double progress)
    {
        try
        {
            SwitchProgressBar.Value = progress;
            ProgressPercentText.Text = $"{progress:F0}%";
        }
        catch
        {
        }
    }

    private void OnDetailAdded(object? sender, string detail)
    {
        try
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {detail}\n";
            _detailLog.Append(line);

            // 追加文本而非重建整个字符串
            DetailText.Text += line;

            // 滚动到底部
            DetailScrollViewer.ChangeView(null, DetailScrollViewer.ScrollableHeight, null);
        }
        catch
        {
        }
    }

    private async void OnSwitchCompleted(object? sender, EventArgs e)
    {
        try
        {
            // 恢复UI状态
            ChinaServerButton.IsEnabled = true;
            GlobalServerButton.IsEnabled = true;
            DetectButton.IsEnabled = true;

            // 重新检测
            ServerRegionType completedRegion = _switchTargetRegion ?? _currentRegion;
            _switchTargetRegion = null;
            _currentRegion = completedRegion;
            RefreshServerDisplay();
            DetectCurrentServer();

            if (this.XamlRoot != null)
            {
                var dialog = new ServerSwitchCompleteDialog(completedRegion, _currentVersion)
                {
                    XamlRoot = this.XamlRoot
                };
                await dialog.ShowAsync();
            }
        }
        catch
        {
            // 忽略UI错误
        }
    }

    private async void OnSwitchFailed(object? sender, string error)
    {
        try
        {
            // 恢复UI状态
            ChinaServerButton.IsEnabled = true;
            GlobalServerButton.IsEnabled = true;
            DetectButton.IsEnabled = true;

            _switchTargetRegion = null;

            await ShowMessageAsync(Lang.ServerSwitchPage_SwitchFailed, string.Format(Lang.ServerSwitchPage_SwitchFailedMessage, error));
        }
        catch
        {
            // 忽略UI错误
        }
    }

    private async System.Threading.Tasks.Task ShowMessageAsync(string title, string message)
    {
        if (this.XamlRoot == null)
            return;

        ContentDialog dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = Lang.ServerSwitchPage_OK,
            XamlRoot = this.XamlRoot
        };
        await dialog.ShowAsync();
    }

    private async System.Threading.Tasks.Task<bool> ShowConfirmDialogAsync(string title, string message)
    {
        ContentDialog dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = Lang.ServerSwitchPage_OK,
            CloseButtonText = Lang.ServerSwitchPage_Cancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }
}