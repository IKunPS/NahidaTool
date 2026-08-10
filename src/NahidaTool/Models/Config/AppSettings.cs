using System;
using System.IO;
using System.Text.Json;
using NahidaTool.Models.Enum;
using NahidaTool.Models.Service;

namespace NahidaTool.Models.Config;

public class AppSettings
{
    public string DownloadPath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NahidaTool");

    public ServerRegionType Region { get; set; } = ServerRegionType.CN;

    public VoiceLanguageType VoiceLanguage { get; set; } = VoiceLanguageType.Chinese;

    public string GameVersion { get; set; } = string.Empty;

    public string GameInstallPath { get; set; } = string.Empty;

    public string LastShownChangelogVersion { get; set; } = string.Empty;

    public CloseWindowOption CloseWindowOption { get; set; } = CloseWindowOption.Exit;

    public string Language { get; set; } = string.Empty;

    public bool EnableRSA { get; set; }

    public bool EnableHookRSA { get; set; }

    public bool EnableProxy { get; set; }

    public string ProxyAddress { get; set; } = "https://127.0.0.1:443";

    public bool StartGameWithCMD { get; set; } // TODO：在主页设置里实现cmd启动游戏

    public bool EnablePopupWindow { get; set; }

    public int StartGameAction { get; set; }

    public bool EnableThirdPartyTool { get; set; }

    public string ThirdPartyToolPath { get; set; } = string.Empty;

    public bool EnableCustomBg { get; set; }

    public string CustomBg { get; set; } = string.Empty;

    public int VideoBgVolume { get; set; } = 100;

    public string AccentColor { get; set; } = string.Empty;

    public string StartGameArgument { get; set; } = string.Empty;

    public bool EnableCpuAffinity { get; set; }

    public long ProcessorAffinityMask { get; set; }

    private static readonly string
        SettingsFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../config.json");
    private static readonly object SettingsFileLock = new();

    public static AppSettings Load()
    {
        lock (SettingsFileLock)
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    string json = File.ReadAllText(SettingsFilePath);
                    return JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.AppSettings) ??
                           new AppSettings();
                }
            }
            catch (Exception ex)
            {
                LogService.Error("加载配置文件失败，将使用默认配置", ex);
            }

            return new AppSettings();
        }
    }

    public void Save()
    {
        lock (SettingsFileLock)
        {
            try
            {
                string? directory = Path.GetDirectoryName(SettingsFilePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string json = JsonSerializer.Serialize(this, AppJsonSerializerContext.Default.AppSettings);
                File.WriteAllText(SettingsFilePath, json);
            }
            catch (Exception ex)
            {
                LogService.Error("保存配置文件失败", ex);
            }
        }
    }

    public static AppSettings Update(Action<AppSettings> update)
    {
        lock (SettingsFileLock)
        {
            var settings = Load();
            update(settings);
            settings.Save();
            return settings;
        }
    }
}
