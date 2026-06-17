using Microsoft.UI.Xaml.Controls;
using NahidaTool.Models;
using NahidaTool.Models.Config;

namespace NahidaTool.Pages.SettingPages;

public sealed partial class AboutSettingPage : Page
{
    public AboutSettingPage()
    {
        InitializeComponent();
    }

    public void Initialize()
    {
        VersionTextBlock.Text = $"{AppVersion.Current}";
    }
}
