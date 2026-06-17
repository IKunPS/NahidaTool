using System;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NahidaTool.Models;
using NahidaTool.Models.Config;
using NahidaTool.Models.Enum;
using NahidaTool.Models.Event;
using NahidaTool.Models.Service;

namespace NahidaTool.Pages.SettingPages;

public sealed partial class GeneralSettingPage : Page
{
    public GeneralSettingPage()
    {
        InitializeComponent();
    }

    public void Initialize()
    {
        try
        {
            InitializeLanguageSelector();
            InitializeCloseWindowOption();
        }
        finally
        {
        }
    }

    #region Language

    private bool _languageInitialized;

    private void InitializeLanguageSelector()
    {
        try
        {
            var lang = AppSettings.Load().Language;
            ComboBox_Language.Items.Clear();

            // "跟随系统"始终以系统安装语言显示，不跟随应用语言切换
            var followSystemText = Lang.ResourceManager.GetString("GeneralSettingPage_FollowSystem", CultureInfo.InstalledUICulture)
                                   ?? "Follow System";
            ComboBox_Language.Items.Add(new ComboBoxItem
            {
                Content = followSystemText,
                Tag = "",
            });
            ComboBox_Language.SelectedIndex = 0;
            foreach (var (Title, LangCode) in Localization.LanguageList)
            {
                var box = new ComboBoxItem
                {
                    Content = Title,
                    Tag = LangCode,
                };
                ComboBox_Language.Items.Add(box);
                if (LangCode == lang)
                {
                    ComboBox_Language.SelectedItem = box;
                }
            }
        }
        finally
        {
            _languageInitialized = true;
        }
    }

    private void ComboBox_Language_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (ComboBox_Language.SelectedItem is ComboBoxItem item && _languageInitialized)
            {
                var lang = item.Tag as string ?? "";
                var settings = AppSettings.Load();
                settings.Language = lang;
                if (string.IsNullOrWhiteSpace(lang))
                {
                    CultureInfo.CurrentUICulture = CultureInfo.InstalledUICulture;
                }
                else
                {
                    CultureInfo.CurrentUICulture = new CultureInfo(lang);
                }
                settings.Save();
                this.Bindings.Update();
                LanguageChangedMessage.Send();
            }
        }
        catch (CultureNotFoundException)
        {
            CultureInfo.CurrentUICulture = CultureInfo.InstalledUICulture;
        }
        catch (Exception ex)
        {
            LogService.Error("语言切换失败", ex);
        }
    }

    #endregion

    #region Close Window Option

    private bool _closeWindowOptionInitialized;

    private void InitializeCloseWindowOption()
    {
        try
        {
            var option = AppSettings.Load().CloseWindowOption;
            if (option is CloseWindowOption.Hide)
                RadioButton_CloseWindowOption_Hide.IsChecked = true;
            else
                RadioButton_CloseWindowOption_Exit.IsChecked = true;
        }
        finally
        {
            _closeWindowOptionInitialized = true;
        }
    }

    private void RadioButton_CloseWindowOption_Checked(object sender, RoutedEventArgs e)
    {
        if (!_closeWindowOptionInitialized) return;
        if (sender is FrameworkElement fe && fe.Tag is string tag)
        {
            var settings = AppSettings.Load();
            settings.CloseWindowOption = tag switch
            {
                "Hide" => CloseWindowOption.Hide,
                _ => CloseWindowOption.Exit
            };
            settings.Save();
        }
    }

    #endregion
}