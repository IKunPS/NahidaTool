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
    #region Custom Background

    public static readonly string BgFolder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "bg"));

    public static readonly HashSet<string> SupportedBgExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp",
        ".mp4", ".webm", ".avi", ".wmv", ".mov"
    };

    /// <summary>
    /// 将文件复制到 bg 文件夹并返回新路径。自动处理同名文件（添加序号）。
    /// 如果源文件已在 bg 文件夹中，直接返回原路径。
    /// </summary>
    public static string CopyFileToBgFolder(string sourcePath)
    {
        try
        {
            Directory.CreateDirectory(BgFolder);

            // 源文件已在 bg 文件夹中，无需复制
            string sourceDir = Path.GetDirectoryName(sourcePath) ?? "";
            if (string.Equals(sourceDir, BgFolder, StringComparison.OrdinalIgnoreCase))
                return sourcePath;

            string ext = Path.GetExtension(sourcePath);
            string nameWithoutExt = Path.GetFileNameWithoutExtension(sourcePath);
            string destPath = Path.Combine(BgFolder, $"{nameWithoutExt}{ext}");

            // 如果同名文件已存在且内容不同，添加数字序号
            if (File.Exists(destPath))
            {
                try
                {
                    var sourceInfo = new FileInfo(sourcePath);
                    var destInfo = new FileInfo(destPath);
                    if (sourceInfo.Length == destInfo.Length && sourceInfo.LastWriteTime == destInfo.LastWriteTime)
                        return destPath; // 同一个文件，无需复制
                }
                catch { }

                for (int i = 1; i < 1000; i++)
                {
                    destPath = Path.Combine(BgFolder, $"{nameWithoutExt}_{i}{ext}");
                    if (!File.Exists(destPath))
                        break;
                }
            }

            File.Copy(sourcePath, destPath, overwrite: true);
            LogService.Info($"背景文件已复制: {sourcePath} → {destPath}");
            return destPath;
        }
        catch (Exception ex)
        {
            LogService.Error("复制背景文件失败", ex);
            return sourcePath; // 回退到原始路径
        }
    }

    private void CustomBgToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _enableCustomBg = CustomBgToggleSwitch.IsOn;
        ChangeCustomBgButton.IsEnabled = _enableCustomBg;
        SaveBgImmediately();
    }

    private async void ChangeCustomBg_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ChangeBgErrorText.Visibility = Visibility.Collapsed;
            ChangeBgErrorText.Text = "";
            var filePath = await PickImageOrVideoFileAsync();
            if (!string.IsNullOrEmpty(filePath))
            {
                if (!await ValidateCustomBgFileAsync(filePath))
                {
                    ChangeBgErrorText.Text = Lang.HomeSettingDialog_CannotDecode;
                    ChangeBgErrorText.Visibility = Visibility.Visible;
                    return;
                }
                // 复制到 bg 文件夹，确保持久化
                string persistedPath = CopyFileToBgFolder(filePath);
                _customBg = persistedPath;
                _enableCustomBg = true;
                CustomBgToggleSwitch.IsOn = true;
                CustomBgPathText.Text = persistedPath;
                CustomBgPathGrid.Visibility = Visibility.Visible;
                ChangeCustomBgButton.IsEnabled = true;
                SaveBgImmediately();
            }
        }
        catch (Exception ex)
        {
            ChangeBgErrorText.Text = Lang.HomeSettingDialog_UnknownError;
            ChangeBgErrorText.Visibility = Visibility.Visible;
            LogService.Error("发生未知错误", ex);
        }
    }

    private async void OpenCustomBg_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (File.Exists(_customBg))
                await Launcher.LaunchUriAsync(new Uri(_customBg));
        }
        catch (Exception ex)
        {
            LogService.Debug($"打开自定义背景失败: {ex.Message}");
        }
    }

    private void DeleteCustomBg_Click(object sender, RoutedEventArgs e)
    {
        _customBg = string.Empty;
        CustomBgPathText.Text = string.Empty;
        CustomBgPathGrid.Visibility = Visibility.Collapsed;
        CustomBgToggleSwitch.IsOn = false;
        _enableCustomBg = false;
        ChangeCustomBgButton.IsEnabled = false;
        ChangeBgErrorText.Visibility = Visibility.Collapsed;
        ChangeBgErrorText.Text = "";
        SaveBgImmediately();
    }

    private void VolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        _videoBgVolume = (int)e.NewValue;
        if (_videoBgVolume > 0)
            _notMuteVolume = _videoBgVolume;
        UpdateVolumeIcon();
        NotifyVideoVolume();
    }

    private void MuteBg_Click(object sender, RoutedEventArgs e)
    {
        if (_videoBgVolume > 0)
        {
            _notMuteVolume = _videoBgVolume;
            _videoBgVolume = 0;
        }
        else
        {
            _videoBgVolume = _notMuteVolume;
        }
        VolumeSlider.Value = _videoBgVolume;
        UpdateVolumeIcon();
        NotifyVideoVolume();
    }

    private void UpdateVolumeIcon()
    {
        VolumeIcon.Glyph = _videoBgVolume switch
        {
            > 66 => "\uE995",
            > 33 => "\uE994",
            > 1 => "\uE993",
            _ => "\uE992",
        };
    }

    #endregion

    #region Helpers

    private void SaveBgImmediately()
    {
        if (_settings == null) return;
        _settings.VideoBgVolume = _videoBgVolume;
        _settings.Save();
        BackgroundChangedMessage.Send();
        ConfigureBackgroundPosition();
    }

    private void NotifyVideoVolume()
    {
        if (_settings != null)
        {
            _settings.VideoBgVolume = _videoBgVolume;
            _settings.Save();
        }
        if (App.MainWindow is MainWindow mw)
            mw.UpdateVideoVolume(_videoBgVolume);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private async Task ShowErrorDialogAsync(string message)
    {
        GeneralErrorText.Text = message;
        GeneralErrorText.Visibility = Visibility.Visible;
        await Task.CompletedTask;
    }

    /// <summary>
    private static async Task<bool> ValidateCustomBgFileAsync(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return false;

        try
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp")
            {
                using var stream = File.OpenRead(filePath);
                var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(
                    stream.AsRandomAccessStream());
                return decoder.FrameCount > 0;
            }
            if (ext is ".mp4" or ".webm" or ".avi" or ".wmv" or ".mov")
            {
                using var fileStream = File.OpenRead(filePath);
                return fileStream.Length > 0;
            }
            return false;
        }
        catch (Exception ex)
        {
            LogService.Debug($"验证自定义背景文件失败 ({filePath}): {ex.Message}");
            return false;
        }
    }

    private async Task<string?> PickImageOrVideoFileAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".bmp");
            picker.FileTypeFilter.Add(".gif");
            picker.FileTypeFilter.Add(".webp");
            picker.FileTypeFilter.Add(".mp4");
            picker.FileTypeFilter.Add(".webm");
            picker.FileTypeFilter.Add(".avi");
            picker.FileTypeFilter.Add(".wmv");
            picker.FileTypeFilter.Add(".mov");
            var file = await picker.PickSingleFileAsync();
            return file?.Path;
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            LogService.Warn($"FileOpenPicker 初始化失败，回退到 Win32 文件选择器: {ex.Message}");
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            return await FolderPickerHelper.PickFileAsync(hwnd, "图片和视频|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.mp4;*.webm;*.avi;*.wmv;*.mov");
        }
        catch (Exception ex)
        {
            LogService.Error("选择自定义背景文件失败", ex);
            return null;
        }
    }

    #endregion
}
