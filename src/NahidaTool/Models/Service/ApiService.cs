using System;
using System.IO;
using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NahidaTool.Models.Config;
using NahidaTool.Models.Enum;

namespace NahidaTool.Models.Service;

public class ApiService
{
    // 共享 SocketsHttpHandler — 全局连接池，避免 socket 泄漏
    private static readonly SocketsHttpHandler SharedHandler = new()
    {
        MaxConnectionsPerServer = 6,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        EnableMultipleHttp2Connections = true,
        AllowAutoRedirect = true,
        SslOptions = new SslClientAuthenticationOptions
        {
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        },
    };

    private static readonly HttpClient SharedHttpClient;

    static ApiService()
    {
        SharedHttpClient = new HttpClient(SharedHandler)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        SharedHttpClient.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
    }

    private readonly HttpClient _httpClient = SharedHttpClient;
    private ServerRegionType _currentRegion = ServerRegionType.CN;

    // 缓存的分支信息
    private string? _cachedPackageId;
    private string? _cachedPassword;
    private ServerRegionType _cachedRegion;

    /// <summary>
    /// 设置当前服务器区域
    /// </summary>
    public void SetRegion(ServerRegionType region)
    {
        if (_currentRegion != region)
        {
            _currentRegion = region;
            // 清除缓存，强制重新获取分支信息
            _cachedPackageId = null;
            _cachedPassword = null;
        }
    }

    /// <summary>
    /// 获取当前服务器区域
    /// </summary>
    public ServerRegionType GetRegion() => _currentRegion;

    /// <summary>
    /// 创建复用连接池的 HttpClient（用于长超时下载场景）
    /// </summary>
    public HttpClient CreateDownloadClient(TimeSpan? timeout = null)
    {
        return new HttpClient(SharedHandler, disposeHandler: false)
        {
            Timeout = timeout ?? TimeSpan.FromMinutes(30),
        };
    }

    public async Task<GameBranch?> GetGameBranchAsync(CancellationToken ct = default)
    {
        string branchApiUrl = ServerConfig.GetBranchApiUrl(_currentRegion);
        string launcherId = ServerConfig.GetLauncherId(_currentRegion);
        string gameId = ServerConfig.GetGameId(_currentRegion);
        string url = $"{branchApiUrl}?game_ids[]={gameId}&launcher_id={launcherId}";

        using HttpResponseMessage response = await _httpClient.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        var branchResponse = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.BranchResponse);

