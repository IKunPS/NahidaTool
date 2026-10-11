using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NahidaTool.Models;
using NahidaTool.Models.Config;
using NahidaTool.Models.Enum;
using NahidaTool.Models.Event;
using NahidaTool.Models.Service;

namespace NahidaTool.Pages;

public sealed partial class ServerSwitchPage : Page
{
    private string _gamePath = string.Empty;
    private InstalledGameInfo? _installation;
    private GameInstallationIssue? _detectionIssue;
    private bool _detectionTimedOut;
    private bool _unexpectedDetectionFailure;
    private bool _isActive;
    private bool _isDetecting;
    private bool _isSwitching;
    private bool _isConfirming;
    private CancellationTokenSource? _detectionCts;
    private ServerRegionType? _switchTargetRegion;
    private string _switchPath = string.Empty;
    private string _switchVersion = string.Empty;
    private readonly ServerSwitchService _switchService;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Queue<string> _detailLines = new();
    private readonly DispatcherTimer _detailRefresh = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private const int MaximumDetailLines = 200;

    public ServerSwitchPage()
    {
        InitializeComponent();
        _switchService = ServerSwitchService.Instance;
        _dispatcherQueue = DispatcherQueue;
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        // This cached page subscribes once to the operation it owns. Navigation
        // subscriptions are attached only while visible, and detection is canceled
        // on departure so stale results cannot overwrite a later visit.
        _switchService.StatusChanged += OnStatusChanged;
        _switchService.ProgressChanged += OnProgressChanged;
        _switchService.DetailAdded += OnDetailAdded;
        _switchService.SwitchCompleted += OnSwitchCompleted;
        _switchService.SwitchFailed += OnSwitchFailed;
        _detailRefresh.Tick += (_, _) =>
        {
            _detailRefresh.Stop();
            DetailText.Text = string.Join(string.Empty, _detailLines);
            DetailScrollViewer.ChangeView(null, DetailScrollViewer.ScrollableHeight, null);
        };
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _isActive = true;
        GameInstallPathChangedMessage.PathChanged += OnGameInstallPathChanged;
        LanguageChangedMessage.LanguageChanged += OnLanguageChanged;
        LoadGamePath();
        if (_isSwitching)
            RefreshServerDisplay();
        else
            _ = DetectCurrentServerAsync();
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        _isActive = false;
        GameInstallPathChangedMessage.PathChanged -= OnGameInstallPathChanged;
        LanguageChangedMessage.LanguageChanged -= OnLanguageChanged;
        _detectionCts?.Cancel();
        _detectionCts = null;
        _isDetecting = false;
        DetectionProgress.IsActive = false;
        CancelDownloadSizeCalculation();
        _detailRefresh.Stop();
        base.OnNavigatedFrom(e);
    }

    private void OnLanguageChanged() => DispatchUi(() =>
    {
        Bindings.Update();
        RefreshServerDisplay();
    });

    private void LoadGamePath() => _gamePath = AppSettings.Load().GameInstallPath?.Trim() ?? string.Empty;

    private void OnGameInstallPathChanged() => DispatchUi(() =>
    {
        if (!_isActive || _isSwitching) return;
        LoadGamePath();
        _ = DetectCurrentServerAsync();
    });

    private void RefreshServerDisplay()
    {
        if (_isDetecting)
            CurrentServerText.Text = Lang.ServerSwitchPage_Detecting;
        else if (_installation is { } installed)
            CurrentServerText.Text = $"{(installed.Region == ServerRegionType.CN ? Lang.ServerSwitchPage_CN_Display : Lang.ServerSwitchPage_OS_Display)} - {Lang.DownloadPage_Version} {installed.Version}";
        else if (_detectionTimedOut)
            CurrentServerText.Text = Lang.ServerSwitchPage_DetectionTimeout;
        else if (_detectionIssue is { } issue)
            CurrentServerText.Text = Lang.GameInstallationIssueMessage(issue);
        else if (_unexpectedDetectionFailure)
            CurrentServerText.Text = Lang.ServerSwitchPage_DetectionFailed;
        else
            CurrentServerText.Text = Lang.ServerSwitchPage_NotSelectedPath;

        bool failed = !_isDetecting && _installation == null && (_detectionIssue != null || _detectionTimedOut || _unexpectedDetectionFailure);
        CurrentServerText.Foreground = (Brush)Application.Current.Resources[
            failed ? "SystemFillColorCriticalBrush" : "TextFillColorPrimaryBrush"];
        DetectionProgress.IsActive = _isDetecting;
        DetectionProgress.Visibility = _isDetecting ? Visibility.Visible : Visibility.Collapsed;
        bool canSwitch = _installation != null && !_isDetecting && !_isSwitching && !_isConfirming && !_switchService.IsSwitching;
        ChinaServerButton.IsEnabled = canSwitch && _installation!.Region != ServerRegionType.CN;
        GlobalServerButton.IsEnabled = canSwitch && _installation!.Region != ServerRegionType.OS;
        DetectButton.IsEnabled = !_isDetecting && !_isSwitching && !_isConfirming && !_switchService.IsSwitching;
        RefreshDownloadSizeDisplay();
    }

