using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NahidaTool.Models.Config;

namespace NahidaTool.Models.Service;

public sealed class AppUpdateInfo
{
    public required string Version { get; init; }
    public required string ReleaseName { get; init; }
    public required string ReleaseNotes { get; init; }
    public DateTimeOffset PublishedAt { get; init; }
    public required string PackageUrl { get; init; }
    public required string PackageName { get; init; }
    public long PackageSize { get; init; }
    public required string Sha256 { get; init; }
    public required string SourceServer { get; init; }
}

public sealed class AppUpdateStageResult
{
    public required string LauncherPath { get; init; }
    public required string VersionDirectory { get; init; }
}

public readonly record struct AppUpdateProgress(long Current, long Total);

public sealed class AppUpdateService
{
    private const string UpdateMetadataPath = "/update/nahida-tool/latest";
    private const int BufferSize = 1024 * 1024;
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly AppSettings _settings;

    public event Action<string>? StatusChanged;

    public AppUpdateService(AppSettings settings)
    {
        _settings = settings;
    }

    public async Task<AppUpdateInfo?> CheckForUpdateAsync(string currentVersion, CancellationToken ct = default)
    {
        string architecture = GetArchitectureName();
        foreach (string serverBaseUrl in PrivateServerService.GetCandidateUpdateServerUrls(_settings))
        {
            string metadataUrl = serverBaseUrl.TrimEnd('/') + UpdateMetadataPath +
                                 $"?arch={Uri.EscapeDataString(architecture)}&channel=stable";
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                using var request = new HttpRequestMessage(HttpMethod.Get, metadataUrl);
                using HttpResponseMessage response = await HttpClient.SendAsync(request, timeout.Token);
                if (response.StatusCode == HttpStatusCode.NotFound)
                    continue;

                response.EnsureSuccessStatusCode();
                await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                ServerAppUpdateResponse? document = await JsonSerializer.DeserializeAsync<ServerAppUpdateResponse>(
                    stream, JsonOptions, timeout.Token);
                if (document?.RetCode is int retCode && retCode != 0)
                    throw new InvalidDataException($"程序更新服务器返回错误：{document.Message ?? retCode.ToString()}");
                ServerAppUpdateMetadata? metadata = document?.Update ?? document?.AsMetadata();
                if (metadata == null || string.IsNullOrWhiteSpace(metadata.Version))
                    throw new InvalidDataException("程序更新元数据缺少版本号");

                string releaseVersion = metadata.Version.Trim().TrimStart('v', 'V');
                if (!IsNewerVersion(releaseVersion, currentVersion))
                    return null;

                if (string.IsNullOrWhiteSpace(metadata.PackageUrl))
                    throw new InvalidDataException($"更新服务器缺少 {architecture} 程序包");

                string sha256 = NormalizeSha256(metadata.Sha256 ?? string.Empty);
                Uri packageUri = ResolvePackageUri(metadataUrl, metadata.PackageUrl);
                string packageName = Path.GetFileName(packageUri.AbsolutePath);
                if (string.IsNullOrWhiteSpace(packageName))
                    packageName = $"NahidaTool-{architecture}.zip";

                return new AppUpdateInfo
                {
                    Version = releaseVersion,
                    ReleaseName = string.IsNullOrWhiteSpace(metadata.ReleaseName)
                        ? releaseVersion
                        : metadata.ReleaseName,
                    ReleaseNotes = metadata.ReleaseNotes ?? string.Empty,
                    PublishedAt = metadata.PublishedAt ?? DateTimeOffset.MinValue,
                    PackageUrl = packageUri.AbsoluteUri,
                    PackageName = SafePathSegment(packageName),
                    PackageSize = metadata.PackageSize,
                    Sha256 = sha256,
                    SourceServer = serverBaseUrl,
                };
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                LogService.Debug($"程序更新服务器请求超时：{metadataUrl}");
            }
            catch (Exception ex)
            {
                LogService.Debug($"检查程序更新失败 ({metadataUrl})：{ex.Message}");
            }
        }

