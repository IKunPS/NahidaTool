using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.UI.Xaml;
using NahidaTool.Models;
using NahidaTool.Models.Config;
using NahidaTool.Models.Helper;
using NahidaTool.Models.Service;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace NahidaTool;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;
    private SystemTrayWindow? _systemTrayWindow;
    private static Mutex? _singleInstanceMutex;

    /// <summary>
    /// Gets the main window of the application.
    /// </summary>
    public static Window MainWindow { get; private set; } = null!;

    #region Single Instance

    private const string AppMutexName = "NahidaTool_SingleInstance_8F3A2B1C";

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsIconic(IntPtr hWnd);

    private const int SW_RESTORE = 9;

    /// <summary>
    /// 检测是否已有实例运行，若有关闭当前进程并激活已有窗口
    /// </summary>
    private static bool TryAcquireSingleInstance()
    {
        _singleInstanceMutex = new Mutex(true, AppMutexName, out bool createdNew);
        if (createdNew) return true;

        // 已有实例在运行 — 激活其窗口后退出
        _singleInstanceMutex.Dispose();
        _singleInstanceMutex = null;

        ActivateExistingWindow();
        return false;
    }

    public static void ReleaseSingleInstanceForUpdate()
    {
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }

        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
    }

    public static void RestoreSingleInstanceAfterUpdateFailure()
    {
        if (_singleInstanceMutex == null)
            TryAcquireSingleInstance();
    }

    /// <summary>
    /// 查找并激活已有实例的主窗口
    /// </summary>
    private static void ActivateExistingWindow()
    {
        // WinUI 3 的窗口类名是固定的，通过窗口标题查找
        var hWnd = FindWindow(null, "NahidaTool");
        if (hWnd == IntPtr.Zero)
            hWnd = FindWindow(null, "Nahida");

        if (hWnd != IntPtr.Zero)
        {
            if (IsIconic(hWnd))
                ShowWindow(hWnd, SW_RESTORE);
            SetForegroundWindow(hWnd);
        }
    }

    #endregion

    public void EnsureSystemTray()
    {
        _systemTrayWindow ??= new SystemTrayWindow();
    }

    public void EnsureMainWindow()
    {
        if (_window is MainWindow mw)
        {
            mw.Show();
        }
        else
        {
            _window = new MainWindow();
            MainWindow = _window;
            _window.Activate();
        }
    }

    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        try
        {
            // 单实例检查 — 必须最先执行
            if (!TryAcquireSingleInstance())
            {
                Environment.Exit(0);
                return;
            }

            // 初始化日志服务
            LogService.Initialize();

            InitializeComponent();

            // 注册全局异常处理
            UnhandledException += App_UnhandledException;
        }
        catch (Exception ex)
        {
            // 如果初始化失败，写入紧急错误日志
            WriteEmergencyCrashLog("App构造函数异常", ex);
            throw;
        }
    }

    private static void InitializeLanguage()
    {
        try
        {
            var settings = AppSettings.Load();
            if (!string.IsNullOrWhiteSpace(settings.Language))
            {
                CultureInfo.CurrentUICulture = new CultureInfo(settings.Language);
            }
        }
        catch (Exception ex)
        {
            LogService.Debug($"初始化语言设置失败: {ex.Message}");
        }
    }

    private static void InitializeAccentColorTheme()
    {
        var defaultColor = Windows.UI.Color.FromArgb(255, 0xCB, 0xAD, 0x8E);
        AccentColorHelper.InitThemeDictionaries(Current.Resources.ThemeDictionaries, defaultColor);
    }

    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            LogService.Error("未处理的异常", e.Exception);
        }
        catch
        {
            WriteEmergencyCrashLog("未处理的异常", e.Exception);
        }
        e.Handled = true; // 防止应用崩溃
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            InitializeLanguage();
            InitializeAccentColorTheme();
            _window = new MainWindow();
            MainWindow = _window;
            _window.Activate();
        }
        catch (Exception ex)
        {
            try
            {
                LogService.Error("创建主窗口失败", ex);
            }
            catch
            {
                WriteEmergencyCrashLog("创建主窗口失败", ex);
            }
            throw;
        }
    }

    /// <summary>
    /// 紧急崩溃日志 - 当 LogService 不可用时使用
    /// 会在程序目录生成 crash_log.txt 文件
    /// </summary>
    private static void WriteEmergencyCrashLog(string message, Exception ex)
    {
        try
        {
            string crashLogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash_log.txt");
            var sb = new StringBuilder();
            sb.AppendLine("========================================");
            sb.AppendLine($"NahidaTool 崩溃报告");
            sb.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"版本: {AppVersion.Current}");
            sb.AppendLine($"操作系统: {Environment.OSVersion}");
            sb.AppendLine($".NET版本: {Environment.Version}");
            sb.AppendLine("========================================");
            sb.AppendLine($"错误信息: {message}");
            sb.AppendLine($"异常类型: {ex.GetType().FullName}");
            sb.AppendLine($"异常消息: {ex.Message}");
            sb.AppendLine($"堆栈跟踪:");
            sb.AppendLine(ex.StackTrace);
            if (ex.InnerException != null)
            {
                sb.AppendLine($"内部异常: {ex.InnerException.Message}");
                sb.AppendLine($"内部堆栈: {ex.InnerException.StackTrace}");
            }
            sb.AppendLine("========================================");
            sb.AppendLine();

            File.AppendAllText(crashLogPath, sb.ToString(), Encoding.UTF8);
        }
        catch
        {
            // 如果连紧急日志都写不了，那就没办法了
        }
    }
}
