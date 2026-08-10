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
/// 私服状态服务：从私服 /status/server 接口获取服务器当前版本，用于推荐下载
/// </summary>
public static class PrivateServerService
{
    public const int UpdateServerPort = 1027;

    /// <summary>
    /// 默认私服状态地址（未配置代理地址时使用）
    /// </summary>
    private const string DefaultServerBaseUrl = "http://210.16.175.19:1145";

    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(5),
    };

    /// <summary>
    /// 根据配置的代理地址生成状态接口地址；代理地址为空时回退到默认地址
    /// </summary>
    public static string GetServerBaseUrl(AppSettings settings)
    {
        string address = settings.ProxyAddress?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(address))
            return DefaultServerBaseUrl;

        if (!address.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !address.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            address = "http://" + address;
        }

        return address.TrimEnd('/');
    }

    public static string GetStatusUrl(AppSettings settings)
    {
        return GetServerBaseUrl(settings) + "/status/server";
    }

    public static IReadOnlyList<string> GetCandidateBaseUrls(AppSettings settings)
    {
        string primary = GetServerBaseUrl(settings);
        if (string.Equals(primary, DefaultServerBaseUrl, StringComparison.OrdinalIgnoreCase))
            return new[] { primary };
        return new[] { primary, DefaultServerBaseUrl };
    }

    /// <summary>
    /// Uses the hosts from the configured/recommended proxy addresses, but sends
    /// program-update traffic to the dedicated HTTP service on port 1027.
    /// </summary>
    public static IReadOnlyList<string> GetCandidateUpdateServerUrls(AppSettings settings)
    {
        var result = new List<string>();
        foreach (string baseUrl in GetCandidateBaseUrls(settings))
        {
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

        return result;
    }

    /// <summary>
    /// 获取私服当前版本；优先请求配置的代理地址，失败时回退到默认地址。获取失败返回 null。
    /// </summary>
    public static async Task<string?> GetServerVersionAsync(AppSettings settings, CancellationToken ct = default)
    {
        var candidates = GetCandidateBaseUrls(settings)
            .Select(baseUrl => baseUrl.TrimEnd('/') + "/status/server");

        foreach (string url in candidates)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                using var response = await SharedHttpClient.GetAsync(url, cts.Token);
                response.EnsureSuccessStatusCode();
                string json = await response.Content.ReadAsStringAsync(cts.Token);

                var result = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.ServerStatusResponse);
                string? version = result?.Status?.Version;
                if (!string.IsNullOrWhiteSpace(version))
                {
                    LogService.Debug(
                        $"私服状态获取成功: 版本={version}, 在线={result?.Status?.PlayerCount}/{result?.Status?.MaxPlayer} ({url})");
                    return version.Trim();
                }
            }
            catch (Exception ex)
            {
                LogService.Debug($"获取私服状态失败 ({url}): {ex.Message}");
            }
        }

        return null;
    }
}
