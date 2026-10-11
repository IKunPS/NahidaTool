using System;
using NahidaTool.Models.Enum;

namespace NahidaTool.Models;

internal sealed record InstalledGameInfo(ServerRegionType Region, string Version);

internal enum GameInstallationIssue
{
    DirectoryMissing,
    DataMissing,
    MetadataMissing,
    MetadataInvalid,
    MetadataUnreadable,
    RegionUnknown,
    RegionMismatch,
    AmbiguousInstallation,
    VersionMissing
}

internal sealed class GameInstallationDetectionException(GameInstallationIssue issue, Exception? inner = null)
    : InvalidOperationException($"Game installation detection failed: {issue}", inner)
{
    internal GameInstallationIssue Issue { get; } = issue;
}
