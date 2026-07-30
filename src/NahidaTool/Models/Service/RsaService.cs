using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace NahidaTool.Models.Service;

public static class RsaService
{
    #region Constants & P/Invoke

    private const string RsaFolderName = "Assets/Patch";
    private const string VersionDllName = "version.dll";
    private const string AstrolabeDllName = "Astrolabe.dll";

    private static string RsaDirectory => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, RsaFolderName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, uint nSize, out UIntPtr lpNumberOfBytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr lpThreadAttributes, uint dwStackSize, IntPtr lpStartAddress, IntPtr lpParameter, uint dwCreationFlags, out uint lpThreadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    private static extern uint GetLastError();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out long lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAllPrivileges, ref TOKEN_PRIVILEGES newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll")]
    private static extern bool IsWow64Process(IntPtr hProcess, out bool wow64Process);

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID_AND_ATTRIBUTES
    {
        public long Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID_AND_ATTRIBUTES Privilege;
    }

    private const int PROCESS_VM_OPERATION = 0x0008;
    private const int PROCESS_VM_WRITE = 0x0020;
    private const int PROCESS_VM_READ = 0x0010;
    private const int PROCESS_CREATE_THREAD = 0x0002;
    private const int PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_RESERVE = 0x2000;
    private const uint PAGE_READWRITE = 0x04;
    private const uint INFINITE = 0xFFFFFFFF;
    private const uint SE_PRIVILEGE_ENABLED = 0x2;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;

    #endregion

    #region DLL Discovery

    private static (int major, int minor)? ParseGameVersion(string? gameVersion)
    {
        if (string.IsNullOrEmpty(gameVersion))
            return null;

        var match = Regex.Match(gameVersion, @"(\d+)\.(\d+)");
        if (!match.Success)
            return null;

        if (!int.TryParse(match.Groups[1].Value, out int major))
            return null;
        if (!int.TryParse(match.Groups[2].Value, out int minor))
            return null;

        return (major, minor);
    }

