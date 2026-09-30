using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

using Windows.UI;
using NahidaTool.Models;
using NahidaTool.Models.Config;
using NahidaTool.Models.Enum;
using NahidaTool.Models.Event;
using NahidaTool.Models.Helper;
using NahidaTool.Models.Service;

namespace NahidaTool.Pages;

public sealed partial class HomePage
{
    #region Download

    private void RegisterDownloadServiceEvents()
    {
        var ds = DownloadService.Instance;
        ds.StatusChanged += DownloadService_StatusChanged;
        ds.ProgressChanged += DownloadService_ProgressChanged;
        ds.ProgressTextChanged += DownloadService_ProgressTextChanged;
        ds.DownloadCompleted += DownloadService_DownloadCompleted;
        ds.DownloadFailed += DownloadService_DownloadFailed;
        ds.SpeedUpdated += DownloadService_SpeedUpdated;
    }

    /// <summary>
    /// 未安装客户端：弹出"选择安装路径"毛玻璃弹窗（语言多选/快捷方式/容量）
    /// </summary>
    private async Task ShowNoClientDialogAsync()
    {
        var dialog = new DownloadGameDialog { XamlRoot = XamlRoot };
        dialog.Initialize(_lastSettings);

        // "已安装？定位游戏"回调：关闭弹窗并走定位流程
        dialog.LocateGameRequested += (_, _) =>
        {
            dialog.Hide();
            _ = DoLocateGameAsync();
        };

        // 版本变化：立即保存到配置并重新拉取容量
        dialog.VersionChanged += (_, _) =>
        {
            _lastSettings = AppSettings.Update(settings => settings.GameVersion = dialog.GameVersion);
            _ = RefreshDialogSizesAsync(dialog);
        };
        // 服区变化时切换 API 区域并重新拉取容量
        dialog.RegionChanged += (_, _) =>
        {
            _apiService.SetRegion(dialog.Region);
            _ = RefreshDialogSizesAsync(dialog);
        };
        dialog.Closed += (_, _) => CancelDialogSizeRefresh();

        // 异步拉取构建信息填充容量（失败静默，不影响弹窗）
        _ = RefreshDialogSizesAsync(dialog);
        // 异步获取私服当前版本并推荐到弹窗（失败静默）
        _ = RecommendServerVersionAsync(dialog);

        await dialog.ShowAsync();
        if (!dialog.Confirmed) return;

    // 保存对话框选择到配置（优先保存输入框中的版本号，空版本回退到服务端解析的最新版）
    string downloadTag = string.IsNullOrWhiteSpace(dialog.GameVersion)
        ? dialog.ResolvedVersion
        : dialog.GameVersion;
    _lastSettings = AppSettings.Update(settings =>
    {
        settings.Region = dialog.Region;
        settings.DownloadPath = dialog.InstallPath;
        settings.GameVersion = downloadTag;
        settings.VoiceLanguage = dialog.SelectedVoices;
    });
    _currentRegion = dialog.Region;
    _apiService.SetRegion(_currentRegion);
    _downloadPath = dialog.InstallPath;
    _currentVoiceLanguage = dialog.SelectedVoices;

        DownloadService.Instance.Initialize(_downloadPath);

        // 下载游戏：空版本 = 最新；多语音按勾选下载
        await DownloadClientAsync(downloadTag, dialog.SelectedVoices);
    }

