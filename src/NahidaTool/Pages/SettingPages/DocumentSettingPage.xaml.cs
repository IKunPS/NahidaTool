using System.Collections.Generic;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NahidaTool.Models;
using NahidaTool.Models.Service;
using Windows.Media.Core;

namespace NahidaTool.Pages.SettingPages;

public sealed partial class DocumentSettingPage : Page
{
    private readonly Dictionary<string, string> _videoFiles = new();

    public DocumentSettingPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LoadVideos();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        VideoPlayer.Source = null;
        VideoPlayerOverlay.Visibility = Visibility.Collapsed;
        VideoPlayerOverlay.Opacity = 0;
    }

    private void LoadVideos()
    {
        AddVideo(Lang.DocumentSettingPage_VideoConnect, "tutorial_connect.mp4");

        VideoList.ItemsSource = _videoFiles.Keys;
    }

    private void AddVideo(string title, string fileName)
    {
        _videoFiles[title] = fileName;
    }

    private void VideoCard_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not Button btn || btn.DataContext is not string title)
                return;

            if (!_videoFiles.TryGetValue(title, out var fileName))
                return;

            var videoPath = FindVideoFile(fileName);
            if (string.IsNullOrEmpty(videoPath))
            {
                LogService.Warn($"教程视频未找到: {fileName}");
                return;
            }

            VideoPlayer.Source = MediaSource.CreateFromUri(new System.Uri(videoPath));
            VideoTitleText.Text = title;
            DocumentScrollViewer.Visibility = Visibility.Collapsed;
            VideoPlayerOverlay.Opacity = 0;
            VideoPlayerOverlay.Visibility = Visibility.Visible;
            _ = VideoPlayerOverlay.DispatcherQueue.TryEnqueue(() => VideoPlayerOverlay.Opacity = 1);
        }
        catch (System.Exception ex)
        {
            LogService.Error("播放教程视频失败", ex);
        }
    }

    private void BackFromVideo_Click(object sender, RoutedEventArgs e)
    {
        VideoPlayer.Source = null;
        VideoPlayerOverlay.Visibility = Visibility.Collapsed;
        VideoPlayerOverlay.Opacity = 0;
        DocumentScrollViewer.Visibility = Visibility.Visible;
    }

    private static string? FindVideoFile(string fileName)
    {
        // 发布后 Assets 会复制到输出目录
        var runtimePath = Path.Combine(System.AppContext.BaseDirectory, "Assets", "Tutorials", fileName);
        if (File.Exists(runtimePath))
            return runtimePath;

        // 开发环境：bin/x64/Debug → 项目根目录
        var devPath = Path.GetFullPath(Path.Combine(System.AppContext.BaseDirectory, "..", "..", "..", "Assets", "Tutorials", fileName));
        if (File.Exists(devPath))
            return devPath;

        return null;
    }
}