        return branchResponse?.Data?.GameBranches?.Count > 0
            ? branchResponse.Data.GameBranches[0]
            : null;
    }

    /// <summary>
    /// 获取游戏分支信息 (package_id 和 password)
    /// </summary>
    private async Task<(string packageId, string password)> GetBranchInfoAsync(CancellationToken ct = default)
    {
        // 如果缓存有效，直接返回
        if (_cachedPackageId != null && _cachedPassword != null && _cachedRegion == _currentRegion)
        {
            LogService.Debug($"使用缓存的分支信息: packageId={_cachedPackageId}");
            return (_cachedPackageId, _cachedPassword);
        }

        string branchApiUrl = ServerConfig.GetBranchApiUrl(_currentRegion);
        string launcherId = ServerConfig.GetLauncherId(_currentRegion);
        string gameId = ServerConfig.GetGameId(_currentRegion);

        // 直接拼接URL，避免HttpUtility对[]进行编码
        string url = $"{branchApiUrl}?game_ids[]={gameId}&launcher_id={launcherId}";

        LogService.Debug($"获取分支信息: 区域={_currentRegion}, URL={url}");

        using HttpResponseMessage response = await _httpClient.GetAsync(url, ct);
        LogService.Debug($"分支API响应状态: {(int)response.StatusCode} {response.StatusCode}");
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync(ct);
        LogService.Debug($"分支API响应: {json.Substring(0, Math.Min(500, json.Length))}...");

        var branchResponse = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.BranchResponse);

        if (branchResponse?.Data?.GameBranches != null && branchResponse.Data.GameBranches.Count > 0)
        {
            var gameBranch = branchResponse.Data.GameBranches[0];
            if (gameBranch.Main != null)
            {
                _cachedPackageId = gameBranch.Main.PackageId;
                _cachedPassword = gameBranch.Main.Password;
                _cachedRegion = _currentRegion;
                LogService.Debug($"获取分支信息成功: packageId={_cachedPackageId}");
                return (_cachedPackageId ?? string.Empty, _cachedPassword ?? string.Empty);
            }
        }

        LogService.Error("分支响应中没有有效的游戏分支数据");
        throw new Exception("无法获取游戏分支信息");
    }

    /// <summary>
    /// 获取构建信息
    /// </summary>
    public async Task<BuildResponse> GetBuildInfoAsync(string? tag = null, CancellationToken ct = default)
    {
        string? requestedTag = string.IsNullOrWhiteSpace(tag) ? null : tag.Trim();
        LogService.Debug($"开始获取构建信息: 区域={_currentRegion}, 版本={requestedTag ?? "最新"}");

        // 正式分支仍然是首选，这也保留了按 tag 下载历史版本的能力。
        var (packageId, password) = await GetBranchInfoAsync(ct);
        BuildResponse mainResult = await RequestBuildInfoAsync(
            "main", packageId, password, requestedTag, ct);

        if (requestedTag == null || IsMatchingBuild(mainResult, requestedTag))
            return mainResult;

        // getBuild 对尚未正式发布的 tag 返回 not found。只有该 tag 确实是当前
        // pre_download 版本时才回退，避免无效或过旧版本误拿到最新预下载资源。
        GameBranch? gameBranch = await GetGameBranchAsync(ct);
        BranchInfo? preDownload = gameBranch?.PreDownload;
        if (preDownload == null ||
            !string.Equals(preDownload.Tag, requestedTag, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(preDownload.PackageId) ||
            string.IsNullOrWhiteSpace(preDownload.Password))
        {
            return mainResult;
        }

        LogService.Info($"版本 {requestedTag} 尚未在正式分支发布，尝试使用预下载分支资源");

        // Sophon 的预下载 getBuild 只提供当前预下载版本，携带 tag 会返回 not found。
        BuildResponse preDownloadResult = await RequestBuildInfoAsync(
            "predownload", preDownload.PackageId, preDownload.Password, null, ct);
        if (IsMatchingBuild(preDownloadResult, requestedTag))
        {
            LogService.Info($"已从预下载分支获取版本 {requestedTag} 的资源");
            return preDownloadResult;
        }

        LogService.Warn(
            $"预下载分支返回的版本与请求不符: 请求={requestedTag}, 返回={preDownloadResult.Data?.Tag ?? "空"}");
        return mainResult;
    }

    private async Task<BuildResponse> RequestBuildInfoAsync(
        string branch,
        string packageId,
        string password,
        string? tag,
        CancellationToken ct)
    {
        string sophonApiUrl = ServerConfig.GetSophonApiUrl(_currentRegion);
        string platApp = ServerConfig.GetPlatApp(_currentRegion);
        string url = $"{sophonApiUrl}?branch={Uri.EscapeDataString(branch)}" +
                     $"&package_id={Uri.EscapeDataString(packageId)}" +
                     $"&password={Uri.EscapeDataString(password)}" +
                     $"&plat_app={Uri.EscapeDataString(platApp)}";

        if (!string.IsNullOrWhiteSpace(tag))
            url += $"&tag={Uri.EscapeDataString(tag)}";

        // package password 不应写入日志。
        LogService.Debug($"请求构建信息: 区域={_currentRegion}, 分支={branch}, 版本={tag ?? "最新"}");

        using HttpResponseMessage response = await _httpClient.GetAsync(url, ct);
        LogService.Debug($"构建API响应状态: {(int)response.StatusCode} {response.StatusCode}");
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync(ct);
        LogService.Debug($"构建API响应长度: {json.Length} 字符");

        var result = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.BuildResponse) ??
                     new BuildResponse();
        LogService.Info(
            $"构建信息解析完成: 分支={branch}, RetCode={result.RetCode}, Tag={result.Data?.Tag}, Manifests数量={result.Data?.Manifests?.Count ?? 0}");

        return result;
    }

    private static bool IsMatchingBuild(BuildResponse response, string requestedTag)
    {
        return response.RetCode == 0 &&
               string.Equals(response.Data?.Tag, requestedTag, StringComparison.OrdinalIgnoreCase) &&
               response.Data?.Manifests is { Count: > 0 };
    }

    public async Task<PatchBuildResponse> GetPatchBuildInfoAsync(CancellationToken ct = default)
    {
        var preDownload = (await GetGameBranchAsync(ct))?.PreDownload;
        if (preDownload == null || string.IsNullOrWhiteSpace(preDownload.PackageId) ||
            string.IsNullOrWhiteSpace(preDownload.Password))
            throw new InvalidOperationException("当前没有可用的预下载分支");

        var requestBody = new
        {
            branch = "predownload",
            package_id = preDownload.PackageId,
            password = preDownload.Password,
            plat_app = ServerConfig.GetPlatApp(_currentRegion),
        };
        string jsonBody = JsonSerializer.Serialize(requestBody);
        using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await _httpClient.PostAsync(
            ServerConfig.GetSophonPatchApiUrl(_currentRegion), content, ct);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync(ct);
        var result = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.PatchBuildResponse) ??
                     new PatchBuildResponse();
        if (result.RetCode != 0 || result.Data == null)
            throw new InvalidOperationException($"获取 LDiff 构建失败: {result.Message ?? result.RetCode.ToString()}");

        return result;
    }

    public async Task<byte[]> DownloadManifestAsync(string urlPrefix, string manifestId)
    {
        string url = $"{urlPrefix}/{manifestId}";

        using HttpClient httpClient = CreateDownloadClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using HttpResponseMessage response = await httpClient.GetAsync(url, cts.Token);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsByteArrayAsync(cts.Token);
    }

    /// <summary>
    /// 流式下载Chunk，直接写入文件，避免在内存中缓冲整个响应
    /// </summary>
    public async Task DownloadChunkToFileAsync(string urlPrefix, string chunkId, string filePath)
    {
        string url = $"{urlPrefix}/{chunkId}";

        using HttpClient httpClient = CreateDownloadClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using HttpResponseMessage response =
            await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.EnsureSuccessStatusCode();

        await using var contentStream = await response.Content.ReadAsStreamAsync();
        await using var fileStream =
            new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        await contentStream.CopyToAsync(fileStream);
    }
}
