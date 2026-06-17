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
    private const int LocalPort = 6898;
    private static System.Net.Security.RemoteCertificateValidationCallback? _previousCertificateValidationCallback;

    private static readonly string[] s_redirectDomains =
    {
        "hoyoverse.com",
        "mihoyo.com",
        "yuanshen.com"
    };

    public static bool IsRunning => Eavesdropper.IsRunning;

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

            var address = settings.ProxyAddress;
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

            if (!Eavesdropper.Certifier.CreateTrustedRootCertificate())
                LogService.Info("根证书安装失败或已取消，HTTPS 流量可能无法拦截");

            _previousCertificateValidationCallback ??= ServicePointManager.ServerCertificateValidationCallback;
            ServicePointManager.ServerCertificateValidationCallback = (_, _, _, _) => true;

            Eavesdropper.RequestInterceptedAsync += OnRequestInterceptedAsync;
            Eavesdropper.Initiate(LocalPort);

            LogService.Info($"代理已启动 :{LocalPort} → {_dispatch}");
            return true;
        }
        catch (Exception ex)
        {
            ServicePointManager.ServerCertificateValidationCallback = _previousCertificateValidationCallback;
            _previousCertificateValidationCallback = null;
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
            ServicePointManager.ServerCertificateValidationCallback = _previousCertificateValidationCallback;
            _previousCertificateValidationCallback = null;
            LogService.Info("代理已停止");
        }
        catch (Exception ex)
        {
            LogService.Error("停止代理失败", ex);
        }
    }

    private static Task OnRequestInterceptedAsync(object sender, RequestInterceptedEventArgs e)
    {
        var url = e.Request.RequestUri.OriginalString;
        foreach (var domain in s_redirectDomains)
        {
            var i = url.IndexOf(domain, StringComparison.OrdinalIgnoreCase);
            if (i == -1) continue;

            var p = url.IndexOf('/', i + domain.Length);
            var target = p >= 0 ? _dispatch + url[p..] : _dispatch;

            e.Request = RedirectRequest((HttpWebRequest)e.Request, new Uri(target));
            return Task.CompletedTask;
        }

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
                case "host":              newRequest.Host            = request.Host;            break;
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