        return null;
    }

    public async Task<AppUpdateStageResult> DownloadAndStageAsync(
        AppUpdateInfo update,
        IProgress<AppUpdateProgress>? progress = null,
        CancellationToken ct = default)
    {
        string currentAppDirectory = Path.GetFullPath(AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string installRoot = Directory.GetParent(currentAppDirectory)?.FullName ??
                             throw new DirectoryNotFoundException("无法确定 NahidaTool 安装目录");
        string workRoot = Path.Combine(installRoot, ".update", Guid.NewGuid().ToString("N"));
        string archivePath = Path.Combine(workRoot, update.PackageName);
        string extractRoot = Path.Combine(workRoot, "extracted");
        string? finalVersionDirectory = null;
        string? pendingLauncher = null;

        Directory.CreateDirectory(workRoot);
        try
        {
            SetStatus("正在下载程序更新...");
            await DownloadFileAsync(update.PackageUrl, archivePath, update.PackageSize, progress, ct);

            SetStatus("正在校验 SHA-256...");
            string actualHash = await ComputeSha256Async(archivePath, ct);
            if (!string.Equals(actualHash, update.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新包 SHA-256 校验失败");

            SetStatus("正在准备新版本...");
            Directory.CreateDirectory(extractRoot);
            await ExtractArchiveSafelyAsync(archivePath, extractRoot, ct);

            string payloadDirectory = Path.Combine(extractRoot, "app");
            string payloadExecutable = Path.Combine(payloadDirectory, "NahidaTool.exe");
            string packagedLauncher = Path.Combine(extractRoot, "NahidaTool.exe");
            if (!File.Exists(payloadExecutable) || !File.Exists(packagedLauncher))
                throw new InvalidDataException("更新包结构无效：缺少启动器或 app/NahidaTool.exe");

            string safeVersion = SafePathSegment(update.Version);
            finalVersionDirectory = Path.Combine(installRoot, $"app-{safeVersion}");
            if (Directory.Exists(finalVersionDirectory))
                finalVersionDirectory += $"-{DateTime.UtcNow:yyyyMMddHHmmss}";

            Directory.Move(payloadDirectory, finalVersionDirectory);
            File.SetLastWriteTimeUtc(Path.Combine(finalVersionDirectory, "NahidaTool.exe"), DateTime.UtcNow);

            string launcherPath = Path.Combine(installRoot, "NahidaTool.exe");
            pendingLauncher = launcherPath + ".new";
            File.Copy(packagedLauncher, pendingLauncher, true);
            File.Move(pendingLauncher, launcherPath, true);
            pendingLauncher = null;

            SetStatus("更新已就绪，正在重启...");
            return new AppUpdateStageResult
            {
                LauncherPath = launcherPath,
                VersionDirectory = finalVersionDirectory,
            };
        }
        catch
        {
            if (pendingLauncher != null)
                TryDeleteFile(pendingLauncher);
            if (finalVersionDirectory != null)
                TryDeleteDirectory(finalVersionDirectory);
            throw;
        }
        finally
        {
            TryDeleteDirectory(workRoot);
            TryDeleteEmptyDirectory(Path.GetDirectoryName(workRoot)!);
        }
    }

    public static void RestartAndExit(AppUpdateStageResult stagedUpdate)
    {
        if (!File.Exists(stagedUpdate.LauncherPath))
            throw new FileNotFoundException("NahidaTool 启动器不存在", stagedUpdate.LauncherPath);

        var startInfo = new ProcessStartInfo(stagedUpdate.LauncherPath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(stagedUpdate.LauncherPath)!,
        };
        startInfo.ArgumentList.Add("--wait-pid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        global::NahidaTool.App.ReleaseSingleInstanceForUpdate();
        try
        {
            _ = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 NahidaTool 更新启动器");
        }
        catch
        {
            global::NahidaTool.App.RestoreSingleInstanceAfterUpdateFailure();
            throw;
        }
        Environment.Exit(0);
    }

    private static async Task DownloadFileAsync(
        string url,
        string destination,
        long expectedSize,
        IProgress<AppUpdateProgress>? progress,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using HttpResponseMessage response = await HttpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long total = response.Content.Headers.ContentLength ?? expectedSize;
        await using Stream input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] buffer = new byte[BufferSize];
        long current = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            current += read;
            progress?.Report(new AppUpdateProgress(current, total));
        }
        await output.FlushAsync(ct);

        if (expectedSize > 0 && current != expectedSize)
            throw new InvalidDataException("更新包下载大小不匹配");
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using SHA256 sha256 = SHA256.Create();
        byte[] hash = await sha256.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task ExtractArchiveSafelyAsync(string archivePath, string extractRoot, CancellationToken ct)
    {
        string normalizedRoot = Path.GetFullPath(extractRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            string relativePath = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(relativePath))
                continue;

            string destination = Path.GetFullPath(Path.Combine(extractRoot, relativePath));
            if (!destination.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"更新包包含越界路径：{entry.FullName}");

            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using Stream input = entry.Open();
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(output, BufferSize, ct);
        }
    }

    private static bool IsNewerVersion(string releaseVersion, string currentVersion)
    {
        return TryGetComparableVersion(releaseVersion, out Version release) &&
               TryGetComparableVersion(currentVersion, out Version current) &&
               release > current;
    }

    private static bool TryGetComparableVersion(string value, out Version comparable)
    {
        comparable = new Version();
        if (!Version.TryParse(NormalizeVersion(value), out Version? parsed))
            return false;

        comparable = new Version(
            parsed.Major,
            parsed.Minor,
            Math.Max(parsed.Build, 0),
            Math.Max(parsed.Revision, 0));
        return true;
    }

    private static string NormalizeVersion(string value)
    {
        string normalized = value.Trim().TrimStart('v', 'V');
        int suffix = normalized.IndexOfAny(['-', '+']);
        return suffix >= 0 ? normalized[..suffix] : normalized;
    }

    private static string NormalizeSha256(string value)
    {
        string normalized = value.Trim().ToLowerInvariant();
        if (!Regex.IsMatch(normalized, "^[0-9a-f]{64}$"))
            throw new InvalidDataException("SHA-256 校验值无效");
        return normalized;
    }

    private static Uri ResolvePackageUri(string metadataUrl, string packageUrl)
    {
        if (Uri.TryCreate(packageUrl, UriKind.Absolute, out Uri? absolute))
        {
            if (absolute.Scheme is not ("http" or "https"))
                throw new InvalidDataException("更新包 URL 协议无效");
            return absolute;
        }

        return new Uri(new Uri(metadataUrl, UriKind.Absolute), packageUrl);
    }

    private static string GetArchitectureName()
    {
        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException("当前处理器架构不支持自动更新"),
        };
    }

    private static string SafePathSegment(string value)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(value) ? "unknown" : value;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        })
        {
            Timeout = TimeSpan.FromHours(2),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NahidaTool-Updater/1.0");
        return client;
    }

    private void SetStatus(string status)
    {
        LogService.Info(status);
        StatusChanged?.Invoke(status);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch
        {
        }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
                Directory.Delete(path);
        }
        catch
        {
        }
    }
}

