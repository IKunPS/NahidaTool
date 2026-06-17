using System;
using Microsoft.UI.Xaml.Controls;
using NahidaTool.Models.Config;
using NahidaTool.Models.Enum;
using NahidaTool.Models.Event;
using NahidaTool.Pages.SettingPages;

namespace NahidaTool.Pages;

public sealed partial class SettingsPage : Page
{
    public string DownloadPath { get; set; } = string.Empty;
    public ServerRegionType Region { get; set; } = ServerRegionType.CN;
    public VoiceLanguageType VoiceLanguage { get; set; } = VoiceLanguageType.Chinese;
    public string GameVersion { get; set; } = string.Empty;

    public event EventHandler? SettingsChanged;
    public event EventHandler<ServerRegionType>? RegionChanged;
    public event EventHandler<VoiceLanguageType>? VoiceLanguageChanged;

    private DownloadSettingPage? _currentDownloadPage;
    private AppSettings? _lastSettings;

    public SettingsPage()
    {
        InitializeComponent();

        this.NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;

        LanguageChangedMessage.LanguageChanged += OnLanguageChanged;

        Frame_Setting.Navigated += Frame_Setting_Navigated;
        Frame_Setting.Navigate(typeof(AboutSettingPage));
    }

    private void OnLanguageChanged()
    {
        this.Bindings.Update();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is AppSettings settings)
        {
            _lastSettings = settings;
            DownloadPath = settings.DownloadPath;
            Region = settings.Region;
            VoiceLanguage = settings.VoiceLanguage;
            GameVersion = settings.GameVersion;

            ReinitializeCurrentSubPage();
        }
        else if (e.Parameter is string downloadPath)
        {
            DownloadPath = downloadPath;
        }
    }

    private void ReinitializeCurrentSubPage()
    {
        if (Frame_Setting.Content is DownloadSettingPage downloadPage)
        {
            downloadPage.Initialize(DownloadPath, Region, VoiceLanguage, GameVersion);
        }
        else if (Frame_Setting.Content is AboutSettingPage aboutPage)
        {
            aboutPage.Initialize();
        }
        else if (Frame_Setting.Content is GeneralSettingPage generalPage)
        {
            generalPage.Initialize();
        }
    }

    private void NavigationView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        var tag = args.InvokedItemContainer?.Tag?.ToString() ?? "";
        switch (tag)
        {
            case "AboutSetting":
                if (Frame_Setting.Content is not AboutSettingPage)
                    Frame_Setting.Navigate(typeof(AboutSettingPage));
                break;
            case "GeneralSetting":
                if (Frame_Setting.Content is not GeneralSettingPage)
                    Frame_Setting.Navigate(typeof(GeneralSettingPage));
                break;
            case "DownloadSetting":
                if (Frame_Setting.Content is not DownloadSettingPage)
                    Frame_Setting.Navigate(typeof(DownloadSettingPage));
                break;
        }
    }

    private void Frame_Setting_Navigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        if (_currentDownloadPage is not null)
        {
            _currentDownloadPage.SettingsChanged -= OnDownloadPageSettingsChanged;
            _currentDownloadPage.RegionChanged -= OnDownloadPageRegionChanged;
            _currentDownloadPage.VoiceLanguageChanged -= OnDownloadPageVoiceLanguageChanged;
            _currentDownloadPage = null;
        }

        if (e.Content is DownloadSettingPage downloadPage)
        {
            _currentDownloadPage = downloadPage;
            downloadPage.SettingsChanged += OnDownloadPageSettingsChanged;
            downloadPage.RegionChanged += OnDownloadPageRegionChanged;
            downloadPage.VoiceLanguageChanged += OnDownloadPageVoiceLanguageChanged;
            downloadPage.Initialize(DownloadPath, Region, VoiceLanguage, GameVersion);
        }
        else if (e.Content is AboutSettingPage aboutPage)
        {
            aboutPage.Initialize();
        }
        else if (e.Content is GeneralSettingPage generalPage)
        {
            generalPage.Initialize();
        }
    }

    private void OnDownloadPageSettingsChanged(object? sender, EventArgs e)
    {
        if (sender is DownloadSettingPage dp)
        {
            DownloadPath = dp.DownloadPath;
            GameVersion = dp.GameVersion;
        }
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnDownloadPageRegionChanged(object? sender, ServerRegionType region)
    {
        Region = region;
        RegionChanged?.Invoke(this, region);
    }

    private void OnDownloadPageVoiceLanguageChanged(object? sender, VoiceLanguageType language)
    {
        VoiceLanguage = language;
        VoiceLanguageChanged?.Invoke(this, language);
    }
}
