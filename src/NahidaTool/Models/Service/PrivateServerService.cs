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
/// 私服信息与程序更新地址服务；Kun 统一入口使用 /api/meta，自定义地址使用 /status/server。
/// </summary>
public static class PrivateServerService
{
    public const int UpdateServerPort = 1027;

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
            return KunServerConfig.BaseAddress;

        if (!address.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !address.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            address = "http://" + address;
        }

        return address.TrimEnd('/');
    }

    /// <summary>
    /// 程序更新服务独立于 Kun 游戏入口；自定义服务器仍可提供同端口的更新服务。
    /// </summary>
    public static IReadOnlyList<string> GetCandidateUpdateServerUrls(AppSettings settings)
    {
        var result = new List<string>();
        string baseUrl = GetServerBaseUrl(settings);
        if (!IsKunServer(baseUrl) && Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri) &&
            !string.IsNullOrWhiteSpace(uri.Host))
        {
            var updateUri = new UriBuilder(Uri.UriSchemeHttp, uri.Host, UpdateServerPort).Uri;
            string updateBaseUrl = updateUri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
            result.Add(updateBaseUrl);
        }

        // 游戏入口合并不改变独立的程序更新服务。
        if (!result.Contains(KunServerConfig.UpdateAddress, StringComparer.OrdinalIgnoreCase))
            result.Add(KunServerConfig.UpdateAddress);

        return result;
    }

    /// <summary>
    /// 读取当前入口支持的游戏版本，不跨服务器回退。
    /// Kun 的 gameversion 支持斜杠分隔的列表；自定义地址保留 status.version 契约。
    /// </summary>
    public static async Task<IReadOnlyList<string>> GetServerVersionsAsync(AppSettings settings, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        string baseUrl = GetServerBaseUrl(settings);
        bool metadataEndpoint = IsKunServer(baseUrl);
        string url = metadataEndpoint ? KunServerConfig.MetadataAddress
            : baseUrl.TrimEnd('/') + "/status/server";
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await SharedHttpClient.GetAsync(url, cts.Token);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(cts.Token);

            var versions = ParseServerVersionsResponse(json, metadataEndpoint);
            if (versions.Count > 0)
                LogService.Debug($"私服游戏版本获取成功: {new Uri(url).IdnHost}, 版本={string.Join("/", versions)}");
            return versions;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            LogService.Debug($"获取私服状态失败: {new Uri(url).IdnHost}, {ex.GetType().Name}");
        }

        return Array.Empty<string>();
    }

    private static bool IsKunServer(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        uri.IsDefaultPort && uri.IdnHost.Equals(new Uri(KunServerConfig.BaseAddress).IdnHost, StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.UserInfo);

    internal static IReadOnlyList<string> ParseServerVersionsResponse(string json, bool metadataEndpoint)
    {
        string? value;
        if (metadataEndpoint)
        {
            var metadata = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.KunServerMetadataResponse);
            if (metadata?.Ok != true)
                return Array.Empty<string>();
            value = metadata.Server?.GameVersion;
        }
        else
        {
            var status = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.ServerStatusResponse);
            if (status?.RetCode != 0)
                return Array.Empty<string>();
            value = status.Status?.Version;
        }
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256)
            return Array.Empty<string>();

        var versions = new List<string>();
        // 只有 Kun 元数据定义了多版本列表；自定义状态接口的附注不扩展契约。
        foreach (string candidate in metadataEndpoint ? value.Split('/') : new[] { value })
        {
            string? version = NormalizeGameVersion(candidate);
            if (version == null)
                return Array.Empty<string>();
            if (!versions.Contains(version, StringComparer.OrdinalIgnoreCase))
                versions.Add(version);
        }
        return versions.AsReadOnly();
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