    /// <summary>
    /// 从私服状态接口获取当前版本并推荐到下载弹窗（失败静默，不影响弹窗使用）
    /// </summary>
    private async Task RecommendServerVersionAsync(DownloadGameDialog dialog)
    {
        try
        {
            string? version = await PrivateServerService.GetServerVersionAsync(_lastSettings);
            if (!string.IsNullOrWhiteSpace(version))
                dialog.SetRecommendedVersion(version);
        }
        catch (Exception ex)
        {
            LogService.Debug($"推荐私服版本失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 拉取构建信息并刷新弹窗容量显示
    /// </summary>
    private async Task RefreshDialogSizesAsync(DownloadGameDialog dialog)
    {
        var requestCts = new CancellationTokenSource();
        var previousCts = Interlocked.Exchange(ref _dialogSizeRefreshCts, requestCts);
        previousCts?.Cancel();

        string requestedVersion = dialog.GameVersion;
        dialog.BeginResourceCheck();
        try
        {
            await Task.Delay(250, requestCts.Token);
            var buildResponse = await _apiService.GetBuildInfoAsync(
                string.IsNullOrEmpty(requestedVersion) ? null : requestedVersion, requestCts.Token);

            if (requestCts.IsCancellationRequested ||
                !string.Equals(dialog.GameVersion, requestedVersion, StringComparison.Ordinal))
                return;
            var builds = buildResponse.Data?.Manifests ?? new List<BuildData>();

            var game = FindGameResource(builds);
            if (!DownloadService.IsValidResource(game))
            {
                dialog.SetResourceError(Lang.DownloadPage_NoResourceInfo);
                return;
            }

            long gameCompressed = game?.Stats?.CompressedSize ?? 0;
            long gameUncompressed = game?.Stats?.UncompressedSize ?? 0;

            var voiceSizes = new Dictionary<VoiceLanguageType, (long Compressed, long Uncompressed)>();
            foreach (var lang in Enum.GetValues<VoiceLanguageType>())
            {
                if (lang == VoiceLanguageType.None) continue;
                var voice = builds.FirstOrDefault(b => b.MatchingField == ServerConfig.VoicePackages.GetMatchingField(lang));
                voiceSizes[lang] = (voice?.Stats?.CompressedSize ?? 0, voice?.Stats?.UncompressedSize ?? 0);
            }

            dialog.UpdateSizes(gameCompressed, gameUncompressed, voiceSizes,
                buildResponse.Data?.Tag ?? requestedVersion);
        }
        catch (OperationCanceledException) when (requestCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LogService.Warn($"刷新弹窗容量失败: {ex.Message}");
            if (!requestCts.IsCancellationRequested)
                dialog.SetResourceError(string.Format(Lang.DownloadPage_GetResourceFailed, ex.Message));
        }
        finally
        {
            Interlocked.CompareExchange(ref _dialogSizeRefreshCts, null, requestCts);
            requestCts.Dispose();
        }
    }

    private void CancelDialogSizeRefresh()
    {
        var cts = Interlocked.Exchange(ref _dialogSizeRefreshCts, null);
        cts?.Cancel();
    }

    /// <summary>
    /// 下载客户端：按版本拉取构建信息并开始安装下载（支持多语音）
    /// </summary>
    private async Task DownloadClientAsync(string tag, VoiceLanguageType voices = VoiceLanguageType.None)
    {
        _isPreparingDownload = true;
        try
        {
            UpdateGameState(GameState.Downloading);

            var buildResponse = await _apiService.GetBuildInfoAsync(
                string.IsNullOrEmpty(tag) ? null : tag);
            _buildDataList = buildResponse.Data?.Manifests ?? new List<BuildData>();
            foreach (var bd in _buildDataList)
                bd.Tag = buildResponse.Data?.Tag;

            if (_buildDataList.Count == 0)
            {
                ShowMessageAsync(Lang.DownloadPage_NoResourceInfo);
                RefreshGameStatus();
                return;
            }

            _selectedBuildData = FindGameResource(_buildDataList);
            if (!DownloadService.IsValidResource(_selectedBuildData))
            {
                ShowMessageAsync(Lang.DownloadPage_NoResourceInfo);
                return;
            }

            _selectedVoiceBuildData = null;
            UpdateVoicePackInfo();
            UpdateDownloadButtonStates();

            // 保存用户选择的游戏版本。
            string resolvedVersion = string.IsNullOrWhiteSpace(tag)
                ? (buildResponse.Data?.Tag ?? string.Empty)
                : tag;
            _lastSettings = AppSettings.Update(settings => settings.GameVersion = resolvedVersion);
            _requestedVersion = _lastSettings.GameVersion;
            ShowDownloadStartTip();

            // 收集勾选语言的语音包
            var voiceBuilds = new List<BuildData>();
            foreach (var lang in Enum.GetValues<VoiceLanguageType>())
            {
                if (lang == VoiceLanguageType.None || !voices.HasFlag(lang)) continue;
                var voice = _buildDataList.FirstOrDefault(b =>
                    b.MatchingField == ServerConfig.VoicePackages.GetMatchingField(lang));
                if (!DownloadService.IsValidResource(voice))
                {
                    ShowMessageAsync(string.Format(Lang.DownloadPage_GetResourceFailed,
                        ServerConfig.VoicePackages.GetDisplayName(lang)));
                    return;
                }
                voiceBuilds.Add(voice!);
            }

            var ds = DownloadService.Instance;
            ds.Initialize(_downloadPath);

            _isPreparingDownload = false;
            bool completed = await ds.StartDownloadAsync(_selectedBuildData, voiceBuilds, DispatcherQueue);
            if (completed)
            {
                if (GameLauncherService.IsValidInstallPath(_downloadPath, _currentRegion))
                {
                    string installedPath = Path.GetFullPath(_downloadPath);
                    _lastSettings = AppSettings.Update(settings => settings.GameInstallPath = installedPath);
                    _foundGamePath = _lastSettings.GameInstallPath;
                    GameInstallPathChangedMessage.Send();
                }
                else
                {
                    ShowMessageAsync(Lang.DownloadDialog_ClientMissingAfterDownload);
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Error("下载客户端失败", ex);
            ShowMessageAsync(string.Format(Lang.DownloadPage_GetResourceFailed, ex.Message));
        }
        finally
        {
            _isPreparingDownload = false;
            RefreshGameStatus();
        }
    }

    /// <summary>
    /// 消息通知气泡（长方形，右上角 X 关闭，无需确认键）
    /// </summary>
    private void ShowMessageAsync(string message)
    {
        var tip = new TeachingTip
        {
            XamlRoot = XamlRoot,
            Target = StartGameGrid,
            Title = message,
            PreferredPlacement = TeachingTipPlacementMode.Top,
            IsLightDismissEnabled = true,
        };
        tip.IsOpen = true;
    }

    private async Task RefreshBuildDataAsync(string? Tag = null, bool silentMode = false)
    {
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = new CancellationTokenSource();
        var ct = _refreshCts.Token;

        try
        {
            if (!string.IsNullOrEmpty(Tag))
                _requestedVersion = Tag;

            var ds = DownloadService.Instance;
            if (!silentMode && ds.IsDownloading)
                ds.CancelDownload();

            if (!silentMode)
            {
                _selectedBuildData = null;
                _selectedVoiceBuildData = null;
                UpdateDownloadButtonStates();
            }

            var buildResponse = await _apiService.GetBuildInfoAsync(Tag, ct);
            ct.ThrowIfCancellationRequested();

            _buildDataList = buildResponse.Data?.Manifests ?? new List<BuildData>();
            foreach (var bd in _buildDataList)
                bd.Tag = buildResponse.Data?.Tag;

            _selectedBuildData = FindGameResource(_buildDataList);
            _selectedVoiceBuildData = null;
            if (DownloadService.IsValidResource(_selectedBuildData))
            {
                UpdateVoicePackInfo();
                UpdateDownloadButtonStates();
            }
            else
            {
                _selectedBuildData = null;
                UpdateDownloadButtonStates();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Error("RefreshBuildData failed", ex);
            _selectedBuildData = null;
            _selectedVoiceBuildData = null;
            UpdateDownloadButtonStates();
        }
    }

    private void UpdateVoicePackInfo()
    {
        if (_currentVoiceLanguage == VoiceLanguageType.None)
        {
            _selectedVoiceBuildData = null;
            return;
        }
        var selectedLanguage = Enum.GetValues<VoiceLanguageType>()
            .FirstOrDefault(language => language != VoiceLanguageType.None
                                        && _currentVoiceLanguage.HasFlag(language));
        if (selectedLanguage == VoiceLanguageType.None)
        {
            _selectedVoiceBuildData = null;
            return;
        }

        string matchingField = ServerConfig.VoicePackages.GetMatchingField(selectedLanguage);
        _selectedVoiceBuildData = _buildDataList.FirstOrDefault(b => b.MatchingField == matchingField);
    }

    private static BuildData? FindGameResource(IEnumerable<BuildData> builds)
    {
        return builds.FirstOrDefault(build =>
                   string.Equals(build.MatchingField, ServerConfig.VoicePackages.Game,
                       StringComparison.OrdinalIgnoreCase))
               ?? builds.FirstOrDefault(build => string.IsNullOrWhiteSpace(build.MatchingField));
    }

    private bool HasPartialClientDownload()
    {
        return !string.IsNullOrWhiteSpace(_downloadPath)
               && DownloadService.Instance.HasPartialDownload(_downloadPath);
    }

    private static string GetDownloadStageText(DownloadService downloadService)
    {
        if (downloadService.IsPaused) return Lang.DownloadPage_Paused;

        return downloadService.CurrentStage switch
        {
            DownloadStage.Preparing => Lang.DownloadPage_Preparing,
            DownloadStage.CheckingFiles => Lang.DownloadPage_CheckingFiles,
            _ => Lang.DownloadPage_Downloading,
        };
    }

    // 鼠标是否悬停在胶囊上（悬停时不自动关闭气泡）
    #endregion
}
