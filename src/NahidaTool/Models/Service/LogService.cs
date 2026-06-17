using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using NahidaTool.Models.Config;

namespace NahidaTool.Models.Service;

/// <summary>
/// 日志服务，用于记录应用程序运行信息和错误
/// </summary>
public static class LogService
{
    private static readonly string LogDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../logs");
    private static readonly object _lock = new();
    private static string? _currentLogFile;

    /// <summary>
    /// 初始化日志服务
    /// </summary>
    public static void Initialize()
    {
        try
        {
            if (!Directory.Exists(LogDirectory))
            {
                Directory.CreateDirectory(LogDirectory);
            }

            // 使用日期作为日志文件名
            _currentLogFile = Path.Combine(LogDirectory, $"log_{DateTime.Now:yyyyMMdd}.txt");

            // 清理7天前的旧日志
            CleanupOldLogs(7);

            Info("========================================");
            Info($"NahidaTool 启动");
            Info($"版本: {AppVersion.Current}");
            Info($"操作系统: {GetWindowsVersion()}");
            Info($".NET版本: {Environment.Version}");
            Info($"进程架构: {RuntimeInformation.ProcessArchitecture}, OS架构: {RuntimeInformation.OSArchitecture}");
            Info($"管理员权限: {IsAdministrator()}");
            Info($"应用目录: {AppDomain.CurrentDomain.BaseDirectory}");
            Info("========================================");
        }
        catch (Exception ex)
        {
            // 日志初始化失败不应影响程序运行，但尝试写入紧急日志
            try
            {
                string crashLogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash_log.txt");
                File.AppendAllText(crashLogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 日志服务初始化失败: {ex.Message}\n{ex.StackTrace}\n");
            }
            catch { }
        }
    }

    /// <summary>
    /// 记录信息日志
    /// </summary>
    public static void Info(string message)
    {
        WriteLog("INFO", message);
    }

    /// <summary>
    /// 记录警告日志
    /// </summary>
    public static void Warn(string message)
    {
        WriteLog("WARN", message);
    }

    /// <summary>
    /// 记录错误日志
    /// </summary>
    public static void Error(string message)
    {
        WriteLog("ERROR", message);
    }

    /// <summary>
    /// 记录异常日志
    /// </summary>
    public static void Error(string message, Exception ex)
    {
        var sb = new StringBuilder();
        sb.AppendLine(message);
        sb.AppendLine($"异常类型: {ex.GetType().FullName}");
        sb.AppendLine($"异常信息: {ex.Message}");
        sb.AppendLine($"堆栈跟踪: {ex.StackTrace}");

        if (ex.InnerException != null)
        {
            sb.AppendLine($"内部异常: {ex.InnerException.Message}");
            sb.AppendLine($"内部堆栈: {ex.InnerException.StackTrace}");
        }

        WriteLog("ERROR", sb.ToString());
    }

    /// <summary>
    /// 记录调试日志
    /// </summary>
    public static void Debug(string message)
    {
#if DEBUG
        WriteLog("DEBUG", message);
#endif
    }

    private static void WriteLog(string level, string message)
    {
        if (string.IsNullOrEmpty(_currentLogFile))
            return;

        try
        {
            lock (_lock)
            {
                string logLine = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}";
                File.AppendAllText(_currentLogFile, logLine + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // 写日志失败不应影响程序运行
        }
    }

    private static void CleanupOldLogs(int keepDays)
    {
        try
        {
            var cutoffDate = DateTime.Now.AddDays(-keepDays);
            var logFiles = Directory.GetFiles(LogDirectory, "log_*.txt");

            foreach (var file in logFiles)
            {
                var fileInfo = new FileInfo(file);
                if (fileInfo.CreationTime < cutoffDate)
                {
                    fileInfo.Delete();
                }
            }
        }
        catch
        {
            // 清理失败不影响程序运行
        }
    }

    /// <summary>
    /// 获取日志目录路径
    /// </summary>
    public static string GetLogDirectory() => LogDirectory;

    /// <summary>
    /// 获取当前日志文件路径
    /// </summary>
    public static string? GetCurrentLogFile() => _currentLogFile;

    /// <summary>
    /// 获取友好的 Windows 版本名称
    /// </summary>
    private static string GetWindowsVersion()
    {
        try
        {
            var os = Environment.OSVersion;
            int build = os.Version.Build;

            string versionName;
            if (os.Version.Major == 10)
            {
                // Windows 11 的 build 号 >= 22000
                versionName = build >= 22000 ? "Windows 11" : "Windows 10";
            }
            else if (os.Version.Major == 6)
            {
                versionName = os.Version.Minor switch
                {
                    3 => "Windows 8.1",
                    2 => "Windows 8",
                    1 => "Windows 7",
                    _ => "Windows Vista"
                };
            }
            else
            {
                versionName = "Windows";
            }

            return $"{versionName} (Build {build})";
        }
        catch
        {
            return Environment.OSVersion.ToString();
        }
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
