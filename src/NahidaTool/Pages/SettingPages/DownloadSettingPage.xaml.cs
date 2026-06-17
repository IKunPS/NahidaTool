using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NahidaTool.Models.Enum;
using NahidaTool.Models.Helper;
using NahidaTool.Models.Service;

namespace NahidaTool.Pages.SettingPages;

public sealed partial class DownloadSettingPage : Page
{
    public string DownloadPath { get; set; } = string.Empty;
    public ServerRegionType Region { get; set; } = ServerRegionType.CN;
    public VoiceLanguageType VoiceLanguage { get; set; } = VoiceLanguageType.Chinese;
    public string GameVersion { get; set; } = string.Empty;

    public event EventHandler? SettingsChanged;
    public event EventHandler<ServerRegionType>? RegionChanged;
    public event EventHandler<VoiceLanguageType>? VoiceLanguageChanged;

    private bool _isInitializing = true;

    public DownloadSettingPage()
    {
        InitializeComponent();
    }

    public void Initialize(string downloadPath, ServerRegionType region, VoiceLanguageType voiceLanguage, string gameVersion)
    {
        _isInitializing = true;

        DownloadPath = downloadPath;
        Region = region;
        VoiceLanguage = voiceLanguage;
        GameVersion = gameVersion;

        DownloadPathText.Text = DownloadPath;
        GameVersionTextBox.Text = GameVersion;
        RegionComboBox.SelectedIndex = Region == ServerRegionType.CN ? 0 : 1;
        VoiceLanguageComboBox.SelectedIndex = VoiceLanguage switch
        {
            VoiceLanguageType.None => 0,
            VoiceLanguageType.Chinese => 1,
            VoiceLanguageType.English => 2,
            VoiceLanguageType.Japanese => 3,
            VoiceLanguageType.Korean => 4,
            _ => 1
        };

        _isInitializing = false;
    }

    private async void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            var folderPath = await FolderPickerHelper.PickFolderAsync(hwnd, "选择下载路径");
            if (!string.IsNullOrEmpty(folderPath))
            {
                DownloadPath = folderPath;
                DownloadPathText.Text = DownloadPath;
                OnSettingsChanged();
            }
        }
        catch (Exception ex)
        {
            LogService.Error("选择下载路径失败", ex);
        }
    }

    private void GameVersionTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing) return;
        GameVersion = GameVersionTextBox.Text;
        OnSettingsChanged();
    }

    private void OnSettingsChanged()
    {
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RegionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;

        if (RegionComboBox.SelectedItem is ComboBoxItem selectedItem)
        {
            string? tag = selectedItem.Tag?.ToString();
            Region = tag == "OS" ? ServerRegionType.OS : ServerRegionType.CN;
            RegionChanged?.Invoke(this, Region);
            OnSettingsChanged();
        }
    }

    private void VoiceLanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;

        if (VoiceLanguageComboBox.SelectedItem is ComboBoxItem selectedItem)
        {
            string? tag = selectedItem.Tag?.ToString();
            VoiceLanguage = tag switch
            {
                "None" => VoiceLanguageType.None,
                "Chinese" => VoiceLanguageType.Chinese,
                "English" => VoiceLanguageType.English,
                "Japanese" => VoiceLanguageType.Japanese,
                "Korean" => VoiceLanguageType.Korean,
                _ => VoiceLanguageType.Chinese
            };
            VoiceLanguageChanged?.Invoke(this, VoiceLanguage);
            OnSettingsChanged();
        }
    }
}
