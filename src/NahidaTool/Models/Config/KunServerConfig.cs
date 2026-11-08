namespace NahidaTool.Models.Config;

public static class KunServerConfig
{
    // Keep normalized base URLs: the game proxy compares these to its active
    // destination and appends the original request path separately.
    public const string BaseAddress = "https://game.kunps.cn";
    public const string MetadataAddress = BaseAddress + "/api/meta";
    public const string UpdateAddress = "http://210.16.175.19:1027";

    public static bool IsEnabled(AppSettings settings) => settings.EnableProxy && settings.EnableKun;

    public static string GetEffectiveProxyAddress(AppSettings settings) =>
        IsEnabled(settings) ? BaseAddress : settings.ProxyAddress;
}
