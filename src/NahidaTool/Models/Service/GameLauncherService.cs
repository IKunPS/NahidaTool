using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Win32;
using NahidaTool.Models;
using NahidaTool.Models.Config;
using NahidaTool.Models.Enum;

namespace NahidaTool.Models.Service;

public static class GameLauncherService
{
    public const string CNExeName = "YuanShen.exe";
    public const string OSExeName = "GenshinImpact.exe";

    private const string CNRegistryPath = @"HKEY_CURRENT_USER\Software\miHoYo\HYP\1_1\hk4e_cn";
    private const string OSRegistryPath = @"HKEY_CURRENT_USER\Software\Cognosphere\HYP\1_0\hk4e_global";

    public static string GetExeName(ServerRegionType region)
    {
        return region == ServerRegionType.CN ? CNExeName : OSExeName;
    }

    /// <summary>
    /// 从注册表自动搜索游戏安装路径
    /// </summary>
    public static string? TryFindFromRegistry(ServerRegionType region)
    {
        var keyPath = region switch
        {
            ServerRegionType.CN => CNRegistryPath,
            ServerRegionType.OS => OSRegistryPath,
            _ => CNRegistryPath
        };

        var path = Registry.GetValue(keyPath, "GameInstallPath", null) as string;
        if (string.IsNullOrEmpty(path))
            return null;

        var exeName = GetExeName(region);
        var exePath = Path.Combine(path, exeName);
        if (File.Exists(exePath))
            return Path.GetFullPath(path);

        return null;
    }

    /// <summary>
    /// 异步从注册表搜索游戏安装路径（不阻塞 UI 线程）
    /// </summary>
    public static Task<string?> TryFindFromRegistryAsync(ServerRegionType region)
    {
        return Task.Run(() => TryFindFromRegistry(region));
    }

    /// <summary>
    /// 自动搜索并保存游戏路径
    /// </summary>
    public static string? AutoDetectAndSave(ServerRegionType region)
    {
        var settings = AppSettings.Load();

        // 先检查已保存的路径
        if (IsValidInstallPath(settings.GameInstallPath, region))
            return settings.GameInstallPath;

        // 尝试注册表
        var found = TryFindFromRegistry(region);
        if (found != null)
        {
            SaveInstallPath(found);
            return found;
        }

        return null;
    }

    /// <summary>
    /// 在目录中查找实际存在的游戏exe（两个区域都试）
    /// </summary>
    private static string? FindActualExe(string installPath)
    {
        foreach (var name in new[] { CNExeName, OSExeName })
        {
            var full = Path.Combine(installPath, name);
            if (File.Exists(full))
                return full;
        }
        return null;
    }

    /// <summary>
    /// 检查路径是否包含有效的游戏exe（任一区域）
    /// </summary>
    public static bool IsValidInstallPath(string? path, ServerRegionType region)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            return false;

