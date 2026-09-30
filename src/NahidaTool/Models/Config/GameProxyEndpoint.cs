using System;

namespace NahidaTool.Models.Config;

internal static class GameProxyEndpoint
{
    internal const int Port = 6898;

    internal static bool Matches(Uri? proxy) => proxy is { IsAbsoluteUri: true } &&
        proxy.IsLoopback && proxy.Port == Port &&
        (proxy.Scheme == Uri.UriSchemeHttp || proxy.Scheme == Uri.UriSchemeHttps);
}
