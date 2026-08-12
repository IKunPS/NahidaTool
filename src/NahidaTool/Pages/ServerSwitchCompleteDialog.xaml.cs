using Microsoft.UI.Xaml.Controls;
using NahidaTool.Models;
using NahidaTool.Models.Enum;

namespace NahidaTool.Pages;

public sealed partial class ServerSwitchCompleteDialog : ContentDialog
{
    public ServerSwitchCompleteDialog(ServerRegionType region, string version)
    {
        InitializeComponent();

        RegionText.Text = region == ServerRegionType.CN
            ? Lang.ServerSwitchPage_CN_Display
            : Lang.ServerSwitchPage_OS_Display;
        VersionText.Text = string.IsNullOrWhiteSpace(version)
            ? string.Empty
            : $"{Lang.DownloadPage_Version} {version}";
    }
}