using System.Globalization;
using System.Resources;

namespace NahidaTool.Models;

public static class Lang
{
    private static readonly ResourceManager _resourceManager = new("NahidaTool.Strings.Lang", typeof(Lang).Assembly);

    public static ResourceManager ResourceManager => _resourceManager;

    // MainWindow Navigation
    public static string HomePage_Title => _resourceManager.GetString("HomePage_Title") ?? "Home";
    public static string LauncherPage_Title => _resourceManager.GetString("LauncherPage_Title") ?? "Launcher";
    public static string ServerSwitchPage_Title => _resourceManager.GetString("ServerSwitchPage_Title") ?? "Server Switch";

    // SettingsPage
    public static string SettingsPage_Title => _resourceManager.GetString("SettingsPage_Title") ?? "Settings";
    public static string SettingsPage_About => _resourceManager.GetString("SettingsPage_About") ?? "About";
    public static string SettingsPage_General => _resourceManager.GetString("SettingsPage_General") ?? "General";
    public static string SettingsPage_Download => _resourceManager.GetString("SettingsPage_Download") ?? "Download";

    // GeneralSettingPage
    public static string GeneralSettingPage_Language => _resourceManager.GetString("GeneralSettingPage_Language") ?? "Language";
    public static string GeneralSettingPage_FollowSystem => _resourceManager.GetString("GeneralSettingPage_FollowSystem") ?? "Follow System";
    public static string GeneralSettingPage_CloseWindowBehavior => _resourceManager.GetString("GeneralSettingPage_CloseWindowBehavior") ?? "Close Window Behavior";
    public static string GeneralSettingPage_MinimizeToSystemTray => _resourceManager.GetString("GeneralSettingPage_MinimizeToSystemTray") ?? "Minimize to System Tray";
    public static string GeneralSettingPage_ExitCompletely => _resourceManager.GetString("GeneralSettingPage_ExitCompletely") ?? "Exit Completely";

    // AboutSettingPage
    public static string AboutSettingPage_AppName => _resourceManager.GetString("AboutSettingPage_AppName") ?? "NahidaTool";
    public static string AboutSettingPage_AppDescription => _resourceManager.GetString("AboutSettingPage_AppDescription") ?? "Download & Management Tool";
    public static string AboutSettingPage_CurrentVersion => _resourceManager.GetString("AboutSettingPage_CurrentVersion") ?? "Current Version:";
    public static string AboutSettingPage_Author => _resourceManager.GetString("AboutSettingPage_Author") ?? "Author:";
    public static string AboutSettingPage_RelatedLinks => _resourceManager.GetString("AboutSettingPage_RelatedLinks") ?? "Related Links";
    public static string AboutSettingPage_ProjectHomepage => _resourceManager.GetString("AboutSettingPage_ProjectHomepage") ?? "Project Homepage";

