using System;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace NahidaTool.Models.Helper;

internal static class DialogPresentation
{
    #region 弹窗布局

    internal static void Configure(ContentDialog dialog, ScrollViewer viewport,
        double preferredWidth = 460, double maximumHeight = 420)
    {
        dialog.Resources["ContentDialogSmokeFill"] = new SolidColorBrush(Colors.Transparent);
        dialog.Resources["ContentDialogTopOverlay"] = new SolidColorBrush(Colors.Transparent);
        dialog.Resources["ContentDialogPadding"] = new Thickness(24);
        dialog.Resources["ContentDialogTitleMargin"] = new Thickness(0, 0, 0, 20);
        dialog.Resources["ContentDialogMinWidth"] = 0d;
        dialog.Resources["ContentDialogMinHeight"] = 0d;
        dialog.Resources["ContentDialogMaxWidth"] = preferredWidth + 50;
        dialog.Resources["ContentDialogMaxHeight"] = maximumHeight + 200;
        viewport.Width = preferredWidth;
        viewport.MaxHeight = maximumHeight;
        XamlRoot? root = null;

        void Resize()
        {
            if (root == null) return;
            double width = Math.Max(40, Math.Min(preferredWidth, root.Size.Width - 96));
            double titleHeight = Find<ContentControl>(dialog, "Title")?.ActualHeight ?? 60;
            viewport.Width = width;
            viewport.MaxHeight = Math.Max(40, Math.Min(maximumHeight,
                root.Size.Height - titleHeight - 170));
            ArrangeButtons(dialog, width);
        }

        void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Resize();
        dialog.Opened += (_, _) =>
        {
            root = dialog.XamlRoot;
            if (root != null) root.Changed += RootChanged;
            Resize();
        };
        dialog.Closed += (_, _) =>
        {
            if (root != null) root.Changed -= RootChanged;
            root = null;
        };
    }

    private static void ArrangeButtons(ContentDialog dialog, double availableWidth)
    {
        // 仅收紧原生模板的按钮排列，不替换模板或按钮事件。
        Grid? commands = Find<Grid>(dialog, "CommandSpace");
        if (commands == null) return;
        // 避免按钮区重复叠加毛玻璃，使整张弹窗保持同一层透明背景。
        commands.Background = new SolidColorBrush(Colors.Transparent);
        commands.HorizontalAlignment = HorizontalAlignment.Right;
        commands.Padding = new Thickness(24, 16, 24, 16);
        if (commands.ColumnDefinitions.Count == 5)
        {
            commands.ColumnDefinitions[0].Width = GridLength.Auto;
            commands.ColumnDefinitions[2].Width = GridLength.Auto;
            commands.ColumnDefinitions[4].Width = GridLength.Auto;
        }
        Button[] buttons = commands.Children.OfType<Button>()
            .Where(button => button.Visibility == Visibility.Visible).ToArray();
        double buttonWidth = Math.Max(40, (availableWidth - Math.Max(0, buttons.Length - 1) * 8)
            / Math.Max(1, buttons.Length));
        foreach (Button button in buttons)
        {
            button.MinWidth = Math.Min(108, buttonWidth);
            button.MaxWidth = buttonWidth;
            button.MinHeight = 36;
            button.CornerRadius = new CornerRadius(8);
            button.Padding = new Thickness(16, 8, 16, 8);
        }
    }

    private static T? Find<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T element && element.Name == name) return element;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            T? found = Find<T>(VisualTreeHelper.GetChild(root, index), name);
            if (found != null) return found;
        }
        return null;
    }

    #endregion
}
