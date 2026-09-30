using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NahidaTool.Models.Config;
using NahidaTool.Models.Event;
using NahidaTool.Models.Helper;
using NahidaTool.Models.Service;

namespace NahidaTool.Pages;

public sealed partial class HomeSettingDialog
{
    private CancellationTokenSource? _positionSaveCts;
    private bool _positionDirty;

    private void InitializeBackgroundSettings()
    {
        if (_settings == null) return;
        _positionSaveCts?.Cancel();
        _positionDirty = false;
        CustomBgToggleSwitch.IsOn = _settings.EnableCustomBg;
        CustomBgPathText.Text = _settings.CustomBg;
        VolumeSlider.Value = _videoBgVolume;
        UpdateVolumeIcon();
        ChangeCustomBgButton.IsEnabled = _settings.EnableCustomBg;
        CustomBgPathGrid.Visibility = string.IsNullOrEmpty(_settings.CustomBg)
            ? Visibility.Collapsed : Visibility.Visible;

        BackgroundPositionControl.PositionChanged -= OnBackgroundPositionChanged;
        BackgroundPositionControl.PositionChanged += OnBackgroundPositionChanged;
        Loaded -= BackgroundPosition_Loaded;
        Loaded += BackgroundPosition_Loaded;
        Closed -= BackgroundPosition_Closed;
        Closed += BackgroundPosition_Closed;
        ConfigureBackgroundPosition();
    }

    private void BackgroundPosition_Loaded(object sender, RoutedEventArgs e) => ConfigureBackgroundPosition();

    private void ConfigureBackgroundPosition()
    {
        if (_settings == null) return;
        double width = XamlRoot?.Size.Width is > 0 ? XamlRoot.Size.Width : 1200;
        double height = XamlRoot?.Size.Height is > 0 ? XamlRoot.Size.Height : 676;
        BackgroundPositionControl.Configure(
            BackgroundImageLoader.ResolveSourcePath(_settings.EnableCustomBg, _settings.CustomBg),
            width, height, _settings.BackgroundImageOffsetX, _settings.BackgroundImageOffsetY);
    }

    private void OnBackgroundPositionChanged(int horizontal, int vertical)
    {
        if (_isInitializing || _settings == null) return;
        _settings.BackgroundImageOffsetX = horizontal;
        _settings.BackgroundImageOffsetY = vertical;
        _positionDirty = true;
        _positionSaveCts?.Cancel();
        var request = new CancellationTokenSource();
        _positionSaveCts = request;
        _ = SaveBackgroundPositionAsync(request, horizontal, vertical);
    }

    private async Task SaveBackgroundPositionAsync(CancellationTokenSource request, int horizontal, int vertical)
    {
        try
        {
            await Task.Delay(220, request.Token);
            PersistBackgroundPosition(horizontal, vertical);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LogService.Error("保存背景图片位置失败", ex);
        }
        finally
        {
            if (ReferenceEquals(_positionSaveCts, request))
                _positionSaveCts = null;
            request.Dispose();
        }
    }

    private void PersistBackgroundPosition(int horizontal, int vertical)
    {
        // Only merge these fields so dragging does not overwrite settings saved
        // by another page or by the asynchronous accent-color extraction.
        AppSettings.Update(settings =>
        {
            settings.BackgroundImageOffsetX = horizontal;
            settings.BackgroundImageOffsetY = vertical;
        });
        _positionDirty = false;
        BackgroundChangedMessage.Send();
    }

    private void BackgroundPosition_Closed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        _positionSaveCts?.Cancel();
        if (_positionDirty)
            PersistBackgroundPosition(BackgroundPositionControl.HorizontalOffset, BackgroundPositionControl.VerticalOffset);
    }
}
