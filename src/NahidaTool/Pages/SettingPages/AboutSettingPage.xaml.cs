using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NahidaTool.Models.Config;
using NahidaTool.Models.Service;

namespace NahidaTool.Pages.SettingPages;

public sealed partial class AboutSettingPage : Page
{
    public AboutSettingPage()
    {
        InitializeComponent();
    }

    public void Initialize()
    {
        VersionTextBlock.Text = AppVersion.Current;
    }

    private async void ChangelogButton_Click(object sender, RoutedEventArgs e)
    {
        if (XamlRoot == null) return;
        ChangelogButton.IsEnabled = false;
        try { await new ChangelogDialog { XamlRoot = XamlRoot }.ShowAsync(); }
        catch (Exception exception) { LogService.Error("显示更新日志失败", exception); }
        finally { ChangelogButton.IsEnabled = true; }
    }
}