    // DownloadSettingPage
    public static string DownloadSettingPage_Title => _resourceManager.GetString("DownloadSettingPage_Title") ?? "Download Settings";
    public static string DownloadSettingPage_DownloadDirectory => _resourceManager.GetString("DownloadSettingPage_DownloadDirectory") ?? "Download Directory";
    public static string DownloadSettingPage_Browse => _resourceManager.GetString("DownloadSettingPage_Browse") ?? "Browse";
    public static string DownloadSettingPage_GameVersion => _resourceManager.GetString("DownloadSettingPage_GameVersion") ?? "Game Version";
    public static string DownloadSettingPage_GameVersionHint => _resourceManager.GetString("DownloadSettingPage_GameVersionHint") ?? "Enter game version to download";
    public static string DownloadSettingPage_ServerRegion => _resourceManager.GetString("DownloadSettingPage_ServerRegion") ?? "Server Region";
    public static string DownloadSettingPage_ServerRegionHint => _resourceManager.GetString("DownloadSettingPage_ServerRegionHint") ?? "Select download server region";
    public static string DownloadSettingPage_CN => _resourceManager.GetString("DownloadSettingPage_CN") ?? "CN";
    public static string DownloadSettingPage_OS => _resourceManager.GetString("DownloadSettingPage_OS") ?? "OS";
    public static string DownloadSettingPage_VoiceLanguage => _resourceManager.GetString("DownloadSettingPage_VoiceLanguage") ?? "Voice Language";
    public static string DownloadSettingPage_VoiceLanguageHint => _resourceManager.GetString("DownloadSettingPage_VoiceLanguageHint") ?? "Select voice pack to download";
    public static string DownloadSettingPage_VoiceNone => _resourceManager.GetString("DownloadSettingPage_VoiceNone") ?? "No Voice Pack";
    public static string DownloadSettingPage_VoiceChinese => _resourceManager.GetString("DownloadSettingPage_VoiceChinese") ?? "Chinese";
    public static string DownloadSettingPage_VoiceEnglish => _resourceManager.GetString("DownloadSettingPage_VoiceEnglish") ?? "English";
    public static string DownloadSettingPage_VoiceJapanese => _resourceManager.GetString("DownloadSettingPage_VoiceJapanese") ?? "Japanese";
    public static string DownloadSettingPage_VoiceKorean => _resourceManager.GetString("DownloadSettingPage_VoiceKorean") ?? "Korean";

    // HomePage
    public static string HomePage_DragDropHint => _resourceManager.GetString("HomePage_DragDropHint") ?? "Drag image or video file to set custom background";
    public static string HomePage_Proxy => _resourceManager.GetString("HomePage_Proxy") ?? "Proxy";
    public static string HomePage_ProxyNotStarted => _resourceManager.GetString("HomePage_ProxyNotStarted") ?? "Not Started";
    public static string HomePage_ProxyRunning => _resourceManager.GetString("HomePage_ProxyRunning") ?? "Running";
    public static string HomePage_ProxyAddress => _resourceManager.GetString("HomePage_ProxyAddress") ?? "Proxy Address";
    public static string HomePage_ProxyUrlError => _resourceManager.GetString("HomePage_ProxyUrlError") ?? "URL format error";
    public static string HomePage_StartGame => _resourceManager.GetString("HomePage_StartGame") ?? "Start Game";
    public static string HomePage_GameRunning => _resourceManager.GetString("HomePage_GameRunning") ?? "Game Running";
    public static string HomePage_LocateGame => _resourceManager.GetString("HomePage_LocateGame") ?? "Locate Game";
    public static string HomePage_Installed => _resourceManager.GetString("HomePage_Installed") ?? "Installed, ";
    public static string HomePage_LocateGameLink => _resourceManager.GetString("HomePage_LocateGameLink") ?? "Locate Game";
    public static string HomePage_SelectGameDir => _resourceManager.GetString("HomePage_SelectGameDir") ?? "Select game installation directory";
    public static string HomePage_InvalidPath => _resourceManager.GetString("HomePage_InvalidPath") ?? "Invalid Path";
    public static string HomePage_InvalidPathMessage => _resourceManager.GetString("HomePage_InvalidPathMessage") ?? "{0} not found in selected directory.";
    public static string HomePage_UrlFormatError => _resourceManager.GetString("HomePage_UrlFormatError") ?? "URL Format Error";
    public static string HomePage_ProxyUrlFormatContent => _resourceManager.GetString("HomePage_ProxyUrlFormatContent") ?? "Proxy address must start with https:// or http://.";
    public static string HomePage_ProxyUrlFormatStartMessage => _resourceManager.GetString("HomePage_ProxyUrlFormatStartMessage") ?? "Proxy address must start with https:// or http://.";
    public static string HomePage_OK => _resourceManager.GetString("HomePage_OK") ?? "OK";

