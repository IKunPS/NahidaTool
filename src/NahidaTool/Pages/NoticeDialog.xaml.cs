using System;
using Microsoft.UI.Xaml.Controls;
using NahidaTool.Models;
using NahidaTool.Models.Helper;

namespace NahidaTool.Pages;

public sealed partial class NoticeDialog : ContentDialog
{
    public NoticeDialog(string title, string message, bool warning = false)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        // 不使用延迟绑定，避免加载时覆盖调用方设置的取消文案。
        CloseButtonText = Lang.HomePage_OK;
        if (warning) MessageIcon.Glyph = "\uE7BA";
        DialogPresentation.Configure(this, ContentViewport, 420, 360);
    }

    internal static NoticeDialog CreateConfirmation(string title, string message,
        string confirmText, string cancelText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmText);
        ArgumentException.ThrowIfNullOrWhiteSpace(cancelText);
        return new NoticeDialog(title, message, warning: true)
        {
            PrimaryButtonText = confirmText,
            SecondaryButtonText = string.Empty,
            CloseButtonText = cancelText,
            DefaultButton = ContentDialogButton.Close
        };
    }
}
