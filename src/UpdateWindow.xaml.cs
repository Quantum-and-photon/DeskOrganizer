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
            TitleText.Text = "已是最新版本";
            VersionText.Text = $"当前版本: v{_result.CurrentVersion}";
            ReleaseNotesText.Text = "你的软件已是最新版本，无需更新。";
            ProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>立即更新按钮：下载并应用更新。</summary>
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

            var downloadedPath = await UpdateService.DownloadUpdateAsync(_result.DownloadUrl, progress);

            if (_isClosed) return;

            // 下载完成，应用更新
            ProgressText.Text = "正在安装更新...";
            // 使用 ProcessPath 获取实际 exe 目录，BaseDirectory 在单文件发布时可能返回临时解压目录
            var exePath = Environment.ProcessPath ?? AppDomain.CurrentDomain.BaseDirectory + "DeskOrganizer_v2.exe";
            var targetDir = System.IO.Path.GetDirectoryName(exePath)!;

            // 先关闭更新窗口，避免阻塞退出流程
            this.Close();

            // 启动更新脚本（detached 模式，父进程退出后子进程继续运行）
            UpdateService.ApplyUpdate(downloadedPath, targetDir);

            // 确保完整清理后退出（IPC 端口、托盘图标等）
            // 用 Environment.Exit 强制退出，避免 Shutdown 阻塞导致 BAT 脚本无法执行
            if (Application.Current.MainWindow is MainWindow mainWin)
            {
                // 先清理资源，再退出
                mainWin.PrepareForExit();
            }

            // 给 BAT 脚本一点时间启动，然后强制退出
            await Task.Delay(500);
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
