using System;
using NahidaTool.Models.Enum;

namespace NahidaTool.Models.Config;

public static class KunServerConfig
{
    // The existing default private server is the story endpoint; Kun's command
    // endpoint uses the same host with a different port.
    private const string Host = "210.16.175.19";

    public static bool IsEnabled(AppSettings settings) => settings.EnableProxy && settings.EnableKun;

    public static string GetAddress(KunServerType server) => server switch
    {
        KunServerType.Story => $"http://{Host}:1145",
        KunServerType.Command => $"http://{Host}:2712",
        _ => throw new ArgumentOutOfRangeException(nameof(server))
    };

    public static string GetEffectiveProxyAddress(AppSettings settings) =>
        IsEnabled(settings) ? GetAddress(settings.KunServer) : settings.ProxyAddress;
}