    private static List<(string path, int priority, int major, int? minorStart, int? minorEnd)> FindCandidateDlls()
    {
        var candidates = new List<(string, int, int, int?, int?)>();

        if (!Directory.Exists(RsaDirectory))
            return candidates;

        foreach (var file in Directory.GetFiles(RsaDirectory, "*version.dll", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(file);
            var match = Regex.Match(fileName, @"^(\d+)version\.dll$", RegexOptions.IgnoreCase);
            if (!match.Success)
                continue;

            var digits = match.Groups[1].Value;
            int major, digitCount = digits.Length;

            if (digitCount == 1)
            {
                if (!int.TryParse(digits, out major))
                    continue;
                candidates.Add((file, 1, major, null, null));
            }
            else if (digitCount == 2)
            {
                major = int.Parse(digits[0].ToString());
                int minor = int.Parse(digits[1].ToString());
                candidates.Add((file, 3, major, minor, minor));
            }
            else if (digitCount >= 3)
            {
                major = int.Parse(digits[0].ToString());
                int minorStart = int.Parse(digits[1].ToString());
                int minorEnd = int.Parse(digits.Substring(2));
                candidates.Add((file, 2, major, minorStart, minorEnd));
            }
        }

        return candidates;
    }

    public static string? FindMatchingRsaDll(string? gameVersion)
    {
        var version = ParseGameVersion(gameVersion);
        if (version == null)
            return null;

        var (major, minor) = version.Value;
        var candidates = FindCandidateDlls();

        var match = candidates
            .Where(c => c.major == major &&
                        (c.minorStart == null ||
                         (minor >= c.minorStart.Value && minor <= c.minorEnd!.Value)))
            .OrderByDescending(c => c.priority)
            .FirstOrDefault();

        return match.path;
    }

    public static bool CopyRsaToGameDirectory(string? gameVersion, string gameInstallPath)
    {
        try
        {
            var rsaDll = FindMatchingRsaDll(gameVersion);
            if (rsaDll == null)
            {
                LogService.Error($"未找到匹配 {gameVersion ?? "未知"} 的 RSA DLL");
                return false;
            }

            var version = ParseGameVersion(gameVersion);
            bool isBelow46 = version != null && (version.Value.major < 4 || (version.Value.major == 4 && version.Value.minor < 6));
            var targetName = isBelow46 ? VersionDllName : AstrolabeDllName;
            var targetPath = Path.Combine(gameInstallPath, targetName);

            if (File.Exists(targetPath))
            {
                var bakPath = targetPath + ".bak";
                if (File.Exists(bakPath))
                    File.Delete(bakPath);
                File.Move(targetPath, bakPath);
                LogService.Info($"已备份现有 {targetName} → {Path.GetFileName(bakPath)}");
            }

            File.Copy(rsaDll, targetPath, overwrite: true);
            LogService.Info($"RSA: {Path.GetFileName(rsaDll)} → {targetName}");
            return true;
        }
        catch (Exception ex)
        {
            LogService.Error("复制 RSA DLL 失败", ex);
            return false;
        }
    }

    /// <summary>
    /// 提权：启用 SeDebugPrivilege，允许打开受保护进程（如管理员启动的游戏）
    /// </summary>
    private static bool EnableDebugPrivilege()
    {
        try
        {
            var currentProcess = Process.GetCurrentProcess();
            if (!OpenProcessToken(currentProcess.Handle, TOKEN_QUERY | TOKEN_ADJUST_PRIVILEGES, out var tokenHandle))
                return false;

            try
            {
                if (!LookupPrivilegeValue(null, "SeDebugPrivilege", out var luid))
                    return false;

                var tp = new TOKEN_PRIVILEGES
                {
                    PrivilegeCount = 1,
                    Privilege = new LUID_AND_ATTRIBUTES { Luid = luid, Attributes = SE_PRIVILEGE_ENABLED }
                };

                if (!AdjustTokenPrivileges(tokenHandle, false, ref tp, (uint)Marshal.SizeOf<TOKEN_PRIVILEGES>(), IntPtr.Zero, IntPtr.Zero))
                    return false;

                // ERROR_NOT_ALL_ASSIGNED (1300) 表示权限不可用，但仍算成功
                return Marshal.GetLastWin32Error() != 1300;
            }
            finally
            {
                CloseHandle(tokenHandle);
            }
        }
        catch
        {
            return false;
        }
    }

    private static IntPtr OpenGameProcess(int processId)
    {
        const int desiredAccess = PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ |
                                  PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION;

        // 先尝试直接打开
        var hProcess = OpenProcess(desiredAccess, false, processId);
        if (hProcess != IntPtr.Zero)
            return hProcess;

        var lastError = (int)GetLastError();
        LogService.Debug($"OpenProcess 失败 (错误: {lastError}), 尝试提权...");

        // 如果被拒绝访问，尝试启用 SeDebugPrivilege
        if (lastError == 5 && EnableDebugPrivilege()) // ERROR_ACCESS_DENIED
        {
            hProcess = OpenProcess(desiredAccess, false, processId);
            if (hProcess != IntPtr.Zero)
            {
                LogService.Info("提权后成功打开游戏进程");
                return hProcess;
            }
            lastError = (int)GetLastError();
        }

        LogService.Error($"无法打开游戏进程 (PID: {processId}), 错误: {lastError}");
        return IntPtr.Zero;
    }

    /// <summary>
    /// 尝试注入 DLL，带重试，带退避延迟
    /// </summary>
    private static bool TryInjectDll(IntPtr hProcess, string fullDllPath)
    {
        // LoadLibraryA 需要 ANSI 编码，不是 UTF-8
        var dllPathBytes = Encoding.Default.GetBytes(fullDllPath + '\0');
        var allocSize = (uint)dllPathBytes.Length;

        var remoteMemory = VirtualAllocEx(hProcess, IntPtr.Zero, allocSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (remoteMemory == IntPtr.Zero)
        {
            LogService.Error($"无法在游戏进程中分配内存, 错误: {GetLastError()}");
            return false;
        }

        if (!WriteProcessMemory(hProcess, remoteMemory, dllPathBytes, allocSize, out _))
        {
            LogService.Error($"无法写入 DLL 路径, 错误: {GetLastError()}");
            return false;
        }

        var hKernel32 = GetModuleHandle("kernel32.dll");
        var loadLibraryAddr = GetProcAddress(hKernel32, "LoadLibraryA");
        if (loadLibraryAddr == IntPtr.Zero)
        {
            LogService.Error($"无法获取 LoadLibraryA 地址, 错误: {GetLastError()}");
            return false;
        }

        var hRemoteThread = CreateRemoteThread(hProcess, IntPtr.Zero, 0, loadLibraryAddr, remoteMemory, 0, out _);
        if (hRemoteThread == IntPtr.Zero)
        {
            LogService.Error($"无法创建远程线程, 错误: {GetLastError()}");
            return false;
        }

        WaitForSingleObject(hRemoteThread, 5000);
        CloseHandle(hRemoteThread);
        return true;
    }

    public static async Task<bool> InjectHookRsaAsync(Process gameProcess, string? gameVersion)
    {
        try
        {
            var rsaDll = FindMatchingRsaDll(gameVersion);
            if (rsaDll == null)
            {
                LogService.Error($"未找到匹配 {gameVersion ?? "未知"} 的 RSA DLL 用于注入");
                return false;
            }

            var fullDllPath = Path.GetFullPath(rsaDll);
            if (!File.Exists(fullDllPath))
            {
                LogService.Error($"RSA DLL 不存在: {fullDllPath}");
                return false;
            }

            LogService.Info($"Hook RSA: 准备注入 {Path.GetFileName(rsaDll)}");

            // 重试注入：2s, 4s, 6s, 10s — 给游戏进程足够时间初始化
            int[] retryDelays = { 2000, 4000, 6000, 10000 };
            for (int i = 0; i < retryDelays.Length; i++)
            {
                await Task.Delay(retryDelays[i]);

                // 进程已退出就放弃
                try
                {
                    if (gameProcess.HasExited)
                    {
                        LogService.Warn("游戏进程已退出，放弃注入");
                        return false;
                    }
                }
                catch
                {
                    // HasExited 可能抛异常
                }

                var hProcess = OpenGameProcess(gameProcess.Id);
                if (hProcess == IntPtr.Zero)
                {
                    LogService.Debug($"第 {i + 1} 次 OpenProcess 失败，将重试...");
                    continue;
                }

                try
                {
                    if (TryInjectDll(hProcess, fullDllPath))
                    {
                        LogService.Info($"Hook RSA: 已注入 {Path.GetFileName(rsaDll)} → 游戏进程 (PID: {gameProcess.Id}, 第 {i + 1} 次尝试)");
                        return true;
                    }
                }
                finally
                {
                    CloseHandle(hProcess);
                }

                LogService.Debug($"第 {i + 1} 次注入失败，将重试...");
            }

            LogService.Error($"Hook RSA 注入失败: 所有 {retryDelays.Length} 次尝试均失败");
            return false;
        }
        catch (Exception ex)
        {
            LogService.Error("Hook RSA 注入失败", ex);
            return false;
        }
    }

    public static void CleanupRsaFromGameDirectory(string gameInstallPath)
    {
        try
        {
            foreach (var targetName in new[] { VersionDllName, AstrolabeDllName })
            {
                var targetPath = Path.Combine(gameInstallPath, targetName);
                var bakPath = targetPath + ".bak";

                if (File.Exists(bakPath))
                {
                    if (File.Exists(targetPath))
                        File.Delete(targetPath);
                    File.Move(bakPath, targetPath);
                    LogService.Info($"RSA: 已恢复原始 {targetName}");
                }
                else if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                    LogService.Info($"RSA: 已删除 {targetName}");
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Error("清理 RSA DLL 失败", ex);
        }
    }

    #endregion
}
