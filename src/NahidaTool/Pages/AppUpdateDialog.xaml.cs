using System;
using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NahidaTool.Models;
using NahidaTool.Models.Config;
using NahidaTool.Models.Service;

namespace NahidaTool.Pages;

public sealed partial class AppUpdateDialog : ContentDialog
{
    private readonly AppUpdateInfo _update;
    private readonly AppUpdateService _service;
    private CancellationTokenSource? _cts;
    private bool _running;

    public AppUpdateDialog(AppUpdateInfo update, AppUpdateService service)
    {
        InitializeComponent();
        _update = update;
        _service = service;
        VersionText.Text = string.Format(Lang.AppUpdate_VersionLine, AppVersion.Current, update.Version);
        string sourceText = string.Format(Lang.AppUpdate_Source, update.SourceServer);
        PublishedText.Text = update.PublishedAt == DateTimeOffset.MinValue
            ? sourceText
            : $"{string.Format(Lang.AppUpdate_Published, update.PublishedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))} · {sourceText}";
        ReleaseNotesText.Text = string.IsNullOrWhiteSpace(update.ReleaseNotes)
            ? Lang.AppUpdate_NoReleaseNotes
            : update.ReleaseNotes;
        _service.StatusChanged += status => DispatcherQueue.TryEnqueue(() => StatusText.Text = status);
    }

    private async void ContentDialog_PrimaryButtonClick(
        ContentDialog sender,
        ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (_running)
            return;

        ContentDialogButtonClickDeferral deferral = args.GetDeferral();
        _running = true;
        IsPrimaryButtonEnabled = false;
        CloseButtonText = Lang.AppUpdate_Cancel;
        UpdateProgressBar.Visibility = Visibility.Visible;
        _cts = new CancellationTokenSource();

        var progress = new Progress<AppUpdateProgress>(value =>
        {
            double ratio = value.Total > 0 ? Math.Clamp((double)value.Current / value.Total, 0, 1) : 0;
            UpdateProgressBar.Value = ratio * 100;
            StatusText.Text = string.Format(Lang.AppUpdate_Downloading,
                ratio, FormatSize(value.Current), FormatSize(value.Total));
        });

        try
        {
            AppUpdateStageResult staged = await _service.DownloadAndStageAsync(_update, progress, _cts.Token);
            StatusText.Text = Lang.AppUpdate_Restarting;
            AppUpdateService.RestartAndExit(staged);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = Lang.AppUpdate_Cancelled;
        }
        catch (Exception ex)
        {
            StatusText.Text = string.Format(Lang.AppUpdate_Failed, ex.Message);
            LogService.Error("程序自动更新失败", ex);
        }
        finally
        {
            _running = false;
            _cts?.Dispose();
            _cts = null;
            IsPrimaryButtonEnabled = true;
            CloseButtonText = Lang.AppUpdate_Close;
            deferral.Complete();
        }
    }

    private void ContentDialog_Closing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (!_running)
            return;

        args.Cancel = true;
        StatusText.Text = Lang.AppUpdate_Cancelling;
        _cts?.Cancel();
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:F2} {units[unit]}";
    }
}