    // DownloadPage
    public static string DownloadPage_ResourceInfo => _resourceManager.GetString("DownloadPage_ResourceInfo") ?? "Resource Info";
    public static string DownloadPage_Version => _resourceManager.GetString("DownloadPage_Version") ?? "Version";
    public static string DownloadPage_FileCount => _resourceManager.GetString("DownloadPage_FileCount") ?? "Files";
    public static string DownloadPage_ChunkCount => _resourceManager.GetString("DownloadPage_ChunkCount") ?? "Chunks";
    public static string DownloadPage_CompressedSize => _resourceManager.GetString("DownloadPage_CompressedSize") ?? "Compressed";
    public static string DownloadPage_UncompressedSize => _resourceManager.GetString("DownloadPage_UncompressedSize") ?? "Uncompressed";
    public static string DownloadPage_DownloadProgress => _resourceManager.GetString("DownloadPage_DownloadProgress") ?? "Download Progress";
    public static string DownloadPage_Progress => _resourceManager.GetString("DownloadPage_Progress") ?? "Progress:";
    public static string DownloadPage_Speed => _resourceManager.GetString("DownloadPage_Speed") ?? "Speed: -- MB/s";
    public static string DownloadPage_Remaining => _resourceManager.GetString("DownloadPage_Remaining") ?? "Remaining: --";
    public static string DownloadPage_StartDownload => _resourceManager.GetString("DownloadPage_StartDownload") ?? "Start Download";
    public static string DownloadPage_ContinueDownload => _resourceManager.GetString("DownloadPage_ContinueDownload") ?? "Continue Download";
    public static string DownloadPage_PauseDownload => _resourceManager.GetString("DownloadPage_PauseDownload") ?? "Pause Download";
    public static string DownloadPage_DownloadLog => _resourceManager.GetString("DownloadPage_DownloadLog") ?? "Download Log";
    public static string DownloadPage_GettingResourceInfo => _resourceManager.GetString("DownloadPage_GettingResourceInfo") ?? "Getting {0} resource info...";
    public static string DownloadPage_ResourceInfoSuccess => _resourceManager.GetString("DownloadPage_ResourceInfoSuccess") ?? "{0} resource info retrieved";
    public static string DownloadPage_UnknownVersion => _resourceManager.GetString("DownloadPage_UnknownVersion") ?? "Unknown";
    public static string DownloadPage_NoResourceInfo => _resourceManager.GetString("DownloadPage_NoResourceInfo") ?? "Failed to get resource info";
    public static string DownloadPage_GetResourceFailed => _resourceManager.GetString("DownloadPage_GetResourceFailed") ?? "Failed to get resource: {0}";
    public static string DownloadPage_Calculating => _resourceManager.GetString("DownloadPage_Calculating") ?? "Calculating...";
    public static string DownloadPage_VoiceSuffix => _resourceManager.GetString("DownloadPage_VoiceSuffix") ?? " Voice";
    public static string DownloadPage_SpeedFormat => _resourceManager.GetString("DownloadPage_SpeedFormat") ?? "Download: {0:F2} MB/s | Write: {1:F2} MB/s";
    public static string DownloadPage_RemainingFormat => _resourceManager.GetString("DownloadPage_RemainingFormat") ?? "Remaining: {0}";

