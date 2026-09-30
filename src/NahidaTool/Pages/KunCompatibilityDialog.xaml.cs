using Microsoft.UI.Xaml.Controls;
using NahidaTool.Models;

namespace NahidaTool.Pages;

internal enum KunCompatibilityIssue
{
    Version,
    Region
}

public sealed partial class KunCompatibilityDialog : ContentDialog
{
    internal KunCompatibilityDialog(KunCompatibilityIssue issue, string currentValue, string requiredValue)
    {
        InitializeComponent();
        NahidaTool.Models.Helper.DialogPresentation.Configure(this, ContentViewport, 440, 420);

        if (issue == KunCompatibilityIssue.Version)
        {
            Title = Lang.HomePage_KunMismatchTitle;
            SummaryText.Text = Lang.HomePage_KunMismatchSummary;
            CurrentLabelText.Text = Lang.HomePage_KunClientVersion;
            RequiredLabelText.Text = Lang.HomePage_KunRequiredVersion;
            ActionText.Text = Lang.HomePage_KunMismatchAction;
            PrimaryButtonText = Lang.HomePage_KunChooseServer;
        }
        else
        {
            Title = Lang.HomePage_KunRegionTitle;
            SummaryText.Text = Lang.HomePage_KunRegionSummary;
            CurrentLabelText.Text = Lang.HomePage_KunCurrentRegion;
            RequiredLabelText.Text = Lang.HomePage_KunRequiredRegion;
            ActionText.Text = Lang.HomePage_KunRegionAction;
            PrimaryButtonText = Lang.HomePage_KunGoToServerSwitch;
        }

        CurrentValueText.Text = currentValue;
        RequiredValueText.Text = requiredValue;
    }
}
