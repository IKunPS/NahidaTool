using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NahidaTool.Models;
using NahidaTool.Models.Enum;

namespace NahidaTool.Models.Service;

/// <summary>
/// 调用与 NahidaTool.exe 同目录的 Nahida-Injector.exe，在游戏启动前注入 RSA 补丁。
/// </summary>
internal static class NahidaInjectorService
{
    private const string InjectorFileName = "Nahida-Injector.exe";
    private const int InjectorTimeoutSeconds = 30;
    private const int GameProcessWaitSeconds = 15;

    public static string InjectorPath => Path.Combine(AppContext.BaseDirectory, InjectorFileName);

    public static async Task<Process> StartGameAsync(
        string gameExePath,
        string dllPath,
        string arguments,
        ServerRegionType region,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(InjectorPath))
            throw new InvalidOperationException(Lang.HomePage_InjectorMissing);

        var startInfo = new ProcessStartInfo
        {
            FileName = InjectorPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        startInfo.ArgumentList.Add("--exe");
        startInfo.ArgumentList.Add(gameExePath);
        startInfo.ArgumentList.Add("--dll");
        startInfo.ArgumentList.Add(dllPath);
        if (!string.IsNullOrWhiteSpace(arguments))
        {
            startInfo.ArgumentList.Add("--args");
            startInfo.ArgumentList.Add(arguments);
        }

        Process injector;
        try
        {
            injector = Process.Start(startInfo) ?? throw new InvalidOperationException(
                string.Format(Lang.HomePage_InjectorStartFailed, "Process.Start returned null"));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 740)
        {
            throw new InvalidOperationException(Lang.HomePage_InjectorElevationRequired, ex);
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                string.Format(Lang.HomePage_InjectorStartFailed, ex.Message), ex);
        }

        using (injector)
        {
            Task<string> outputTask = injector.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> errorTask = injector.StandardError.ReadToEndAsync(cancellationToken);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(InjectorTimeoutSeconds));
            try
            {
                await injector.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(injector);
                throw new InvalidOperationException(Lang.HomePage_InjectorTimeout);
            }

            string output = await outputTask.ConfigureAwait(false);
            string error = await errorTask.ConfigureAwait(false);
            LogService.Debug($"Nahida-Injector 输出: {output.Trim()}");
            if (injector.ExitCode != 0)
            {
                string detail = string.IsNullOrWhiteSpace(error) ? output : error;
                if (string.IsNullOrWhiteSpace(detail))
                    detail = "(无输出)";
                throw new InvalidOperationException(string.Format(
                    Lang.HomePage_InjectorFailed, injector.ExitCode, detail.Trim()));
            }

            int? pid = ParsePid(output);
            if (pid.HasValue)
            {
                try
                {
                    return Process.GetProcessById(pid.Value);
                }
                catch (ArgumentException)
                {
                }
            }

            Process? gameProcess = await WaitForGameProcessAsync(region, cancellationToken)
                .ConfigureAwait(false);
            if (gameProcess != null)
                return gameProcess;

            throw new InvalidOperationException(Lang.HomePage_InjectorGameProcessMissing);
        }
    }

    private static async Task<Process?> WaitForGameProcessAsync(
        ServerRegionType region,
        CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(GameProcessWaitSeconds);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Process? process = GameLauncherService.GetRunningProcess(region);
            if (process != null)
                return process;
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    private static int? ParsePid(string output)
    {
        foreach (string line in output.Split('\n'))
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith("PID=", StringComparison.OrdinalIgnoreCase))
                continue;
            if (int.TryParse(trimmed.AsSpan(4), out int pid) && pid > 0)
                return pid;
        }
        return null;
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }
}
