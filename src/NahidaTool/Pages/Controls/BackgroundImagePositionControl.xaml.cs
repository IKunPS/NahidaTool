using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;
using NahidaTool.Models;
using NahidaTool.Models.Helper;
using NahidaTool.Models.Service;

namespace NahidaTool.Pages.Controls;

public sealed partial class BackgroundImagePositionControl : UserControl
{
    private bool _suppressChanges = true;
    private string _path = string.Empty;
    private double _viewportWidth = 1200;
    private double _viewportHeight = 676;
    private bool _supported;
    private bool _capabilitiesKnown;
    private bool _horizontalAvailable;
    private bool _verticalAvailable;
    private CancellationTokenSource? _previewCts;

    public event Action<int, int>? PositionChanged;

    public int HorizontalOffset => (int)Math.Round(HorizontalSlider.Value);
    public int VerticalOffset => (int)Math.Round(VerticalSlider.Value);

    public BackgroundImagePositionControl()
    {
        InitializeComponent();
        _suppressChanges = false;
        Loaded += (_, _) => QueuePreview();
        Unloaded += (_, _) => _previewCts?.Cancel();
        SizeChanged += (_, args) =>
        {
            if (Math.Abs(args.NewSize.Width - args.PreviousSize.Width) > 1)
                QueuePreview();
        };
    }

    public void Configure(string path, double viewportWidth, double viewportHeight, int horizontal, int vertical)
    {
        _previewCts?.Cancel();
        _path = path;
        _viewportWidth = viewportWidth;
        _viewportHeight = viewportHeight;
        _supported = Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp";
        _capabilitiesKnown = false;
        _suppressChanges = true;
        HorizontalSlider.Value = Math.Clamp(horizontal, -100, 100);
        VerticalSlider.Value = Math.Clamp(vertical, -100, 100);
        _suppressChanges = false;
        UpdateValues();
        HorizontalSlider.IsEnabled = false;
        VerticalSlider.IsEnabled = false;
        PreviewRetryButton.Visibility = Visibility.Collapsed;
        if (!_supported)
        {
            PreviewImage.Source = null;
            PreviewProgress.IsActive = false;
            PreviewProgress.Visibility = Visibility.Collapsed;
            PreviewState.Visibility = Visibility.Visible;
            PreviewStateText.Text = Lang.HomeSettingDialog_ImagePositionStaticOnly;
            return;
        }
        PreviewState.Visibility = Visibility.Collapsed;
        QueuePreview();
    }

    public void UpdateLanguage()
    {
        Bindings.Update();
        UpdateValues();
        if (!_supported)
            PreviewStateText.Text = Lang.HomeSettingDialog_ImagePositionStaticOnly;
        else if (PreviewRetryButton.Visibility == Visibility.Visible)
            PreviewStateText.Text = Lang.HomeSettingDialog_ImagePositionFailed;
    }

    private void PositionSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressChanges) return;
        UpdateValues();
        PositionChanged?.Invoke(HorizontalOffset, VerticalOffset);
        QueuePreview();
    }

    private void CenterButton_Click(object sender, RoutedEventArgs e)
    {
        _suppressChanges = true;
        HorizontalSlider.Value = 0;
        VerticalSlider.Value = 0;
        _suppressChanges = false;
        UpdateValues();
        PositionChanged?.Invoke(0, 0);
        QueuePreview();
    }

    private void UpdateValues()
    {
        HorizontalValueText.Text = HorizontalOffset.ToString("+0;-0;0", CultureInfo.CurrentCulture) + "%";
        VerticalValueText.Text = VerticalOffset.ToString("+0;-0;0", CultureInfo.CurrentCulture) + "%";
        CenterButton.IsEnabled = _supported && (HorizontalOffset != 0 || VerticalOffset != 0);
    }

    private void PreviewRetryButton_Click(object sender, RoutedEventArgs e) => QueuePreview();

    private void QueuePreview()
    {
        if (!IsLoaded || !_supported || ActualWidth <= 0) return;
        _previewCts?.Cancel();
        var request = new CancellationTokenSource();
        _previewCts = request;
        _ = RefreshPreviewAsync(request, _path, HorizontalOffset, VerticalOffset, _viewportWidth, _viewportHeight);
    }

    private async Task RefreshPreviewAsync(CancellationTokenSource request, string path, int horizontal, int vertical,
        double viewportWidth, double viewportHeight)
    {
        CancellationToken ct = request.Token;
        try
        {
            await Task.Delay(180, ct);
            PreviewState.Visibility = Visibility.Collapsed;
            PreviewRetryButton.Visibility = Visibility.Collapsed;
            PreviewProgress.IsActive = true;
            PreviewProgress.Visibility = Visibility.Visible;
            if (!_capabilitiesKnown)
            {
                var movement = await Task.Run(() => BackgroundImageLoader.GetMovementAsync(
                    path, viewportWidth, viewportHeight, ct), ct);
                ct.ThrowIfCancellationRequested();
                _horizontalAvailable = movement.Horizontal;
                _verticalAvailable = movement.Vertical;
                _capabilitiesKnown = true;
            }

            double scale = XamlRoot?.RasterizationScale ?? 1;
            double aspect = viewportWidth / viewportHeight;
            int width = Math.Max(1, (int)Math.Ceiling(Math.Min(ActualWidth - 10, 130 * aspect) * scale));
            int height = Math.Max(1, (int)Math.Round(width / aspect));
            using var bitmap = await Task.Run(() => BackgroundImageLoader.LoadAsync(
                path, width, height, horizontal, vertical, ct), ct);
            var source = new SoftwareBitmapSource();
            await source.SetBitmapAsync(bitmap).AsTask(ct);
            ct.ThrowIfCancellationRequested();
            PreviewImage.Source = source;
            HorizontalSlider.IsEnabled = _horizontalAvailable;
            VerticalSlider.IsEnabled = _verticalAvailable;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return;
            LogService.Warn($"背景位置预览失败: {ex.Message}");
            PreviewStateText.Text = Lang.HomeSettingDialog_ImagePositionFailed;
            PreviewState.Visibility = Visibility.Visible;
            PreviewRetryButton.Visibility = Visibility.Visible;
            HorizontalSlider.IsEnabled = false;
            VerticalSlider.IsEnabled = false;
        }
        finally
        {
            if (ReferenceEquals(_previewCts, request))
            {
                _previewCts = null;
                PreviewProgress.IsActive = false;
                PreviewProgress.Visibility = Visibility.Collapsed;
            }
            request.Dispose();
        }
    }
}