internal sealed class ServerAppUpdateResponse
{
    [JsonPropertyName("retcode")]
    public int? RetCode { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("update")]
    public ServerAppUpdateMetadata? Update { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("releaseName")]
    public string? ReleaseName { get; set; }

    [JsonPropertyName("releaseNotes")]
    public string? ReleaseNotes { get; set; }

    [JsonPropertyName("publishedAt")]
    public DateTimeOffset? PublishedAt { get; set; }

    [JsonPropertyName("packageUrl")]
    public string? PackageUrl { get; set; }

    [JsonPropertyName("packageSize")]
    public long PackageSize { get; set; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

    public ServerAppUpdateMetadata? AsMetadata()
    {
        if (string.IsNullOrWhiteSpace(Version))
            return null;
        return new ServerAppUpdateMetadata
        {
            Version = Version,
            ReleaseName = ReleaseName,
            ReleaseNotes = ReleaseNotes,
            PublishedAt = PublishedAt,
            PackageUrl = PackageUrl,
            PackageSize = PackageSize,
            Sha256 = Sha256,
        };
    }
}

internal sealed class ServerAppUpdateMetadata
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("releaseName")]
    public string? ReleaseName { get; set; }

    [JsonPropertyName("releaseNotes")]
    public string? ReleaseNotes { get; set; }

    [JsonPropertyName("publishedAt")]
    public DateTimeOffset? PublishedAt { get; set; }

    [JsonPropertyName("packageUrl")]
    public string? PackageUrl { get; set; }

    [JsonPropertyName("packageSize")]
    public long PackageSize { get; set; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }
}
