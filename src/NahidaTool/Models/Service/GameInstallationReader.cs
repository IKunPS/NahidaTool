using System;
using System.IO;
using System.Text.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NahidaTool.Models.Enum;

namespace NahidaTool.Models.Service;

internal static class GameInstallationReader
{
    private const int MaximumMetadataBytes = 128 * 1024;

    internal static async Task<InstalledGameInfo> ReadAsync(string root, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            throw new GameInstallationDetectionException(GameInstallationIssue.DirectoryMissing);

        try
        {
            bool hasCnExe = File.Exists(Path.Combine(root, GameLauncherService.CNExeName));
            bool hasOsExe = File.Exists(Path.Combine(root, GameLauncherService.OSExeName));
            bool hasCnData = Directory.Exists(Path.Combine(root, "YuanShen_Data"));
            bool hasOsData = Directory.Exists(Path.Combine(root, "GenshinImpact_Data"));
            ServerRegionType expectedRegion;
            if (hasCnExe != hasOsExe)
                expectedRegion = hasCnExe ? ServerRegionType.CN : ServerRegionType.OS;
            else if (hasCnData != hasOsData)
                expectedRegion = hasCnData ? ServerRegionType.CN : ServerRegionType.OS;
            else
                throw new GameInstallationDetectionException(hasCnData || hasOsData
                    ? GameInstallationIssue.AmbiguousInstallation : GameInstallationIssue.DataMissing);

            string dataFolder = expectedRegion == ServerRegionType.CN ? "YuanShen_Data" : "GenshinImpact_Data";
            string metadataPath = Path.Combine(root, dataFolder, "StreamingAssets", "APMConfig.json");
            if (!File.Exists(metadataPath))
                throw new GameInstallationDetectionException(GameInstallationIssue.MetadataMissing);

            string json = await ReadMetadataTextAsync(metadataPath, ct).ConfigureAwait(false);
            var metadata = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.APMConfig)
                ?? throw new GameInstallationDetectionException(GameInstallationIssue.MetadataInvalid);
            ServerRegionType region = metadata.IsCN ? ServerRegionType.CN : metadata.IsOS ? ServerRegionType.OS
                : throw new GameInstallationDetectionException(GameInstallationIssue.RegionUnknown);
            if (region != expectedRegion)
                throw new GameInstallationDetectionException(GameInstallationIssue.RegionMismatch);

            string? version = NormalizeVersion(metadata.GetVersionNumber());
            // Installer config.ini and download preferences may be stale after a
            // client switch. Only the installed client's metadata is authoritative.
            if (version == null)
                throw new GameInstallationDetectionException(GameInstallationIssue.VersionMissing);
            return new InstalledGameInfo(region, version);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (JsonException ex)
        {
            throw new GameInstallationDetectionException(GameInstallationIssue.MetadataInvalid, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new GameInstallationDetectionException(GameInstallationIssue.MetadataUnreadable, ex);
        }
    }

    private static string? NormalizeVersion(string? value) =>
        Version.TryParse(value?.Trim(), out var version) && version.Major > 0 && version.Build >= 0
            ? version.ToString(3) : null;

    private static async Task<string> ReadMetadataTextAsync(string path, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 4096, useAsync: true);
        if (file.Length > MaximumMetadataBytes)
            throw new GameInstallationDetectionException(GameInstallationIssue.MetadataInvalid);
        using var reader = new StreamReader(file, detectEncodingFromByteOrderMarks: true);
        var text = new StringBuilder();
        char[] buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            // Bound reads as well as the initial file length: another process may
            // grow or rewrite the metadata while detection is in progress.
            if (text.Length + count > MaximumMetadataBytes)
                throw new GameInstallationDetectionException(GameInstallationIssue.MetadataInvalid);
            text.Append(buffer, 0, count);
        }
        ct.ThrowIfCancellationRequested();
        return text.ToString();
    }
}
