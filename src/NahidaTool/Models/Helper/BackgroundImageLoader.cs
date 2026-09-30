using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;

namespace NahidaTool.Models.Helper;

internal static class BackgroundImageLoader
{
    internal static string ResolveSourcePath(bool useCustomBackground, string? customPath) =>
        useCustomBackground && !string.IsNullOrEmpty(customPath) && File.Exists(customPath)
            ? customPath
            : Path.Combine(AppContext.BaseDirectory, "Assets", "Nahida.png");

    internal static async Task<(bool Horizontal, bool Vertical)> GetMovementAsync(
        string path, double viewportWidth, double viewportHeight, CancellationToken cancellationToken)
    {
        if (!double.IsFinite(viewportWidth) || !double.IsFinite(viewportHeight) ||
            viewportWidth <= 0 || viewportHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(viewportWidth));
        cancellationToken.ThrowIfCancellationRequested();

        using var file = File.OpenRead(path);
        using var stream = file.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (decoder.OrientedPixelWidth == 0 || decoder.OrientedPixelHeight == 0)
            throw new InvalidDataException("Background image has no pixels");
        double imageAspect = (double)decoder.OrientedPixelWidth / decoder.OrientedPixelHeight;
        double viewportAspect = viewportWidth / viewportHeight;
        return (imageAspect > viewportAspect + 0.000001, imageAspect < viewportAspect - 0.000001);
    }

    /// <summary>
    /// Produces a positioned, display-sized bitmap in physical pixels.
    /// Fant averages the source pixels covered by each output pixel, preventing
    /// thin lines from aliasing when a large background is reduced substantially.
    /// </summary>
    internal static async Task<SoftwareBitmap> LoadAsync(
        string path, int pixelWidth, int pixelHeight, int horizontalOffset, int verticalOffset,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelHeight);
        cancellationToken.ThrowIfCancellationRequested();

        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var stream = file.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken).ConfigureAwait(false);
        if (decoder.OrientedPixelWidth == 0 || decoder.OrientedPixelHeight == 0)
            throw new InvalidDataException("Background image has no pixels");

        double scale = Math.Max(
            (double)pixelWidth / decoder.OrientedPixelWidth,
            (double)pixelHeight / decoder.OrientedPixelHeight);
        uint scaledWidth = checked((uint)Math.Ceiling(decoder.PixelWidth * scale));
        uint scaledHeight = checked((uint)Math.Ceiling(decoder.PixelHeight * scale));
        bool swapsAxes = decoder.OrientedPixelWidth != decoder.PixelWidth;
        uint orientedWidth = swapsAxes ? scaledHeight : scaledWidth;
        uint orientedHeight = swapsAxes ? scaledWidth : scaledHeight;

        // WIC scales before applying EXIF orientation, then crops in the final
        // oriented coordinate space. Positive offsets move the artwork right/down
        // by selecting a crop further left/up, without exposing empty borders.
        var transform = new BitmapTransform
        {
            ScaledWidth = scaledWidth,
            ScaledHeight = scaledHeight,
            InterpolationMode = scale < 1 ? BitmapInterpolationMode.Fant : BitmapInterpolationMode.Cubic,
            Bounds = new BitmapBounds
            {
                X = (uint)Math.Round((orientedWidth - (uint)pixelWidth) *
                    (100 - Math.Clamp(horizontalOffset, -100, 100)) / 200d),
                Y = (uint)Math.Round((orientedHeight - (uint)pixelHeight) *
                    (100 - Math.Clamp(verticalOffset, -100, 100)) / 200d),
                Width = (uint)pixelWidth,
                Height = (uint)pixelHeight
            }
        };

        var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb)
            .AsTask(cancellationToken).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
        {
            bitmap.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
        }
        return bitmap;
    }
}
