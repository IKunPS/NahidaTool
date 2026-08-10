using System;
using Microsoft.UI.Xaml.Controls;
using NahidaTool.Models.Config;
using NahidaTool.Models.Event;
using NahidaTool.Pages.SettingPages;

namespace NahidaTool.Pages;

public sealed partial class SettingsPage : Page
{
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
            ReinitializeCurrentSubPage();
        }
    }

    private void ReinitializeCurrentSubPage()
    {
        if (Frame_Setting.Content is AboutSettingPage aboutPage)
            aboutPage.Initialize();
        else if (Frame_Setting.Content is GeneralSettingPage generalPage)
            generalPage.Initialize();
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
        }
    }

    private void Frame_Setting_Navigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        if (e.Content is AboutSettingPage aboutPage)
            aboutPage.Initialize();
        else if (e.Content is GeneralSettingPage generalPage)
            generalPage.Initialize();
    }
}