    private async Task DetectCurrentServerAsync()
    {
        if (!_isActive || _isSwitching) return;
        CancelDownloadSizeCalculation();
        var request = new CancellationTokenSource();
        var previous = _detectionCts;
        _detectionCts = request;
        previous?.Cancel();
        string path = _gamePath;
        _installation = null;
        _detectionIssue = null;
        _detectionTimedOut = false;
        _unexpectedDetectionFailure = false;
        _isDetecting = true;
        RefreshServerDisplay();

        bool IsCurrent() => _isActive && ReferenceEquals(_detectionCts, request) &&
            string.Equals(path, _gamePath, StringComparison.OrdinalIgnoreCase);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(request.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            CancellationToken token = timeout.Token;
            // File-system metadata checks can block on disconnected network drives.
            // The UI wait is bounded and every result is tied to its request and path.
            var installed = await Task.Run(() => GameInstallationReader.ReadAsync(path, token), token)
                .WaitAsync(token);
            if (IsCurrent())
                _installation = installed;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (OperationCanceledException)
        {
            if (IsCurrent())
                _detectionTimedOut = true;
        }
        catch (GameInstallationDetectionException ex)
        {
            if (IsCurrent())
                _detectionIssue = ex.Issue;
            LogService.Warn($"转服检测失败: {ex.Issue}");
        }
        catch (Exception ex)
        {
            if (IsCurrent())
                _unexpectedDetectionFailure = true;
            LogService.Error($"转服检测发生异常: {ex.GetType().Name}");
        }
        finally
        {
            if (IsCurrent())
            {
                _detectionCts = null;
                _isDetecting = false;
                RefreshServerDisplay();
                if (_installation is { } installed)
                    _downloadSizeTask = CalculateDownloadSizeAsync(path, installed);
            }
            request.Dispose();
        }
    }

    private async void DetectButton_Click(object sender, RoutedEventArgs e)
    {
        LoadGamePath();
        await DetectCurrentServerAsync();
    }

    private async void ChinaServerButton_Click(object sender, RoutedEventArgs e) => await ConfirmSwitchAsync(ServerRegionType.CN);
    private async void GlobalServerButton_Click(object sender, RoutedEventArgs e) => await ConfirmSwitchAsync(ServerRegionType.OS);

    private async Task ConfirmSwitchAsync(ServerRegionType target)
    {
        if (_installation == null || _isDetecting || _isSwitching || _isConfirming || _switchService.IsSwitching) return;
        if (_installation.Region == target) return;
        var source = _installation;
        string path = _gamePath;
        _isConfirming = true;
        RefreshServerDisplay();
        bool confirmed;
        try
        {
            // Share the current preview instead of issuing another manifest request
            // on click. Navigation/path changes cancel it and invalidate the dialog.
            if (_downloadSizeTask is { } calculation)
                await calculation;
            if (!_isActive || _installation != source ||
                !string.Equals(path, _gamePath, StringComparison.OrdinalIgnoreCase)) return;
            confirmed = await ShowConfirmDialogAsync(Lang.ServerSwitchPage_ConfirmSwitch,
                string.Format(target == ServerRegionType.CN ? Lang.ServerSwitchPage_ConfirmSwitchToCN : Lang.ServerSwitchPage_ConfirmSwitchToOS,
                    source.Version, GetDownloadSizeDescription()));
        }
        finally
        {
            _isConfirming = false;
            RefreshServerDisplay();
        }
        if (!confirmed || !_isActive || !string.Equals(path, _gamePath, StringComparison.OrdinalIgnoreCase)) return;
        await StartSwitchAsync(path, source, target);
    }

    private async Task StartSwitchAsync(string path, InstalledGameInfo source, ServerRegionType target)
    {
        _switchTargetRegion = target;
        _switchPath = path;
        _switchVersion = source.Version;
        _isSwitching = true;
        ProgressCard.Visibility = Visibility.Visible;
        _detailRefresh.Stop();
        _detailLines.Clear();
        DetailText.Text = string.Empty;
        SwitchProgressBar.Value = 0;
        ProgressPercentText.Text = "0%";
        StatusText.Text = Lang.ServerSwitchPage_Preparing;
        RefreshServerDisplay();
        try
        {
            await _switchService.SwitchServerAsync(path, target, _dispatcherQueue);
        }
        finally
        {
            _isSwitching = false;
            RefreshServerDisplay();
        }
    }

    private void OnStatusChanged(object? sender, string status) => DispatchUi(() => StatusText.Text = status);

    private void OnProgressChanged(object? sender, double progress) => DispatchUi(() =>
    {
        double value = double.IsFinite(progress) ? Math.Clamp(progress, 0, 100) : 0;
        SwitchProgressBar.Value = value;
        ProgressPercentText.Text = $"{value:F0}%";
    });

    private void OnDetailAdded(object? sender, string detail) => DispatchUi(() =>
    {
        _detailLines.Enqueue($"[{DateTime.Now:HH:mm:ss}] {detail}\n");
        while (_detailLines.Count > MaximumDetailLines) _detailLines.Dequeue();
        if (_isActive && !_detailRefresh.IsEnabled) _detailRefresh.Start();
    });

    private async void OnSwitchCompleted(object? sender, EventArgs e)
    {
        try
        {
            if (_switchTargetRegion is not { } completedRegion) return;
            _switchTargetRegion = null;
            _installation = new InstalledGameInfo(completedRegion, _switchVersion);
            AppSettings.Update(settings =>
            {
                if (string.Equals(settings.GameInstallPath, _switchPath, StringComparison.OrdinalIgnoreCase))
                    settings.Region = completedRegion;
            });
            GameInstallPathChangedMessage.Send();
            _isSwitching = false;
            RefreshServerDisplay();
            if (!_isActive) return;
            LoadGamePath();
            await DetectCurrentServerAsync();
            if (_isActive && XamlRoot != null)
                await new ServerSwitchCompleteDialog(completedRegion, _switchVersion) { XamlRoot = XamlRoot }.ShowAsync();
        }
        catch (Exception ex)
        {
            LogService.Error($"显示转服完成状态失败: {ex.GetType().Name}");
        }
    }

    private async void OnSwitchFailed(object? sender, string error)
    {
        _switchTargetRegion = null;
        _isSwitching = false;
        RefreshServerDisplay();
        if (!_isActive) return;
        try
        {
            await ShowMessageAsync(Lang.ServerSwitchPage_SwitchFailed,
                string.Format(Lang.ServerSwitchPage_SwitchFailedMessage, error));
        }
        catch (Exception ex)
        {
            LogService.Error($"显示转服失败提示失败: {ex.GetType().Name}");
        }
    }

    private void DispatchUi(Action action)
    {
        if (_dispatcherQueue.HasThreadAccess)
            action();
        else if (!_dispatcherQueue.TryEnqueue(() => action()))
            LogService.Warn("转服页面已关闭，无法更新显示状态");
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        if (XamlRoot != null)
            await new NoticeDialog(title, message) { CloseButtonText = Lang.ServerSwitchPage_OK, XamlRoot = XamlRoot }.ShowAsync();
    }

    private async Task<bool> ShowConfirmDialogAsync(string title, string message)
    {
        if (XamlRoot == null) return false;
        var dialog = NoticeDialog.CreateConfirmation(title, message, Lang.ServerSwitchPage_ConfirmSwitch, Lang.ServerSwitchPage_Cancel);
        dialog.XamlRoot = XamlRoot;
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
