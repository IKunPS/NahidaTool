using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using NahidaTool.Models.Event;
using NahidaTool.Models.Service;

namespace NahidaTool.Models.Helper;

internal static class AccentColorHelper
{
    public static unsafe Color? GetAccentColor(byte[] bgra, int width, int height)
    {
        if (bgra.Length % 4 == 0)
        {
            fixed (byte* ptr = bgra)
            {
                return GetAccentColorInternal(ptr, width, height);
            }
        }
        return null;
    }

    private static unsafe Color? GetAccentColorInternal(void* bgra, int width, int height)
    {
        try
        {
            uint* p = (uint*)bgra;
            long b = 0, g = 0, r = 0;
            for (int y = 0; y < height; y += 2)
            {
                for (int x = 0; x < width; x += 2)
                {
                    Bgra32 pixel = Unsafe.AsRef<Bgra32>(p);
                    b += pixel.B;
                    g += pixel.G;
                    r += pixel.R;
                    p += 2;
                }
                p += width - width % 2;
            }

            int c = (width / 2) * (height / 2);
            Unsafe.SkipInit(out Color color);
            color.B = (byte)(b / c);
            color.G = (byte)(g / c);
            color.R = (byte)(r / c);
            color.A = 255;

            (double h, double s, double v) = RgbToHsv(color.R, color.G, color.B);
            return HsvToColor(h, 0.6, v);
        }
        catch (Exception ex)
        {
            LogService.Debug($"提取主题色失败: {ex.Message}");
        }
        return null;
    }

    private static (double h, double s, double v) RgbToHsv(byte r, byte g, byte b)
    {
        double rf = r / 255.0;
        double gf = g / 255.0;
        double bf = b / 255.0;
        double max = Math.Max(Math.Max(rf, gf), bf);
        double min = Math.Min(Math.Min(rf, gf), bf);
        double delta = max - min;

        double h = 0;
        if (delta > 0)
        {
            if (max == rf)
                h = 60 * (((gf - bf) / delta) % 6);
            else if (max == gf)
                h = 60 * (((bf - rf) / delta) + 2);
            else
                h = 60 * (((rf - gf) / delta) + 4);
        }
        if (h < 0) h += 360;

        double s = max > 0 ? delta / max : 0;
        double v = max;

        return (h, s, v);
    }

    private static Color HsvToColor(double h, double s, double v)
    {
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = v - c;

        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        return Color.FromArgb(255,
            (byte)Math.Clamp((int)((r + m) * 255), 0, 255),
            (byte)Math.Clamp((int)((g + m) * 255), 0, 255),
            (byte)Math.Clamp((int)((b + m) * 255), 0, 255));
    }

