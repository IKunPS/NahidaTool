using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.UI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NahidaTool.Models.Config;
using NahidaTool.Models.Helper;
using NahidaTool.Models.Service;

namespace NahidaTool;

public sealed partial class MainWindow
{
    private bool _bgFirstActive = true;
    private string? _lastBgPath;
    private MediaPlayer? _bgMediaPlayer;
    private bool _isVideoBackground;
    private bool _backgroundDisposed;
    private XamlRoot? _backgroundXamlRoot;
    private CancellationTokenSource? _imageRenderCts;
    private int _backgroundOffsetX;
    private int _backgroundOffsetY;
    private (string Path, int Width, int Height, long LastWrite, int OffsetX, int OffsetY)? _pendingImageKey;
    private (string Path, int Width, int Height, long LastWrite, int OffsetX, int OffsetY)? _renderedImageKey;
    private CanvasRenderTarget? _videoSurface;
    private CanvasImageSource? _videoImageSource;
    private readonly SemaphoreSlim _videoSemaphore = new(1, 1);

    public void RefreshBackground()
    {
        var settings = AppSettings.Load();
        string targetPath = BackgroundImageLoader.ResolveSourcePath(settings.EnableCustomBg, settings.CustomBg);
        _backgroundOffsetX = Math.Clamp(settings.BackgroundImageOffsetX, -100, 100);
        _backgroundOffsetY = Math.Clamp(settings.BackgroundImageOffsetY, -100, 100);

        bool pathChanged = !string.Equals(_lastBgPath, targetPath, StringComparison.OrdinalIgnoreCase);
        _lastBgPath = targetPath;

        var ext = Path.GetExtension(targetPath).ToLowerInvariant();
        _isVideoBackground = ext is ".mp4" or ".webm" or ".avi" or ".wmv" or ".mov";

        if (_isVideoBackground)
        {
            _imageRenderCts?.Cancel();
            _pendingImageKey = null;
            _renderedImageKey = null;
            if (pathChanged || _bgMediaPlayer == null)
                StartVideoPlayer(targetPath, settings.VideoBgVolume);
        }
        else
        {
            StopVideoPlayer();
            QueueBackgroundImageRender();
            if (pathChanged)
                _ = UpdateAccentColorFromImageAsync(targetPath);
        }
    }

    private void BackgroundRoot_Loaded(object sender, RoutedEventArgs e)
    {
        if (_backgroundXamlRoot != BackgroundRoot.XamlRoot)
        {
            if (_backgroundXamlRoot != null)
                _backgroundXamlRoot.Changed -= BackgroundXamlRoot_Changed;
            _backgroundXamlRoot = BackgroundRoot.XamlRoot;
            if (_backgroundXamlRoot != null)
                _backgroundXamlRoot.Changed += BackgroundXamlRoot_Changed;
        }
        QueueBackgroundImageRender();
    }

    private void BackgroundRoot_SizeChanged(object sender, SizeChangedEventArgs e) => QueueBackgroundImageRender();

    private void BackgroundXamlRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args) => QueueBackgroundImageRender();

    private void QueueBackgroundImageRender()
    {
        if (_backgroundDisposed || _isVideoBackground || _lastBgPath == null ||
            BackgroundRoot.ActualWidth <= 0 || BackgroundRoot.ActualHeight <= 0)
            return;

        try
        {
            double scale = BackgroundRoot.XamlRoot?.RasterizationScale ?? UIScale;
            var key = (Path: _lastBgPath,
                Width: checked((int)Math.Ceiling(BackgroundRoot.ActualWidth * scale)),
                Height: checked((int)Math.Ceiling(BackgroundRoot.ActualHeight * scale)),
                LastWrite: File.GetLastWriteTimeUtc(_lastBgPath).Ticks,
                OffsetX: _backgroundOffsetX, OffsetY: _backgroundOffsetY);
            if (_renderedImageKey == key || _pendingImageKey == key)
                return;

            _imageRenderCts?.Cancel();
            var request = new CancellationTokenSource();
            _imageRenderCts = request;
            _pendingImageKey = key;
            _ = RenderBackgroundImageAsync(key, request);
        }
        catch (Exception ex)
        {
            LogService.Warn($"准备背景图片失败: {ex.Message}");
        }
    }

    private async Task RenderBackgroundImageAsync(
        (string Path, int Width, int Height, long LastWrite, int OffsetX, int OffsetY) key,
        CancellationTokenSource request)
    {
        CancellationToken ct = request.Token;
        try
        {
            // Coalesce layout/DPI notifications. Keep the current image until its
            // replacement is ready, and never let an older request overwrite it.
            await Task.Delay(100, ct);
            ImageSource source;
            if (string.Equals(Path.GetExtension(key.Path), ".gif", StringComparison.OrdinalIgnoreCase))
            {
                // Keep GIF playback with the XAML decoder; the still-image WIC
                // pipeline would flatten an animation into its first frame.
                using var file = File.OpenRead(key.Path);
                using var stream = file.AsRandomAccessStream();
                var animated = new BitmapImage();
                await animated.SetSourceAsync(stream).AsTask(ct);
                source = animated;
            }
            else
            {
                using var bitmap = await Task.Run(
                    () => BackgroundImageLoader.LoadAsync(
                        key.Path, key.Width, key.Height, key.OffsetX, key.OffsetY, ct), ct);
                var resized = new SoftwareBitmapSource();
                await resized.SetBitmapAsync(bitmap).AsTask(ct);
                source = resized;
            }
            ct.ThrowIfCancellationRequested();

            var currentActive = _bgFirstActive ? BackgroundImage1 : BackgroundImage2;
            var nextActive = _bgFirstActive ? BackgroundImage2 : BackgroundImage1;
            if (_renderedImageKey?.Path == key.Path)
            {
                currentActive.Source = source;
            }
            else
            {
                nextActive.Source = source;
                currentActive.Opacity = 0;
                nextActive.Opacity = 1;
                _bgFirstActive = !_bgFirstActive;
            }
            _renderedImageKey = key;
            LogService.Info($"背景图片显示尺寸: {key.Width}×{key.Height} 物理像素，位置: {key.OffsetX}, {key.OffsetY}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LogService.Error("背景图片加载失败", ex);
        }
        finally
        {
            if (ReferenceEquals(_imageRenderCts, request))
            {
                _imageRenderCts = null;
                _pendingImageKey = null;
            }
            request.Dispose();
        }
    }

    private void DisposeBackground()
    {
        _backgroundDisposed = true;
        _imageRenderCts?.Cancel();
        if (_backgroundXamlRoot != null)
            _backgroundXamlRoot.Changed -= BackgroundXamlRoot_Changed;
        StopVideoPlayer();
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

}
