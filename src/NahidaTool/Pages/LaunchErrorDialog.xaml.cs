using Microsoft.UI.Xaml.Controls;

namespace NahidaTool.Pages;

public sealed partial class LaunchErrorDialog : ContentDialog
{
    public LaunchErrorDialog(string message)
    {
        InitializeComponent();
        NahidaTool.Models.Helper.DialogPresentation.Configure(this, ContentViewport, 420, 360);
        MessageText.Text = message;
    }
}
