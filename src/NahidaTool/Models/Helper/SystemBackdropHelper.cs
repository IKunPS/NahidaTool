using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using System;
using System.Net;
using System.Runtime.InteropServices;
using Windows.UI;
using WinRT;
using NahidaTool.Models.Service;

namespace NahidaTool.Models.Helper;

public class SystemBackdropHelper
{
    private readonly Window _window;
    private WindowsSystemDispatcherQueueHelper? wsdqHelper;
    private SystemBackdropConfiguration? configurationSource;
    private MicaController? micaController;
    private DesktopAcrylicController? acrylicController;
    private SystemBackdropProperty? backdropProperty;
    private bool alwaysActive;

    public SystemBackdropHelper(Window window, SystemBackdropProperty? backdropProperty = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        _window = window;
        this.backdropProperty = backdropProperty;
    }

    public void ResetBackdrop()
    {
        micaController?.Dispose();
        micaController = null;
        acrylicController?.Dispose();
        acrylicController = null;
        _window.Activated -= Window_Activated;
        _window.Closed -= Window_Closed;
        if (_window.Content is FrameworkElement element)
            element.ActualThemeChanged -= Window_ThemeChanged;
        configurationSource = null;
        alwaysActive = false;
    }

    public bool TrySetAcrylic(bool alwaysActive = false)
    {
        ResetBackdrop();
        if (DesktopAcrylicController.IsSupported())
        {
            wsdqHelper = new WindowsSystemDispatcherQueueHelper();
            wsdqHelper.EnsureWindowsSystemDispatcherQueueController();

            configurationSource = new SystemBackdropConfiguration();
            _window.Activated += Window_Activated;
            _window.Closed += Window_Closed;
            if (_window.Content is FrameworkElement element)
                element.ActualThemeChanged += Window_ThemeChanged;
            else
                LogService.Warn("SystemBackdrop: Window.Content 不是 FrameworkElement，跳过主题变更监听");

            configurationSource.IsInputActive = true;
            SetConfigurationSourceTheme();

            acrylicController = new DesktopAcrylicController();
            SetControllerProperties();

            if (!acrylicController.AddSystemBackdropTarget(_window.As<ICompositionSupportsSystemBackdrop>()))
            {
                LogService.Warn("SystemBackdrop: AddSystemBackdropTarget 失败，已回退到普通背景");
                ResetBackdrop();
                return false;
            }
            acrylicController.SetSystemBackdropConfiguration(configurationSource);

            this.alwaysActive = alwaysActive;
            return true;
        }
        LogService.Info("SystemBackdrop: 当前系统不支持 DesktopAcrylic，已回退到普通背景");
        return false;
    }

    private void Window_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (configurationSource != null)
        {
            configurationSource.IsInputActive = alwaysActive || args.WindowActivationState != WindowActivationState.Deactivated;
        }
    }

    private void Window_ThemeChanged(FrameworkElement sender, object args)
    {
        if (configurationSource != null)
        {
            SetConfigurationSourceTheme();
        }
        if (backdropProperty != null)
        {
            SetControllerProperties();
        }
    }

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        ResetBackdrop();
    }

    private void SetConfigurationSourceTheme()
    {
        if (configurationSource != null)
        {
            configurationSource.Theme = (_window.Content as FrameworkElement)?.ActualTheme switch
            {
                ElementTheme.Light => SystemBackdropTheme.Light,
                ElementTheme.Dark => SystemBackdropTheme.Dark,
                _ => SystemBackdropTheme.Default,
            };
        }
    }

    private void SetControllerProperties()
    {
        if (backdropProperty != null)
        {
            var actualTheme = ((FrameworkElement)_window.Content).ActualTheme;
            if (actualTheme is ElementTheme.Default)
            {
                acrylicController?.ResetProperties();
                micaController?.ResetProperties();
            }
            if (actualTheme is ElementTheme.Light)
            {
                if (acrylicController != null)
                {
                    acrylicController.FallbackColor = backdropProperty.FallbackColorLight.ToColor();
                    acrylicController.LuminosityOpacity = backdropProperty.LuminosityOpacityLight;
                    acrylicController.TintColor = backdropProperty.TintColorLight.ToColor();
                    acrylicController.TintOpacity = backdropProperty.TintOpacityLight;
                }
            }
            if (actualTheme is ElementTheme.Dark)
            {
                if (acrylicController != null)
                {
                    acrylicController.FallbackColor = backdropProperty.FallbackColorDark.ToColor();
                    acrylicController.LuminosityOpacity = backdropProperty.LuminosityOpacityDark;
                    acrylicController.TintColor = backdropProperty.TintColorDark.ToColor();
                    acrylicController.TintOpacity = backdropProperty.TintOpacityDark;
                }
            }
        }
    }

    private class WindowsSystemDispatcherQueueHelper
    {
        [StructLayout(LayoutKind.Sequential)]
        struct DispatcherQueueOptions
        {
            internal int dwSize;
            internal int threadType;
            internal int apartmentType;
        }

        [DllImport("CoreMessaging.dll")]
        private static extern int CreateDispatcherQueueController(in DispatcherQueueOptions options, out nint dispatcherQueueController);

        nint m_dispatcherQueueController;
        public void EnsureWindowsSystemDispatcherQueueController()
        {
            if (Windows.System.DispatcherQueue.GetForCurrentThread() != null)
            {
                return;
            }

            if (m_dispatcherQueueController == 0)
            {
                DispatcherQueueOptions options;
                options.dwSize = Marshal.SizeOf(typeof(DispatcherQueueOptions));
                options.threadType = 2;    // DQTYPE_THREAD_CURRENT
                options.apartmentType = 2; // DQTAT_COM_STA

                _ = CreateDispatcherQueueController(options, out m_dispatcherQueueController);
            }
        }
    }
}

public record SystemBackdropProperty
{
    public required uint FallbackColorDark { get; init; }
    public required uint FallbackColorLight { get; init; }
    public required float LuminosityOpacityDark { get; init; }
    public required float LuminosityOpacityLight { get; init; }
    public required uint TintColorDark { get; init; }
    public required uint TintColorLight { get; init; }
    public required float TintOpacityDark { get; init; }
    public required float TintOpacityLight { get; init; }

    public static readonly SystemBackdropProperty AcrylicDefault = new()
    {
        FallbackColorDark = 0xFF545454,
        FallbackColorLight = 0xFFD3D3D3,
        LuminosityOpacityDark = 0.64f,
        LuminosityOpacityLight = 0.64f,
        TintColorDark = 0xFF545454,
        TintColorLight = 0xFFD3D3D3,
        TintOpacityDark = 0,
        TintOpacityLight = 0,
    };
}

file static class UInt32ToColorHelper
{
    [StructLayout(LayoutKind.Explicit)]
    private struct UInt32ToColor
    {
        [FieldOffset(0)]
        public uint Value;

        [FieldOffset(0)]
        public Color Color;
    }

    public static Color ToColor(this uint value)
    {
        return new UInt32ToColor { Value = (uint)IPAddress.HostToNetworkOrder((int)value) }.Color;
    }
}
