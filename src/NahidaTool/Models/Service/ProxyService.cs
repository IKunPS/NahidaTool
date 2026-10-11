using System;
using System.Net;
using System.Threading.Tasks;
using NahidaTool.Eavesdrop;
using NahidaTool.Eavesdrop.Event_Args;
using NahidaTool.Models.Config;

namespace NahidaTool.Models.Service;

public static class ProxyService
{
    private static string _dispatch = string.Empty;

    private static readonly string[] s_redirectDomains =
    {
        "hoyoverse.com",
        "mihoyo.com",
        "yuanshen.com"
    };

    public static bool IsRunning => Eavesdropper.IsRunning;

    public static string CurrentTarget => _dispatch;

    static ProxyService()
    {
        Eavesdropper.Certifier = new Certifier("NahidaTool", "NahidaTool Root Certificate Authority");
    }

    public static async Task<bool> StartAsync()
    {
        try
        {
            if (Eavesdropper.IsRunning)
                Stop();

            var settings = AppSettings.Load();

            var address = KunServerConfig.GetEffectiveProxyAddress(settings);
            if (string.IsNullOrEmpty(address))
            {
                LogService.Error("代理地址不能为空");
                return false;
            }

            if (!address.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !address.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                address = "https://" + address;
            }

            // 校验 URL 格式
            _ = new Uri(address);
            _dispatch = address.TrimEnd('/');

            Eavesdropper.Overrides.Clear();
            Eavesdropper.Overrides.AddRange(new[]
            {
                "localhost", "127.*", "10.*", "172.16.*", "172.17.*", "172.18.*", "172.19.*",
                "172.20.*", "172.21.*", "172.22.*", "172.23.*", "172.24.*", "172.25.*",
                "172.26.*", "172.27.*", "172.28.*", "172.29.*", "172.30.*", "172.31.*", "192.168.*"
            });

            // 设置需要 MITM 拦截的域名（仅游戏相关域名），其余 HTTPS 流量做原始 TCP 透传
            Eavesdropper.InterceptHosts.Clear();
            foreach (var domain in s_redirectDomains)
                Eavesdropper.InterceptHosts.Add(domain);

            // Let official launcher APIs use the user's normal network route,
            // including VPN/TUN. Only game traffic is rewritten to the private server.
            Eavesdropper.PassthroughHosts.Clear();
            foreach (string url in new[]
            {
                ServerConfig.CN.BranchApiUrl, ServerConfig.CN.SophonApiUrl, ServerConfig.CN.SophonPatchApiUrl,
                ServerConfig.OS.BranchApiUrl, ServerConfig.OS.SophonApiUrl, ServerConfig.OS.SophonPatchApiUrl
            })
                Eavesdropper.PassthroughHosts.Add(new Uri(url).IdnHost);

            if (!Eavesdropper.Certifier.CreateTrustedRootCertificate())
                LogService.Info("根证书安装失败或已取消，HTTPS 流量可能无法拦截");

            Eavesdropper.RequestInterceptedAsync += OnRequestInterceptedAsync;
            Eavesdropper.Initiate(GameProxyEndpoint.Port);

            LogService.Info($"代理已启动 :{GameProxyEndpoint.Port} → {_dispatch}");
            return true;
        }
        catch (Exception ex)
        {
            LogService.Error("启动代理失败", ex);
            return false;
        }
    }

    public static bool DestroyCertificate()
    {
        return Eavesdropper.Certifier.DestroyTrustedRootCertificate();
    }

    public static void Stop()
    {
        try
        {
            Eavesdropper.RequestInterceptedAsync -= OnRequestInterceptedAsync;
            Eavesdropper.Terminate();
            LogService.Info("代理已停止");
        }
        catch (Exception ex)
        {
            LogService.Error("停止代理失败", ex);
        }
    }

    private static Task OnRequestInterceptedAsync(object sender, RequestInterceptedEventArgs e)
    {
        Uri original = e.Request.RequestUri;
        if (!ProxyHostPolicy.ShouldIntercept(original.IdnHost, Eavesdropper.InterceptHosts,
                Eavesdropper.PassthroughHosts))
            return Task.CompletedTask;
        var target = new Uri(_dispatch + original.PathAndQuery);
        e.Request = RedirectRequest((HttpWebRequest)e.Request, target);
        return Task.CompletedTask;
    }

    private static HttpWebRequest RedirectRequest(HttpWebRequest request, Uri newUri)
    {
        var newRequest = WebRequest.CreateHttp(newUri);
        newRequest.ProtocolVersion = request.ProtocolVersion;
        newRequest.CookieContainer = request.CookieContainer;
        newRequest.AllowAutoRedirect = request.AllowAutoRedirect;
        newRequest.KeepAlive = request.KeepAlive;
        newRequest.Method = request.Method;
        newRequest.Proxy = request.Proxy;

        foreach (var name in request.Headers.AllKeys)
        {
            switch (name.ToLower())
            {
                // HTTPS/SNI and virtual-host routing must use the destination's
                // authority; carrying the game Host to game.kunps.cn breaks TLS.
                case "host": break;
                case "accept":            newRequest.Accept          = request.Accept;          break;
                case "referer":           newRequest.Referer         = request.Referer;         break;
                case "user-agent":        newRequest.UserAgent       = request.UserAgent;       break;
                case "content-type":      newRequest.ContentType     = request.ContentType;     break;
                case "content-length":    newRequest.ContentLength   = request.ContentLength;   break;
                case "if-modified-since": newRequest.IfModifiedSince = request.IfModifiedSince; break;
                case "date":              newRequest.Date            = request.Date;            break;
                default:                  newRequest.Headers[name]   = request.Headers[name];   break;
            }
        }

        return newRequest;
    }
}
