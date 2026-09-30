using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;
using Windows.System;
using Windows.UI;
using NahidaTool.Models.Config;
using NahidaTool.Models.Enum;
using NahidaTool.Models.Event;
using NahidaTool.Models.Helper;
using NahidaTool.Models.Service;
using System.Globalization;
using NahidaTool.Models;

namespace NahidaTool.Pages;

public sealed partial class HomeSettingDialog
{
    private void ProxyToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _enableProxy = ProxyToggleSwitch.IsOn;
        KunToggleSwitch.IsEnabled = _enableProxy;
        if (_settings != null)
        {
            _settings.EnableProxy = _enableProxy;
            _settings.EnableKun = _enableKun;
            _settings.Save();
        }
        ProxySettingChangedMessage.Send();
    }

    private void KunToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _enableKun = KunToggleSwitch.IsOn;
        if (_settings != null)
        {
            _settings.EnableKun = _enableKun;
            _settings.EnableProxy = _enableProxy;
            _settings.Save();
        }
        ProxySettingChangedMessage.Send();
    }

    private void UninstallCaButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProxyService.IsRunning)
        {
            LogService.Info("代理运行中，无法卸载 CA 证书");
            return;
        }

        bool success = ProxyService.DestroyCertificate();
        LogService.Info(success ? "CA 证书已卸载" : "CA 证书卸载失败");
    }

}
