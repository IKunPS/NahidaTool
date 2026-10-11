using System;
using NahidaTool.Models.Enum;

namespace NahidaTool.Models.Config;

public static class KunServerConfig
{
    // Keep normalized base URLs: the game proxy compares these to its active
    // destination and appends the original request path separately.
    public const string StoryAddress = "https://game.kunps.cn";
    public const string StoryMetadataAddress = StoryAddress + "/api/meta";
    public const string CommandAddress = "http://210.16.175.19:2712";
    public const string UpdateAddress = "http://210.16.175.19:1027";

    public static bool IsEnabled(AppSettings settings) => settings.EnableProxy && settings.EnableKun;

    public static string GetAddress(KunServerType server) => server switch
    {
        KunServerType.Story => StoryAddress,
        KunServerType.Command => CommandAddress,
        _ => throw new ArgumentOutOfRangeException(nameof(server))
    };

    public static string GetEffectiveProxyAddress(AppSettings settings) =>
        IsEnabled(settings) ? GetAddress(settings.KunServer) : settings.ProxyAddress;
}