    public static string ToHex(this Color color)
    {
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    public static Color ToColor(string hex)
    {
        hex = hex.TrimStart('#');
        byte r = Convert.ToByte(hex.Substring(0, 2), 16);
        byte g = Convert.ToByte(hex.Substring(2, 2), 16);
        byte b = Convert.ToByte(hex.Substring(4, 2), 16);
        return Color.FromArgb(255, r, g, b);
    }

    private static Color ColorMix(Color input, Color blend, double percent)
    {
        return Color.FromArgb(255,
                              (byte)(input.R * percent + blend.R * (1 - percent)),
                              (byte)(input.G * percent + blend.G * (1 - percent)),
                              (byte)(input.B * percent + blend.B * (1 - percent)));
    }

    public static void InitThemeDictionaries(System.Collections.Generic.IDictionary<object, object> themeDictionaries, Color defaultColor)
    {
        void EnsureThemeDict(string key)
        {
            if (!themeDictionaries.ContainsKey(key) || themeDictionaries[key] is not ResourceDictionary)
            {
                themeDictionaries[key] = new ResourceDictionary();
            }
        }

        EnsureThemeDict("Light");
        EnsureThemeDict("Dark");

        foreach (var key in themeDictionaries.Keys)
        {
            if (themeDictionaries[key] is ResourceDictionary themeDict)
            {
                themeDict["SystemAccentColor"] = defaultColor;
                themeDict["SystemAccentColorLight1"] = defaultColor;
                themeDict["SystemAccentColorLight2"] = defaultColor;
                themeDict["SystemAccentColorLight3"] = defaultColor;
                themeDict["SystemAccentColorDark1"] = defaultColor;
                themeDict["SystemAccentColorDark2"] = defaultColor;
                themeDict["SystemAccentColorDark3"] = defaultColor;
            }
        }
    }

    public static void ChangeAppAccentColor(Color? color)
    {
        if (color is null)
            return;

        Color light1 = ColorMix(color.Value, Colors.White, 0.8);
        Color light2 = ColorMix(color.Value, Colors.White, 0.6);
        Color light3 = ColorMix(color.Value, Colors.White, 0.4);
        Color dark1 = ColorMix(color.Value, Colors.Black, 0.8);
        Color dark2 = ColorMix(color.Value, Colors.Black, 0.6);
        Color dark3 = ColorMix(color.Value, Colors.Black, 0.4);

        var resources = Application.Current.Resources;

        // Base colors
        resources["SystemAccentColor"] = color;
        resources["SystemAccentColorLight1"] = light1;
        resources["SystemAccentColorLight2"] = light2;
        resources["SystemAccentColorLight3"] = light3;
        resources["SystemAccentColorDark1"] = dark1;
        resources["SystemAccentColorDark2"] = dark2;
        resources["SystemAccentColorDark3"] = dark3;

        // Accent fill brushes (for buttons, toggles, etc.)
        // 参考 Starward：暗色主题下填充色使用较亮的变体，避免按钮颜色过深
        resources["AccentFillColorDefaultBrush"] = new SolidColorBrush(light2);
        resources["AccentFillColorSecondaryBrush"] = new SolidColorBrush(light3);
        resources["AccentFillColorTertiaryBrush"] = new SolidColorBrush(light1);

        // Accent text brushes
        resources["AccentTextFillColorPrimaryBrush"] = new SolidColorBrush(light3);
        resources["AccentTextFillColorSecondaryBrush"] = new SolidColorBrush(light2);
        resources["AccentTextFillColorTertiaryBrush"] = new SolidColorBrush(light1);
        resources["AccentTextFillColorDisabledBrush"] = new SolidColorBrush(dark1);

        // Text on accent (used for text on accent-colored backgrounds)
        resources["TextOnAccentFillColorPrimaryBrush"] = new SolidColorBrush(Colors.White);
        resources["TextOnAccentFillColorSecondaryBrush"] = new SolidColorBrush(Colors.White);
        resources["TextOnAccentFillColorDisabledBrush"] = new SolidColorBrush(light1);

        // Also update in ThemeDictionaries so ThemeResource bindings fire
        foreach (var key in resources.ThemeDictionaries.Keys)
        {
            if (resources.ThemeDictionaries[key] is ResourceDictionary themeDict)
            {
                themeDict["SystemAccentColor"] = color;
                themeDict["SystemAccentColorLight1"] = light1;
                themeDict["SystemAccentColorLight2"] = light2;
                themeDict["SystemAccentColorLight3"] = light3;
                themeDict["SystemAccentColorDark1"] = dark1;
                themeDict["SystemAccentColorDark2"] = dark2;
                themeDict["SystemAccentColorDark3"] = dark3;
            }
        }

        AccentColorChangedMessage.Send(color.Value);
        // ThemeResource bindings are re-evaluated by MainWindow.OnAccentColorChanged
        // which flips RequestedTheme to force a full visual tree refresh (Starward's approach).
    }

    internal static void ForceAccentUpdate(DependencyObject element, Color accentBg, Color accentText)
    {
        int count = VisualTreeHelper.GetChildrenCount(element);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(element, i);

            // Update Rectangle fills, skipping template-internal ones like SeparatorLine in NavigationViewItemSeparator
            if (child is Microsoft.UI.Xaml.Shapes.Rectangle rect && rect.Fill is SolidColorBrush)
            {
                if (!IsInsideNavigationViewItemSeparator(rect))
                {
                    rect.Fill = new SolidColorBrush(accentBg);
                }
            }

            // Update accent-style buttons
            if (child is Button btn && btn.Style == Application.Current.Resources["AccentButtonStyle"])
            {
                btn.Background = new SolidColorBrush(accentBg);
                btn.Foreground = new SolidColorBrush(Colors.White);
            }

            // Update ToggleSwitch accent
            if (child is ToggleSwitch ts)
            {
                ts.Foreground = new SolidColorBrush(accentBg);
            }

            ForceAccentUpdate(child, accentBg, accentText);
        }
    }

    /// <summary>
    /// Check if a Rectangle is inside a NavigationViewItemSeparator template (like SeparatorLine),
    /// which should not be affected by accent color changes.
    /// </summary>
    private static bool IsInsideNavigationViewItemSeparator(DependencyObject? element)
    {
        // Walk up to 5 levels; the separator template is shallow (SeparatorLine → Grid → ControlTemplate)
        for (int i = 0; i < 5 && element != null; i++)
        {
            if (element is NavigationViewItemSeparator)
                return true;
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    [StructLayout(LayoutKind.Explicit, Size = 4)]
    private readonly struct Bgra32
    {
        [FieldOffset(0)] public readonly byte B;
        [FieldOffset(1)] public readonly byte G;
        [FieldOffset(2)] public readonly byte R;
        [FieldOffset(3)] public readonly byte A;
    }
}