    // ServerSwitchPage
    public static string ServerSwitchPage_Description => _resourceManager.GetString("ServerSwitchPage_Description") ?? "Switch game client between CN and OS server";
    public static string ServerSwitchPage_CurrentServer => _resourceManager.GetString("ServerSwitchPage_CurrentServer") ?? "Current Server";
    public static string ServerSwitchPage_Detecting => _resourceManager.GetString("ServerSwitchPage_Detecting") ?? "Detecting...";
    public static string ServerSwitchPage_ReDetect => _resourceManager.GetString("ServerSwitchPage_ReDetect") ?? "Re-detect";
    public static string ServerSwitchPage_SelectTargetServer => _resourceManager.GetString("ServerSwitchPage_SelectTargetServer") ?? "Select Target Server";
    public static string ServerSwitchPage_CN => _resourceManager.GetString("ServerSwitchPage_CN") ?? "CN";
    public static string ServerSwitchPage_CN_Short => _resourceManager.GetString("ServerSwitchPage_CN_Short") ?? "CN";
    public static string ServerSwitchPage_OS => _resourceManager.GetString("ServerSwitchPage_OS") ?? "OS";
    public static string ServerSwitchPage_OS_Short => _resourceManager.GetString("ServerSwitchPage_OS_Short") ?? "OS";
    public static string ServerSwitchPage_Warning => _resourceManager.GetString("ServerSwitchPage_Warning") ?? "Please close the game before switching.";
    public static string ServerSwitchPage_Preparing => _resourceManager.GetString("ServerSwitchPage_Preparing") ?? "Preparing...";
    public static string ServerSwitchPage_NotSelectedPath => _resourceManager.GetString("ServerSwitchPage_NotSelectedPath") ?? "Game path not selected";
    public static string ServerSwitchPage_CN_Display => _resourceManager.GetString("ServerSwitchPage_CN_Display") ?? "CN";
    public static string ServerSwitchPage_OS_Display => _resourceManager.GetString("ServerSwitchPage_OS_Display") ?? "OS";
    public static string ServerSwitchPage_Tips => _resourceManager.GetString("ServerSwitchPage_Tips") ?? "Tips";
    public static string ServerSwitchPage_AlreadyCN => _resourceManager.GetString("ServerSwitchPage_AlreadyCN") ?? "Game is already on CN server";
    public static string ServerSwitchPage_AlreadyOS => _resourceManager.GetString("ServerSwitchPage_AlreadyOS") ?? "Game is already on OS server";
    public static string ServerSwitchPage_ConfirmSwitch => _resourceManager.GetString("ServerSwitchPage_ConfirmSwitch") ?? "Confirm Switch";
    public static string ServerSwitchPage_ConfirmSwitchToCN => _resourceManager.GetString("ServerSwitchPage_ConfirmSwitchToCN") ?? "Switch from OS to CN server?";
    public static string ServerSwitchPage_ConfirmSwitchToOS => _resourceManager.GetString("ServerSwitchPage_ConfirmSwitchToOS") ?? "Switch from CN to OS server?";
    public static string ServerSwitchPage_SwitchComplete => _resourceManager.GetString("ServerSwitchPage_SwitchComplete") ?? "Switch Complete";
    public static string ServerSwitchPage_SwitchCompleteMessage => _resourceManager.GetString("ServerSwitchPage_SwitchCompleteMessage") ?? "Game server switched successfully!";
    public static string ServerSwitchPage_SwitchFailed => _resourceManager.GetString("ServerSwitchPage_SwitchFailed") ?? "Switch Failed";
    public static string ServerSwitchPage_SwitchFailedMessage => _resourceManager.GetString("ServerSwitchPage_SwitchFailedMessage") ?? "Error during server switch: {0}";
    public static string ServerSwitchPage_OK => _resourceManager.GetString("ServerSwitchPage_OK") ?? "OK";
    public static string ServerSwitchPage_Cancel => _resourceManager.GetString("ServerSwitchPage_Cancel") ?? "Cancel";

