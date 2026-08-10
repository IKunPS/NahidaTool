using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using NahidaTool.Models;
using NahidaTool.Models.Config;
using NahidaTool.Models.Event;
using NahidaTool.Models.Service;

namespace NahidaTool.Pages;

public sealed partial class LdiffUpdateDialog : ContentDialog
{
    private readonly ApiService _apiService;
    private readonly AppSettings _settings;
    private LdiffUpdateInfo? _update;
    private CancellationTokenSource? _cts;
    private bool _running;
    private bool _completed;
    private bool _closeAfterCancellation;

    public LdiffUpdateDialog(ApiService apiService, AppSettings settings)
    {
        InitializeComponent();
        _apiService = apiService;
        _settings = settings;
    }

    private async void ContentDialog_Opened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        try
        {
            string sourceVersion = ReadInstalledVersion(_settings.GameInstallPath, _settings.GameVersion);
            CurrentVersionText.Text = sourceVersion;
            _update = await new LdiffPatchService(_apiService)
                .GetAvailableUpdateAsync(sourceVersion, _settings.VoiceLanguage);

            if (_update == null)
            {
                StatusText.Text = Lang.Ldiff_NoUpdate;
                UpdateProgressBar.IsIndeterminate = false;
                UpdateProgressBar.Value = 0;
                return;
            }

            TargetVersionText.Text = _update.TargetVersion;
            DownloadSizeText.Text = FormatSize(_update.DownloadSize);
            StatusText.Text = string.Format(Lang.Ldiff_Ready, _update.Resources.Count);
            IsPrimaryButtonEnabled = true;
            UpdateProgressBar.IsIndeterminate = false;
        }
        catch (Exception ex)
        {
            StatusText.Text = string.Format(Lang.Ldiff_Failed, ex.Message);
            UpdateProgressBar.IsIndeterminate = false;
            LogService.Error("检查 LDiff 更新失败", ex);
        }
    }

    private async void ContentDialog_PrimaryButtonClick(
        ContentDialog sender,
        ContentDialogButtonClickEventArgs args)
    {
        if (_completed)
            return;

        args.Cancel = true;
        if (_running || _update == null)
            return;

        if (GameLauncherService.GetRunningProcess(_settings.Region) != null)
        {
            StatusText.Text = Lang.Ldiff_GameRunning;
            return;
        }

        ContentDialogButtonClickDeferral deferral = args.GetDeferral();
        _running = true;
        IsPrimaryButtonEnabled = false;
        CloseButtonText = Lang.Ldiff_Cancel;
        _cts = new CancellationTokenSource();

        var service = new LdiffPatchService(_apiService);
        service.StatusChanged += status => DispatcherQueue.TryEnqueue(() => StatusText.Text = status);
        service.ProgressChanged += (ratio, current, total) => DispatcherQueue.TryEnqueue(() =>
        {
            UpdateProgressBar.IsIndeterminate = false;
            UpdateProgressBar.Value = ratio * 100;
            ProgressText.Text = total > 0
                ? $"{ratio:P1}  ({FormatSize(current)} / {FormatSize(total)})"
                : $"{ratio:P1}";
        });

        try
        {
            string localLdiff = Path.Combine(_settings.GameInstallPath, "ldiff");
            await service.ApplyUpdateAsync(_update, _settings.GameInstallPath,
                Directory.Exists(localLdiff) ? localLdiff : null, _cts.Token);

            AppSettings.Update(settings => settings.GameVersion = _update.TargetVersion);
            _settings.GameVersion = _update.TargetVersion;
            GameInstallPathChangedMessage.Send();

            _completed = true;
            StatusText.Text = string.Format(Lang.Ldiff_Success, _update.TargetVersion);
            ProgressText.Text = "100%";
            UpdateProgressBar.Value = 100;
            PrimaryButtonText = Lang.Ldiff_Done;
            CloseButtonText = string.Empty;
            IsPrimaryButtonEnabled = true;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = Lang.Ldiff_Cancelled;
            if (_closeAfterCancellation)
                Hide();
        }
        catch (Exception ex)
        {
            StatusText.Text = string.Format(Lang.Ldiff_Failed, ex.Message);
            IsPrimaryButtonEnabled = true;
            LogService.Error("LDiff 更新失败", ex);
        }
        finally
        {
            _running = false;
            _cts?.Dispose();
            _cts = null;
            if (!_completed)
                CloseButtonText = Lang.Ldiff_Close;
            deferral.Complete();
        }
    }

    private void ContentDialog_Closing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (!_running)
            return;

        args.Cancel = true;
        _closeAfterCancellation = true;
        StatusText.Text = Lang.Ldiff_Cancelling;
        _cts?.Cancel();
    }

    private static string ReadInstalledVersion(string gameRoot, string fallback)
    {
        string configPath = Path.Combine(gameRoot, "config.ini");
        if (File.Exists(configPath))
        {
            foreach (string line in File.ReadLines(configPath))
            {
                if (line.StartsWith("game_version=", StringComparison.OrdinalIgnoreCase))
                    return line["game_version=".Length..].Trim();
            }
        }
        return fallback;
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
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
