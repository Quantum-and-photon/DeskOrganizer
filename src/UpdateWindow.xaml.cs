using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using DeskOrganizer.Model;

namespace DeskOrganizer;

public partial class UpdateWindow : Window
{
    private UpdateCheckResult? _result;
    private bool _isDownloading;
    private bool _isClosed;

    public UpdateWindow()
    {
        InitializeComponent();
        Closed += (_, _) => _isClosed = true;
    }

    /// <summary>启动时自动检查更新。</summary>
    public void CheckOnLoad()
    {
        if (_isClosed) return;
        ProgressBar.Visibility = Visibility.Visible;
        TitleText.Text = "正在检查更新...";
        VersionText.Text = $"当前版本: v{UpdateService.GetCurrentVersion()}";

        // 用 Task.Run + ContinueWith 替代 async void，避免混合 WPF/WinForms 环境下的异常路由问题
        Task.Run(async () => await UpdateService.CheckForUpdateAsync().ConfigureAwait(false))
            .ContinueWith(t =>
            {
                if (_isClosed) return;

                if (t.IsFaulted)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_isClosed) return;
                        TitleText.Text = "检查更新失败";
                        VersionText.Text = t.Exception?.GetBaseException().Message ?? "未知错误";
                        ProgressBar.Visibility = Visibility.Collapsed;
                    }));
                    return;
                }

                _result = t.Result;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_isClosed) return;
                    ShowResult();
                }));
            });
    }

    /// <summary>直接显示已有的检查结果（用于自动检查后直接展示）。</summary>
    public void ShowUpdateResult(UpdateCheckResult result)
    {
        _result = result;
        ShowResult();
    }

    /// <summary>根据检查结果显示 UI。</summary>
    private void ShowResult()
    {
        if (_result == null || _isClosed) return;

        if (!string.IsNullOrEmpty(_result.Error))
        {
            TitleText.Text = "检查更新失败";
            VersionText.Text = _result.Error;
            ProgressBar.Visibility = Visibility.Collapsed;
            return;
        }

        if (_result.HasUpdate)
        {
            TitleText.Text = "发现新版本!";
            VersionText.Text = $"当前: v{_result.CurrentVersion}  ->  最新: v{_result.LatestVersion}";

            var notes = _result.ReleaseNotes;
            if (string.IsNullOrEmpty(notes))
                notes = "暂无更新说明";
            if (!string.IsNullOrEmpty(_result.PublishedDate))
                notes = $"发布日期: {_result.PublishedDate}\n\n{notes}";
            ReleaseNotesText.Text = notes;

            ProgressBar.Visibility = Visibility.Collapsed;

            if (!string.IsNullOrEmpty(_result.DownloadUrl))
                DownloadButton.Visibility = Visibility.Visible;
            else
                OpenBrowserButton.Visibility = Visibility.Visible;
        }
        else
        {
            // 检查是否有待应用更新（静默下载已就绪）
            if (UpdateService.HasPendingUpdate())
            {
                var pendingVer = ConfigService.Instance.Config.PendingUpdateVersion;
                TitleText.Text = "更新已就绪!";
                VersionText.Text = $"待应用版本: v{pendingVer}";
                ReleaseNotesText.Text = $"新版本 v{pendingVer} 已下载完成。\n关闭程序后将自动应用更新，下次启动即为新版本。";
                ProgressBar.Visibility = Visibility.Collapsed;
            }
            else
            {
                TitleText.Text = "已是最新版本";
                VersionText.Text = $"当前版本: v{_result.CurrentVersion}";
                ReleaseNotesText.Text = "你的软件已是最新版本，无需更新。";
                ProgressBar.Visibility = Visibility.Collapsed;
            }
        }
    }

    /// <summary>立即更新按钮：下载到暂存目录并重启应用。</summary>
    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_result == null || _isDownloading) return;

        _isDownloading = true;
        DownloadButton.IsEnabled = false;
        DownloadButton.Content = "下载中...";
        ProgressBar.Visibility = Visibility.Visible;
        ProgressBar.IsIndeterminate = false;
        ProgressText.Visibility = Visibility.Visible;

        try
        {
            var progress = new Progress<(long received, long total)>(p =>
            {
                if (_isClosed) return;
                if (p.total > 0)
                {
                    var pct = (int)(p.received * 100 / p.total);
                    ProgressBar.Value = pct;
                    var recvMB = p.received / 1024.0 / 1024.0;
                    var totalMB = p.total / 1024.0 / 1024.0;
                    ProgressText.Text = $"{recvMB:F1} / {totalMB:F1} MB ({pct}%)";
                }
                else
                {
                    ProgressBar.IsIndeterminate = true;
                    ProgressText.Text = $"{p.received / 1024.0 / 1024.0:F1} MB";
                }
            });

            // 下载到暂存目录（%APPDATA%\DeskOrganizer\update\）
            var stagedPath = await UpdateService.DownloadToUpdateAsync(
                _result.DownloadUrl, _result.LatestVersion, progress);

            if (_isClosed) return;

            // 下载完成，弹出确认提示
            ProgressText.Text = "下载完成";
            ProgressBar.Visibility = Visibility.Collapsed;

            var result = System.Windows.MessageBox.Show(
                "更新已下载完成，是否立即重启并应用更新？\n\n选择\"否\"将在下次关闭程序时自动应用更新。",
                "更新就绪",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
            {
                // 用户选择稍后，暂存文件已保留，退出时自动应用
                DownloadButton.IsEnabled = true;
                DownloadButton.Content = "立即重启更新";
                _isDownloading = false;
                return;
            }

            // 用户确认立即更新：生成批处理脚本，退出后替换 exe 并重启
            this.Close();

            UpdateService.ApplyUpdateNow(stagedPath);

            // 清理资源并退出主程序
            if (Application.Current.MainWindow is MainWindow mainWin)
            {
                mainWin.PrepareForExit();
            }

            // 释放单实例 Mutex，避免更新脚本启动的新进程被误判为重复实例而静默退出
            (Application.Current as App)?.ReleaseSingleInstanceMutex();

            App.Log("[UpdateWindow] Exiting process for update...");
            // 给更新脚本足够时间启动（UseShellExecute=true 已创建独立进程，但仍需确保 cmd.exe 就绪）
            await Task.Delay(800);
            System.Environment.Exit(0);
        }
        catch (Exception ex)
        {
            if (_isClosed) return;
            System.Windows.MessageBox.Show($"下载更新失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            DownloadButton.IsEnabled = true;
            DownloadButton.Content = "立即更新";
            ProgressBar.Visibility = Visibility.Collapsed;
            ProgressText.Visibility = Visibility.Collapsed;
            _isDownloading = false;
        }
    }

    /// <summary>前往下载按钮：在浏览器中打开 Release 页面。</summary>
    private void OpenBrowserButton_Click(object sender, RoutedEventArgs e)
    {
        if (_result != null && !string.IsNullOrEmpty(_result.HtmlUrl))
        {
            Process.Start(new ProcessStartInfo(_result.HtmlUrl) { UseShellExecute = true });
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
