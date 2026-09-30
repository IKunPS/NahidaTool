using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace NahidaTool.Pages;

public sealed partial class HomeSettingDialog
{
    #region 设置弹窗自适应

    private void InitializeResponsiveLayout()
    {
        XamlRoot? root = null;
        Resources["ContentDialogMinWidth"] = 0d;
        Resources["ContentDialogMinHeight"] = 0d;

        void Resize()
        {
            if (root == null || root.Size.Width <= 0 || root.Size.Height <= 0) return;
            SettingsLayout.Width = Math.Max(0, Math.Min(800, root.Size.Width - 48));
            SettingsLayout.Height = Math.Max(0, Math.Min(520, root.Size.Height - 48));
            Resources["ContentDialogMaxWidth"] = SettingsLayout.Width + 2;
            Resources["ContentDialogMaxHeight"] = SettingsLayout.Height + 2;
            bool compact = SettingsLayout.Width < 680;
            SettingsNavigation.PaneDisplayMode = compact
                ? NavigationViewPaneDisplayMode.Top : NavigationViewPaneDisplayMode.Left;
            SettingsNavigation.OpenPaneLength = 176;
            FlipView_Settings.Margin = new Thickness(compact ? 0 : 20, compact ? 8 : 0, 0, 0);
        }

        void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Resize();
        // 首次显示前确定布局，避免打开动画期间再次移动文字。
        Loading += (_, _) =>
        {
            if (root != null) root.Changed -= RootChanged;
            root = XamlRoot;
            if (root != null) root.Changed += RootChanged;
            Resize();
        };
        Closed += (_, _) =>
        {
            if (root != null) root.Changed -= RootChanged;
            root = null;
            Grid_UninstallWarning.Visibility = Visibility.Collapsed;
        };
    }

    #endregion
}
