using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System;
using NahidaTool.Models.Service;
using Vanara.PInvoke;
using Windows.Graphics;

namespace NahidaTool.Frameworks;

public abstract class WindowEx : Window
{
    public IntPtr WindowHandle { get; }
    private readonly ComCtl32.SUBCLASSPROC _windowSubclassProc;

    public double UIScale => User32.GetDpiForWindow(WindowHandle) / 96d;

    protected WindowEx()
    {
        WindowHandle = (IntPtr)AppWindow.Id.Value;
        _windowSubclassProc = WindowSubclassProc;
        ComCtl32.SetWindowSubclass(WindowHandle, _windowSubclassProc, 1001, IntPtr.Zero);
        SetTaskbarIcon();
    }

    private void SetTaskbarIcon()
    {
        try
        {
            nint hInstance = Kernel32.GetModuleHandle(null).DangerousGetHandle();
            nint hIcon = User32.LoadIcon(hInstance, "#32512").DangerousGetHandle();
            User32.SendMessage(WindowHandle, (uint)User32.WindowMessage.WM_SETICON, 0, hIcon); // ICON_SMALL - 任务栏
            User32.SendMessage(WindowHandle, (uint)User32.WindowMessage.WM_SETICON, 1, hIcon); // ICON_BIG - Alt+Tab
        }
        catch (Exception ex)
        {
            LogService.Warn($"设置任务栏图标失败: {ex.Message}");
        }
    }

    protected virtual nint WindowSubclassProc(HWND hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nint dwRefData)
    {
        if (uMsg == (uint)User32.WindowMessage.WM_SYSCOMMAND && wParam == 0xF030)
            return IntPtr.Zero; // 阻止双击标题栏最大化
        return ComCtl32.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    public virtual void CenterInScreen(int? width = null, int? height = null)
    {
        width = width <= 0 ? null : width;
        height = height <= 0 ? null : height;
        User32.GetCursorPos(out var point);
        var display = DisplayArea.GetFromPoint(new PointInt32(point.X, point.Y), DisplayAreaFallback.Nearest);
        double scale = UIScale;
        int w = (int)((width * scale) ?? AppWindow.Size.Width);
        int h = (int)((height * scale) ?? AppWindow.Size.Height);
        int x = display.WorkArea.X + (display.WorkArea.Width - w) / 2;
        int y = display.WorkArea.Y + (display.WorkArea.Height - h) / 2;
        AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
    }

    public void SetDragRectangles(params RectInt32[] value)
    {
        if (AppWindowTitleBar.IsCustomizationSupported() && AppWindow.TitleBar.ExtendsContentIntoTitleBar)
            AppWindow.TitleBar.SetDragRectangles(value);
    }

    public virtual void Show()
    {
        User32.ShowWindow(WindowHandle, ShowWindowCommand.SW_SHOWNORMAL);
        User32.SetForegroundWindow(WindowHandle);
    }

    public virtual void Hide()
    {
        AppWindow.Hide();
    }

    public virtual void Minimize()
    {
        User32.ShowWindow(WindowHandle, ShowWindowCommand.SW_MINIMIZE);
    }

    public void AdaptTitleBarButtonColorToActuallTheme()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported() || !AppWindow.TitleBar.ExtendsContentIntoTitleBar)
            return;

        var titleBar = AppWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        titleBar.ButtonInactiveBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);

        if (Content is FrameworkElement element)
        {
            if (element.ActualTheme == ElementTheme.Light)
            {
                titleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 0, 0, 0);
                titleBar.ButtonHoverForegroundColor = Windows.UI.Color.FromArgb(255, 0, 0, 0);
                titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(0x20, 0x00, 0x00, 0x00);
            }
            else
            {
                titleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255);
                titleBar.ButtonHoverForegroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255);
                titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF);
            }
        }
    }
}