        return FindActualExe(path) != null;
    }

    /// <summary>
    /// 检查游戏是否正在运行（两个区域都查）
    /// </summary>
    public static Process? GetRunningProcess(ServerRegionType region)
    {
        foreach (var name in new[] { CNExeName, OSExeName })
        {
            var procName = Path.GetFileNameWithoutExtension(name);
            var processes = Process.GetProcessesByName(procName);
            if (processes.Length > 0)
                return processes[0];
        }
        return null;
    }

    private static bool IsGameProcess(Process process)
    {
        try
        {
            return string.Equals(process.ProcessName, Path.GetFileNameWithoutExtension(CNExeName), StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(process.ProcessName, Path.GetFileNameWithoutExtension(OSExeName), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<Process?> WaitForGameProcessAsync(ServerRegionType region, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var process = GetRunningProcess(region);
            if (process != null)
                return process;

            await Task.Delay(250);
        }

        return null;
    }

    /// <summary>
    /// 启动游戏（参考 Starward 实现）
    /// </summary>
    public static async Task<Process?> StartGameAsync(ServerRegionType region, string? installPath = null)
    {
        var settings = AppSettings.Load();
        var path = installPath ?? settings.GameInstallPath;

        if (string.IsNullOrEmpty(path))
            throw new InvalidOperationException("未找到游戏安装路径");

        var existing = GetRunningProcess(region);
        if (existing != null)
            throw new InvalidOperationException("游戏已在运行中");

        // 确定启动的 exe 路径（两个区域都试，不因切服而找不到已安装的游戏）
        string? exePath;
        string verb = "runas";
        bool thirdPartyTool = false;

        // 优先使用自定义启动程序
        if (settings.EnableThirdPartyTool && !string.IsNullOrWhiteSpace(settings.ThirdPartyToolPath) && File.Exists(settings.ThirdPartyToolPath))
        {
            exePath = settings.ThirdPartyToolPath;
            thirdPartyTool = true;
            var ext = Path.GetExtension(exePath).ToLowerInvariant();
            verb = ext is ".exe" or ".bat" ? "runas" : "";
            LogService.Info($"使用自定义启动程序: {exePath}");
        }
        else
        {
            exePath = FindActualExe(path);
            if (exePath is null)
                throw new FileNotFoundException($"找不到游戏文件 ({CNExeName} 或 {OSExeName})", path);
        }

        bool useRsa = settings.EnableRSA;
        bool useHookRsa = useRsa && settings.EnableHookRSA;
        if (useRsa && RsaService.FindMatchingRsaDll(settings.GameVersion) == null)
        {
            throw new InvalidOperationException(string.Format(
                Lang.HomePage_RsaPatchUnavailable,
                string.IsNullOrWhiteSpace(settings.GameVersion)
                    ? Lang.DownloadPage_UnknownVersion
                    : settings.GameVersion));
        }

        if (useRsa && !useHookRsa)
        {
            LogService.Info("RSA: 正在部署 RSA DLL 到游戏目录...");
            if (!RsaService.CopyRsaToGameDirectory(settings.GameVersion, path))
            {
                throw new InvalidOperationException(string.Format(
                    Lang.HomePage_RsaPatchUnavailable,
                    settings.GameVersion));
            }
        }

        return await Task.Run(async () =>
        {
            try
            {
                // 构建启动参数
                var arg = settings.StartGameArgument?.Trim() ?? string.Empty;

                if (settings.EnablePopupWindow)
                {
                    arg = string.IsNullOrEmpty(arg) ? "-popupwindow" : $"{arg} -popupwindow";
                }

                // 非第三方工具且启用 StartGameWithCMD 时，包装为 cmd.exe /c start
                if (!thirdPartyTool && settings.StartGameWithCMD)
                {
                    var dir = Path.GetDirectoryName(exePath) ?? path;
                    arg = $"""/c start "" /d "{EscapeCmdArgument(dir)}" "{EscapeCmdArgument(exePath)}" {arg}""";
                    exePath = "cmd.exe";
                    verb = "";
                }

                var info = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = arg,
                    UseShellExecute = true,
                    Verb = verb,
                    WorkingDirectory = Path.GetDirectoryName(exePath) ?? path,
                };

                var startedProcess = Process.Start(info);

                if (startedProcess != null)
                {
                    LogService.Info($"启动命令已执行: {Path.GetFileName(exePath)} (PID: {startedProcess.Id})");
                }

                Process? gameProcess = startedProcess;
                bool launchedThroughWrapper = thirdPartyTool || settings.StartGameWithCMD;
                if (startedProcess != null && (useHookRsa || launchedThroughWrapper) && !IsGameProcess(startedProcess))
                {
                    LogService.Info("正在等待实际游戏进程启动...");
                    gameProcess = await WaitForGameProcessAsync(region, TimeSpan.FromSeconds(60));
                    if (gameProcess != null)
                    {
                        LogService.Info($"已找到实际游戏进程: {gameProcess.ProcessName}.exe (PID: {gameProcess.Id})");
                    }
                    else
                    {
                        LogService.Error("等待实际游戏进程超时，已跳过 Hook RSA 注入");
                    }
                }

                if (gameProcess != null && IsGameProcess(gameProcess))
                {
                    // 优化: 可选的 CPU 亲和性设置，减少跨核心上下文切换
                    if (settings.EnableCpuAffinity && settings.ProcessorAffinityMask != 0)
                    {
                        try
                        {
                            gameProcess.ProcessorAffinity = (IntPtr)settings.ProcessorAffinityMask;
                            LogService.Info($"CPU 亲和性已设置: 0x{settings.ProcessorAffinityMask:X}");
                        }
                        catch (Exception ex)
                        {
                            LogService.Debug($"CPU 亲和性设置失败 (非关键): {ex.Message}");
                        }
                    }
                }

                if (gameProcess != null && IsGameProcess(gameProcess) && useHookRsa)
                {
                    var hookTarget = gameProcess;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await RsaService.InjectHookRsaAsync(hookTarget, settings.GameVersion);
                        }
                        catch (Exception ex)
                        {
                            LogService.Error("Hook RSA 注入失败", ex);
                        }
                    });
                }

                return gameProcess;
            }
            catch (Exception ex) when (ex is not InvalidOperationException && ex is not FileNotFoundException)
            {
                LogService.Error("启动游戏进程时发生未预期错误", ex);
                throw;
            }
        });
    }

    /// <summary>
    /// 保存游戏路径到配置
    /// </summary>
    public static void SaveInstallPath(string path)
    {
        AppSettings.Update(settings => settings.GameInstallPath = path);
    }

    /// <summary>
    /// 异步保存游戏路径
    /// </summary>
    public static async Task SaveInstallPathAsync(string path)
    {
        await Task.Run(() => SaveInstallPath(path));
    }

    private static string EscapeCmdArgument(string value)
    {
        return value.Replace("\"", "\\\"", StringComparison.Ordinal);
    }
}