    // HomeSettingDialog
    public static string HomeSettingDialog_Title => _resourceManager.GetString("HomeSettingDialog_Title") ?? "Game Settings";
    public static string HomeSettingDialog_BasicInfo => _resourceManager.GetString("HomeSettingDialog_BasicInfo") ?? "Basic Info";
    public static string HomeSettingDialog_LaunchArgs => _resourceManager.GetString("HomeSettingDialog_LaunchArgs") ?? "Launch Arguments";
    public static string HomeSettingDialog_CustomBg => _resourceManager.GetString("HomeSettingDialog_CustomBg") ?? "Custom Background";
    public static string HomeSettingDialog_ProxySettings => _resourceManager.GetString("HomeSettingDialog_ProxySettings") ?? "Proxy Settings";
    public static string HomeSettingDialog_LocateGame => _resourceManager.GetString("HomeSettingDialog_LocateGame") ?? "Locate Game";
    public static string HomeSettingDialog_UninstallGame => _resourceManager.GetString("HomeSettingDialog_UninstallGame") ?? "Uninstall Game";
    public static string HomeSettingDialog_UninstallWarning => _resourceManager.GetString("HomeSettingDialog_UninstallWarning") ?? "Delete game folder requires re-download.";
    public static string HomeSettingDialog_ConfirmUninstall => _resourceManager.GetString("HomeSettingDialog_ConfirmUninstall") ?? "Confirm Uninstall";
    public static string HomeSettingDialog_Size => _resourceManager.GetString("HomeSettingDialog_Size") ?? "Size ";
    public static string HomeSettingDialog_Relocate => _resourceManager.GetString("HomeSettingDialog_Relocate") ?? "Relocate";
    public static string HomeSettingDialog_CmdArgs => _resourceManager.GetString("HomeSettingDialog_CmdArgs") ?? "Command-line Arguments";
    public static string HomeSettingDialog_MoreCmdArgs => _resourceManager.GetString("HomeSettingDialog_MoreCmdArgs") ?? "Learn more about command-line arguments";
    public static string HomeSettingDialog_AfterStartGame => _resourceManager.GetString("HomeSettingDialog_AfterStartGame") ?? "After Game Launch";
    public static string HomeSettingDialog_NoAction => _resourceManager.GetString("HomeSettingDialog_NoAction") ?? "No Action";
    public static string HomeSettingDialog_MinimizeToTaskbar => _resourceManager.GetString("HomeSettingDialog_MinimizeToTaskbar") ?? "Minimize to Taskbar";
    public static string HomeSettingDialog_MinimizeToTray => _resourceManager.GetString("HomeSettingDialog_MinimizeToTray") ?? "Minimize to System Tray";
    public static string HomeSettingDialog_CustomLauncher => _resourceManager.GetString("HomeSettingDialog_CustomLauncher") ?? "Custom Launcher";
    public static string HomeSettingDialog_Select => _resourceManager.GetString("HomeSettingDialog_Select") ?? "Select";
    public static string HomeSettingDialog_CustomBgSwitch => _resourceManager.GetString("HomeSettingDialog_CustomBgSwitch") ?? "Custom Background (Supports Video)";
    public static string HomeSettingDialog_CustomBgHint => _resourceManager.GetString("HomeSettingDialog_CustomBgHint") ?? "Please choose images matching the app window size.";
    public static string HomeSettingDialog_DragDropHere => _resourceManager.GetString("HomeSettingDialog_DragDropHere") ?? "Drag image or video file here";
    public static string HomeSettingDialog_EnableProxy => _resourceManager.GetString("HomeSettingDialog_EnableProxy") ?? "Enable Proxy";
    public static string HomeSettingDialog_ProxyHint => _resourceManager.GetString("HomeSettingDialog_ProxyHint") ?? "When enabled, game traffic will be routed through the proxy server.";
    public static string HomeSettingDialog_UninstallCA => _resourceManager.GetString("HomeSettingDialog_UninstallCA") ?? "Uninstall CA Certificate";
    public static string HomeSettingDialog_CannotDeleteRoot => _resourceManager.GetString("HomeSettingDialog_CannotDeleteRoot") ?? "Cannot delete the root directory of a drive";
    public static string HomeSettingDialog_NahidaInGameFolder => _resourceManager.GetString("HomeSettingDialog_NahidaInGameFolder") ?? "NahidaTool is located inside the game folder.";
    public static string HomeSettingDialog_GameIsRunning => _resourceManager.GetString("HomeSettingDialog_GameIsRunning") ?? "Game is currently running. Please close the game first.";
    public static string HomeSettingDialog_UninstallFailed => _resourceManager.GetString("HomeSettingDialog_UninstallFailed") ?? "Uninstall failed: {0}";
    public static string HomeSettingDialog_CannotDecode => _resourceManager.GetString("HomeSettingDialog_CannotDecode") ?? "Cannot decode file. Please select a valid image or video file.";
    public static string HomeSettingDialog_Document => _resourceManager.GetString("HomeSettingDialog_Document") ?? "Document";
    public static string HomeSettingDialog_UnknownError => _resourceManager.GetString("HomeSettingDialog_UnknownError") ?? "An unknown error occurred. Please check the logs.";
    // DocumentSettingPage
    public static string DocumentSettingPage_Subtitle => _resourceManager.GetString("DocumentSettingPage_Subtitle") ?? "";
    public static string DocumentSettingPage_HowToPlay => _resourceManager.GetString("DocumentSettingPage_HowToPlay") ?? "How to Play";
    public static string DocumentSettingPage_Step1 => _resourceManager.GetString("DocumentSettingPage_Step1") ?? "1. Enable Proxy";
    public static string DocumentSettingPage_Step1Detail => _resourceManager.GetString("DocumentSettingPage_Step1Detail") ?? "";
    public static string DocumentSettingPage_StepRSA => _resourceManager.GetString("DocumentSettingPage_StepRSA") ?? "Enable RSA & Hook RSA";
    public static string DocumentSettingPage_StepRSADetail => _resourceManager.GetString("DocumentSettingPage_StepRSADetail") ?? "";
    public static string DocumentSettingPage_Step2 => _resourceManager.GetString("DocumentSettingPage_Step2") ?? "Configure Proxy Address";
    public static string DocumentSettingPage_Step2Detail => _resourceManager.GetString("DocumentSettingPage_Step2Detail") ?? "";
    public static string DocumentSettingPage_Step3 => _resourceManager.GetString("DocumentSettingPage_Step3") ?? "3. Launch the Game";
    public static string DocumentSettingPage_Step3Detail => _resourceManager.GetString("DocumentSettingPage_Step3Detail") ?? "";
    public static string DocumentSettingPage_Step4 => _resourceManager.GetString("DocumentSettingPage_Step4") ?? "4. Troubleshooting";
    public static string DocumentSettingPage_Step4Detail => _resourceManager.GetString("DocumentSettingPage_Step4Detail") ?? "";
    public static string DocumentSettingPage_ErrorCodes => _resourceManager.GetString("DocumentSettingPage_ErrorCodes") ?? "Error Codes";
    public static string DocumentSettingPage_ErrorCodeHint => _resourceManager.GetString("DocumentSettingPage_ErrorCodeHint") ?? "";
    public static string DocumentSettingPage_Error4201 => _resourceManager.GetString("DocumentSettingPage_Error4201") ?? "";
    public static string DocumentSettingPage_Error4206 => _resourceManager.GetString("DocumentSettingPage_Error4206") ?? "";
    public static string DocumentSettingPage_Error4214 => _resourceManager.GetString("DocumentSettingPage_Error4214") ?? "";
    public static string DocumentSettingPage_Error4301 => _resourceManager.GetString("DocumentSettingPage_Error4301") ?? "";
    public static string DocumentSettingPage_Error4308 => _resourceManager.GetString("DocumentSettingPage_Error4308") ?? "";
    public static string DocumentSettingPage_TutorialVideos => _resourceManager.GetString("DocumentSettingPage_TutorialVideos") ?? "Tutorial Videos";
    public static string DocumentSettingPage_VideoConnect => _resourceManager.GetString("DocumentSettingPage_VideoConnect") ?? "How to Connect to a Private Server";
    public static string DocumentSettingPage_VideoProxy => _resourceManager.GetString("DocumentSettingPage_VideoProxy") ?? "Setting Up the Proxy";
    public static string DocumentSettingPage_VideoSwitch => _resourceManager.GetString("DocumentSettingPage_VideoSwitch") ?? "Server Region Switching Tutorial";
    public static string DocumentSettingPage_Back => _resourceManager.GetString("DocumentSettingPage_Back") ?? "Back";
}