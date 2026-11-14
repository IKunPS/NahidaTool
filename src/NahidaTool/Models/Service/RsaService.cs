using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace NahidaTool.Models.Service;

public static class RsaService
{
    private const string RsaFolderName = "Assets/Patch";
    private const string VersionDllName = "version.dll";
    private const string AstrolabeDllName = "Astrolabe.dll";

    private static string RsaDirectory => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, RsaFolderName);

    private sealed record RsaPatchCandidate(
        string Path,
        int Priority,
        int Major,
        int? MinorStart,
        int? MinorEnd)
    {
        public int RangeWidth => MinorStart.HasValue && MinorEnd.HasValue
            ? MinorEnd.Value - MinorStart.Value
            : int.MaxValue;

        public bool Matches(int major, int minor) =>
            Major == major &&
            (!MinorStart.HasValue ||
             (minor >= MinorStart.Value && minor <= MinorEnd!.Value));
    }

    private static (int major, int minor)? ParseGameVersion(string? gameVersion)
    {
        if (string.IsNullOrWhiteSpace(gameVersion))
            return null;

        var match = Regex.Match(gameVersion, @"(?<!\d)(\d+)\.(\d+)");
        if (!match.Success ||
            !int.TryParse(match.Groups[1].Value, out int major) ||
            !int.TryParse(match.Groups[2].Value, out int minor))
        {
            return null;
        }

        return (major, minor);
    }

    private static bool IsUsablePatchDll(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return stream.Length >= 2 && stream.ReadByte() == 'M' && stream.ReadByte() == 'Z';
        }
        catch (Exception ex)
        {
            LogService.Warn($"RSA: 无法读取补丁 {Path.GetFileName(path)}: {ex.Message}");
            return false;
        }
    }

    private static RsaPatchCandidate? ParsePatchCandidate(string path)
    {
        string fileName = Path.GetFileName(path);

        // 推荐的无歧义命名：7.0version.dll / 6.4-8version.dll。
        var rangeMatch = Regex.Match(fileName,
            @"^(?<major>\d+)\.(?<start>\d+)-(?<end>\d+)version\.dll$",
            RegexOptions.IgnoreCase);
        if (rangeMatch.Success &&
            int.TryParse(rangeMatch.Groups["major"].Value, out int rangeMajor) &&
            int.TryParse(rangeMatch.Groups["start"].Value, out int rangeStart) &&
            int.TryParse(rangeMatch.Groups["end"].Value, out int rangeEnd) &&
            rangeStart <= rangeEnd)
        {
            return new RsaPatchCandidate(path, 2, rangeMajor, rangeStart, rangeEnd);
        }

        var exactMatch = Regex.Match(fileName,
            @"^(?<major>\d+)\.(?<minor>\d+)version\.dll$",
            RegexOptions.IgnoreCase);
        if (exactMatch.Success &&
            int.TryParse(exactMatch.Groups["major"].Value, out int exactMajor) &&
            int.TryParse(exactMatch.Groups["minor"].Value, out int exactMinor))
        {
            return new RsaPatchCandidate(path, 3, exactMajor, exactMinor, exactMinor);
        }

        // 兼容历史紧凑命名：6（主版本兜底）、46/70（精确）、648（范围）。
        var compactMatch = Regex.Match(fileName, @"^(?<digits>\d+)version\.dll$", RegexOptions.IgnoreCase);
        if (!compactMatch.Success)
            return null;

        string digits = compactMatch.Groups["digits"].Value;
        if (digits.Length == 1 && int.TryParse(digits, out int fallbackMajor))
            return new RsaPatchCandidate(path, 1, fallbackMajor, null, null);

        if (digits.Length == 2 &&
            int.TryParse(digits[..1], out int compactExactMajor) &&
            int.TryParse(digits[1..], out int compactExactMinor))
        {
            return new RsaPatchCandidate(path, 3, compactExactMajor, compactExactMinor, compactExactMinor);
        }

        if (digits.Length >= 3 &&
            int.TryParse(digits[..1], out int compactRangeMajor) &&
            int.TryParse(digits.Substring(1, 1), out int compactRangeStart) &&
            int.TryParse(digits[2..], out int compactRangeEnd) &&
            compactRangeStart <= compactRangeEnd)
        {
            return new RsaPatchCandidate(path, 2, compactRangeMajor, compactRangeStart, compactRangeEnd);
        }

        return null;
    }

    private static List<RsaPatchCandidate> FindCandidateDlls()
    {
        var candidates = new List<RsaPatchCandidate>();

        if (!Directory.Exists(RsaDirectory))
            return candidates;

        string[] files;
        try
        {
            files = Directory.GetFiles(RsaDirectory, "*version.dll", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            LogService.Error("RSA: 无法枚举补丁目录", ex);
            return candidates;
        }

        foreach (var file in files)
        {
            var candidate = ParsePatchCandidate(file);
            if (candidate == null)
                continue;

            if (!IsUsablePatchDll(file))
            {
                LogService.Warn($"RSA: 已忽略无效补丁 {Path.GetFileName(file)}");
                continue;
            }

            candidates.Add(candidate);
        }

        return candidates;
    }

    public static string? FindMatchingRsaDll(string? gameVersion)
    {
        var version = ParseGameVersion(gameVersion);
        if (version == null)
        {
            LogService.Warn($"RSA: 无法识别游戏版本 {gameVersion ?? "未知"}");
            return null;
        }

        var (major, minor) = version.Value;
        var match = FindCandidateDlls()
            .Where(candidate => candidate.Matches(major, minor))
            .OrderByDescending(candidate => candidate.Priority)
            .ThenBy(candidate => candidate.RangeWidth)
            .ThenBy(candidate => Path.GetFileName(candidate.Path), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (match == null)
        {
            LogService.Warn($"RSA: 没有适用于 {major}.{minor} 的补丁");
            return null;
        }

        string matchType = match.Priority switch
        {
            3 => "精确版本",
            2 => "版本范围",
            _ => "主版本兜底"
        };
        LogService.Info($"RSA: {major}.{minor} 匹配 {Path.GetFileName(match.Path)} ({matchType})");
        return match.Path;
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
}
