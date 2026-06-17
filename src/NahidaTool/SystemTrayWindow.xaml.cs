using System;
using System.Windows.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using NahidaTool.Frameworks;
using NahidaTool.Models.Helper;
using NahidaTool.Models.Service;
using Vanara.PInvoke;

namespace NahidaTool;

public sealed partial class SystemTrayWindow : WindowEx
{
    public SystemTrayWindow()
    {
        InitializeComponent();
        InitializeWindow();
        SetTrayIcon();

        // H.NotifyIcon 用 Command 而非 Event
        TrayIcon.LeftClickCommand = new RelayCommand(ShowMainWindow);
        TrayIcon.RightClickCommand = new RelayCommand(ShowPopupMenu);
        TrayIcon.NoLeftClickDelay = true;
    }

    private unsafe void InitializeWindow()
    {
        new SystemBackdropHelper(this, SystemBackdropProperty.AcrylicDefault with
        {
            TintColorLight = 0xFFE7E7E7,
            TintColorDark = 0xFF404040
        }).TrySetAcrylic(true);

        AppWindow.IsShownInSwitchers = false;
        AppWindow.Closing += (_, args) => args.Cancel = true;
        Activated += Window_Activated;

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsResizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        // 移除标题栏和边框，变成弹出菜单样式
        var style = User32.GetWindowLongPtr(WindowHandle, User32.WindowLongFlags.GWL_STYLE);
        style &= ~(nint)User32.WindowStyles.WS_CAPTION;
        style &= ~(nint)User32.WindowStyles.WS_BORDER;
        User32.SetWindowLong(WindowHandle, User32.WindowLongFlags.GWL_STYLE, style);

        // Win11 才支持 DWMWA_WINDOW_CORNER_PREFERENCE；Win10 上直接跳过，避免无意义的 DWM 调用失败。
        if (Environment.OSVersion.Version.Build >= 22000)
        {
            var corner = DwmApi.DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUND;
            DwmApi.DwmSetWindowAttribute(WindowHandle, DwmApi.DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE,
                (nint)(&corner), sizeof(DwmApi.DWM_WINDOW_CORNER_PREFERENCE));
        }

        // Show + Hide 后才显示托盘图标
        Show();
        Hide();
    }

    private void SetTrayIcon()
    {
        try
        {
            nint hInstance = Kernel32.GetModuleHandle(null).DangerousGetHandle();
            nint hIcon = User32.LoadIcon(hInstance, "#32512").DangerousGetHandle();
            TrayIcon.Icon = System.Drawing.Icon.FromHandle(hIcon);
        }
        catch (Exception ex)
        {
            LogService.Warn($"设置托盘图标失败: {ex.Message}");
        }
    }

    private void ShowMainWindow()
    {
        if (App.Current is App app)
            app.EnsureMainWindow();
        Hide();
    }

    private void ShowPopupMenu()
    {
        // 在光标位置弹出菜单
        RootGrid.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        double scale = UIScale;
        User32.GetCursorPos(out POINT point);
        int w = (int)(RootGrid.DesiredSize.Width * scale);
        int h = (int)(RootGrid.DesiredSize.Height * scale);

        User32.CalculatePopupWindowPosition(
            point, new SIZE { Width = w, Height = h },
            User32.TrackPopupMenuFlags.TPM_RIGHTALIGN | User32.TrackPopupMenuFlags.TPM_BOTTOMALIGN | User32.TrackPopupMenuFlags.TPM_WORKAREA,
            null, out RECT windowPos);

        User32.MoveWindow(WindowHandle, windowPos.X, windowPos.Y, windowPos.Width, windowPos.Height, true);
        Show();
    }

    private void Window_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState is WindowActivationState.Deactivated)
        {
            Hide();
        }
    }

    private void ShowMainWindow_Click(object sender, RoutedEventArgs e)
    {
        ShowMainWindow();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        TrayIcon.Dispose();
        Environment.Exit(0);
    }
}

/// <summary>
/// 简易 ICommand 实现，避免引入 CommunityToolkit.Mvvm
/// </summary>
internal sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    public event EventHandler? CanExecuteChanged;
    public RelayCommand(Action execute) => _execute = execute;
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _execute();
}
