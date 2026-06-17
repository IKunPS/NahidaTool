using NahidaTool.Models.Enum;

namespace NahidaTool.Models.Config;

/// <summary>
/// 服务器配置类，存储国服和国际服的API配置信息
/// </summary>
public static class ServerConfig
{
    /// <summary>
    /// 原神国服配置 (CNREL)
    /// </summary>
    public static class CN
    {
        /// <summary>
        /// 获取游戏分支信息的API (用于获取package_id和password)
        /// </summary>
        public const string BranchApiUrl = "https://hyp-api.mihoyo.com/hyp/hyp-connect/api/getGameBranches";

        /// <summary>
        /// Sophon下载API
        /// </summary>
        public const string SophonApiUrl = "https://api-takumi.mihoyo.com/downloader/sophon_chunk/api/getBuild";

        /// <summary>
        /// 启动器ID
        /// </summary>
        public const string LauncherId = "jGHBHlcOq1";

        /// <summary>
        /// 平台应用ID
        /// </summary>
        public const string PlatApp = "ddxf5qt290cg";

        /// <summary>
        /// 游戏ID (hk4e - 原神)
        /// </summary>
        public const string GameId = "1Z8W5NHUQb";
    }

    /// <summary>
    /// 原神国际服配置 (OSREL)
    /// </summary>
    public static class OS
    {
        /// <summary>
        /// 获取游戏分支信息的API (用于获取package_id和password)
        /// </summary>
        public const string BranchApiUrl = "https://sg-hyp-api.hoyoverse.com/hyp/hyp-connect/api/getGameBranches";

        /// <summary>
        /// Sophon下载API
        /// </summary>
        public const string SophonApiUrl = "https://sg-public-api.hoyoverse.com/downloader/sophon_chunk/api/getBuild";

        /// <summary>
        /// 启动器ID
        /// </summary>
        public const string LauncherId = "VYTpXlbWo8";

        /// <summary>
        /// 平台应用ID
        /// </summary>
        public const string PlatApp = "ddxf6vlr1reo";

        /// <summary>
        /// 游戏ID (hk4e - 原神)
        /// </summary>
        public const string GameId = "gopR6Cufr3";
    }

    /// <summary>
    /// 语音包匹配字段映射
    /// </summary>
    public static class VoicePackages
    {
        public const string Game = "game";
        public const string Chinese = "zh-cn";
        public const string English = "en-us";
        public const string Japanese = "ja-jp";
        public const string Korean = "ko-kr";

        /// <summary>
        /// 根据语音语言枚举获取对应的匹配字段
        /// </summary>
        public static string GetMatchingField(VoiceLanguageType language)
        {
            return language switch
            {
                VoiceLanguageType.Chinese => Chinese,
                VoiceLanguageType.English => English,
                VoiceLanguageType.Japanese => Japanese,
                VoiceLanguageType.Korean => Korean,
                _ => Game
            };
        }

        /// <summary>
        /// 获取语音包的显示名称
        /// </summary>
        public static string GetDisplayName(VoiceLanguageType language)
        {
            return language switch
            {
                VoiceLanguageType.Chinese => Lang.DownloadSettingPage_VoiceChinese,
                VoiceLanguageType.English => Lang.DownloadSettingPage_VoiceEnglish,
                VoiceLanguageType.Japanese => Lang.DownloadSettingPage_VoiceJapanese,
                VoiceLanguageType.Korean => Lang.DownloadSettingPage_VoiceKorean,
                _ => Lang.DownloadSettingPage_VoiceNone
            };
        }
    }

    /// <summary>
    /// 根据服务器区域获取分支API URL
    /// </summary>
    public static string GetBranchApiUrl(ServerRegionType region)
    {
        return region == ServerRegionType.CN ? CN.BranchApiUrl : OS.BranchApiUrl;
    }

    /// <summary>
    /// 根据服务器区域获取Sophon API URL
    /// </summary>
    public static string GetSophonApiUrl(ServerRegionType region)
    {
        return region == ServerRegionType.CN ? CN.SophonApiUrl : OS.SophonApiUrl;
    }

    /// <summary>
    /// 根据服务器区域获取启动器ID
    /// </summary>
    public static string GetLauncherId(ServerRegionType region)
    {
        return region == ServerRegionType.CN ? CN.LauncherId : OS.LauncherId;
    }

    /// <summary>
    /// 根据服务器区域获取平台应用ID
    /// </summary>
    public static string GetPlatApp(ServerRegionType region)
    {
        return region == ServerRegionType.CN ? CN.PlatApp : OS.PlatApp;
    }

    /// <summary>
    /// 根据服务器区域获取游戏ID
    /// </summary>
    public static string GetGameId(ServerRegionType region)
    {
        return region == ServerRegionType.CN ? CN.GameId : OS.GameId;
    }

    /// <summary>
    /// 获取服务器区域的显示名称
    /// </summary>
    public static string GetRegionDisplayName(ServerRegionType region)
    {
        return region == ServerRegionType.CN ? Lang.DownloadSettingPage_CN : Lang.DownloadSettingPage_OS;
    }
}

