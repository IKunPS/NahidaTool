using System.Collections.Generic;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NahidaTool.Models;
using NahidaTool.Models.Config;
using NahidaTool.Models.Helper;

namespace NahidaTool.Pages;

public sealed partial class ChangelogDialog : ContentDialog
{
    public IReadOnlyList<ChangelogHistoryEntry> History { get; } = ChangelogHistory.GetEntries();

    public ChangelogDialog()
    {
        InitializeComponent();
        VersionBadge.Text = $"v{AppVersion.Current}";
        AutomationProperties.SetName(this, $"NahidaTool {AppVersion.Current} {Lang.Changelog_Title}");
        DialogPresentation.Configure(this, ContentViewport, 480, 520);
    }
}
