using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NahidaTool.Models.Config;

namespace NahidaTool.Models;

public sealed record ChangelogHistoryEntry(string VersionLabel, string SourceCommit, IReadOnlyList<string> Items)
{
    public string Summary => string.Format(CultureInfo.CurrentUICulture, Lang.Changelog_EntryCount, Items.Count);
    public string SourceLabel => string.IsNullOrEmpty(SourceCommit)
        ? string.Format(CultureInfo.CurrentUICulture, Lang.Changelog_HistoryBundledSource, VersionLabel)
        : string.Format(CultureInfo.CurrentUICulture, Lang.Changelog_HistorySource, SourceCommit);
}

internal static class ChangelogHistory
{
    #region 历史版本

    // 保留各版本原有日志，不根据提交标题补写或合并重复内容。
    private static readonly (string Version, string ResourceKey, string Commit)[] Releases =
    [
        ("0.1.8", "Changelog_History018", "56aca9fdad942f282a7c7051b9bb041c69fbaf9e"),
        ("0.1.7", "Changelog_History017", string.Empty),
        ("0.1.6", "Changelog_History016", "0b272061242eb689409794895a883cdd3282f2c3"),
        ("0.1.5", "Changelog_History015", "a7008f07887821aaf7e13f5108f9183d146f944e"),
        ("0.1.4", "Changelog_History014", "4b79a3b71d77e51cb196451b841b525891ce74ac"),
        ("0.1.3", "Changelog_History013", "8ad617a24c512a5e7a06fc09cf3a592d2c0e15e4")
    ];

    internal static IReadOnlyList<ChangelogHistoryEntry> GetEntries()
    {
        Version.TryParse(AppVersion.Current, out Version? current);
        return Array.AsReadOnly(Releases
            .Where(release => current == null || Version.Parse(release.Version) < current)
            .OrderByDescending(release => Version.Parse(release.Version))
            .Select(release => new ChangelogHistoryEntry(
                $"v{release.Version}", release.Commit, ReadItems(release.ResourceKey)))
            .ToArray());
    }

    private static IReadOnlyList<string> ReadItems(string resourceKey)
    {
        string notes = Lang.ResourceManager.GetString(resourceKey, CultureInfo.CurrentUICulture)
            ?? Lang.AppUpdate_NoReleaseNotes;
        return Array.AsReadOnly(notes.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.StartsWith("· ", StringComparison.Ordinal) ? line[2..] : line)
            .ToArray());
    }

    #endregion
}
