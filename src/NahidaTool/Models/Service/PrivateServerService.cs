using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NahidaTool.Models;
using NahidaTool.Models.Config;

namespace NahidaTool.Models.Service;

/// <summary>
/// 私服信息与程序更新地址服务；剧情服使用 /api/meta，其余使用 /status/server。
/// </summary>
public static class PrivateServerService
{
    public const int UpdateServerPort = 1027;

    /// <summary>
    /// 默认剧情服游戏地址；版本查询使用独立的元数据接口。
    /// </summary>
    private const string DefaultServerBaseUrl = KunServerConfig.StoryAddress;

    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(5),
    };

    /// <summary>
    /// 根据当前代理模式生成状态接口地址；普通模式地址为空时回退到默认地址
    /// </summary>
    public static string GetServerBaseUrl(AppSettings settings)
    {
        string address = KunServerConfig.GetEffectiveProxyAddress(settings)?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(address))
            return DefaultServerBaseUrl;

        if (!address.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !address.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            address = "http://" + address;
        }

        return address.TrimEnd('/');
    }

    public static IReadOnlyList<string> GetCandidateBaseUrls(AppSettings settings)
    {
        // Never use a different server's version to validate the selected server.
        return new[] { GetServerBaseUrl(settings) };
    }

    /// <summary>
    /// Program-update traffic is independent of the story game's HTTPS endpoint.
    /// Custom/command server hosts may provide the same dedicated update service.
    /// </summary>
    public static IReadOnlyList<string> GetCandidateUpdateServerUrls(AppSettings settings)
    {
        var result = new List<string>();
        foreach (string baseUrl in GetCandidateBaseUrls(settings))
        {
            if (IsStoryServer(baseUrl))
                continue;
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri) ||
                string.IsNullOrWhiteSpace(uri.Host))
            {
                continue;
            }

            var updateUri = new UriBuilder(Uri.UriSchemeHttp, uri.Host, UpdateServerPort).Uri;
            string updateBaseUrl = updateUri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
            if (!result.Contains(updateBaseUrl, StringComparer.OrdinalIgnoreCase))
                result.Add(updateBaseUrl);
        }

        // The story's metadata API does not move the dedicated update service.
        if (!result.Contains(KunServerConfig.UpdateAddress, StringComparer.OrdinalIgnoreCase))
            result.Add(KunServerConfig.UpdateAddress);

        return result;
    }

    /// <summary>
    /// 从所选服务器读取游戏版本；剧情服只取 server.gameversion，
    /// 指令服保持原有 status.version 契约，不跨服务器回退。
    /// </summary>
    public static async Task<string?> GetServerVersionAsync(AppSettings settings, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        foreach (string baseUrl in GetCandidateBaseUrls(settings))
        {
            bool metadataEndpoint = IsStoryServer(baseUrl);
            string url = metadataEndpoint ? KunServerConfig.StoryMetadataAddress
                : baseUrl.TrimEnd('/') + "/status/server";
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                using var response = await SharedHttpClient.GetAsync(url, cts.Token);
                response.EnsureSuccessStatusCode();
                string json = await response.Content.ReadAsStringAsync(cts.Token);

                string? version = ParseServerVersionResponse(json, metadataEndpoint);
                if (version != null)
                {
                    LogService.Debug($"私服游戏版本获取成功: {new Uri(url).IdnHost}, 版本={version}");
                    return version;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
            {
                LogService.Debug($"获取私服状态失败: {new Uri(url).IdnHost}, {ex.GetType().Name}");
            }
        }

        return null;
    }

    private static bool IsStoryServer(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        uri.IsDefaultPort && uri.IdnHost.Equals(new Uri(KunServerConfig.StoryAddress).IdnHost, StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.UserInfo);

    internal static string? ParseServerVersionResponse(string json, bool metadataEndpoint)
    {
        string? value;
        if (metadataEndpoint)
        {
            var metadata = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.KunServerMetadataResponse);
            if (metadata?.Ok != true)
                return null;
            value = metadata.Server?.GameVersion;
        }
        else
        {
            var status = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.ServerStatusResponse);
            if (status?.RetCode != 0)
                return null;
            value = status.Status?.Version;
        }
        return NormalizeGameVersion(value);
    }

    private static string? NormalizeGameVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256)
            return null;
        ReadOnlySpan<char> text = value.AsSpan().TrimStart();
        if (text[0] is 'v' or 'V')
            text = text[1..];
        int length = 0;
        while (length < text.Length && (text[length] is >= '0' and <= '9' || text[length] == '.'))
            length++;
        if (!Version.TryParse(text[..length], out var version) || version.Major <= 0 || version.Revision >= 0)
            return null;
        // Two-component release versions (e.g. 7.0) mean 7.0.0. Only the numeric
        // prefix participates in comparison/recommendation; annotations stay out.
        return new Version(version.Major, version.Minor, Math.Max(version.Build, 0)).ToString(3);
    }
}
