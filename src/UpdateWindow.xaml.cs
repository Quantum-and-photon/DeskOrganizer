using System;
using System.Diagnostics;
using System.Windows;
using DeskOrganizer.Model;

namespace DeskOrganizer;

public partial class UpdateWindow : Window
{
    private UpdateCheckResult? _result;
    private bool _isDownloading;

    public UpdateWindow()
    {
        InitializeComponent();
    }

    /// <summary>启动时自动检查更新。</summary>
    public async void CheckOnLoad()
    {
        ProgressBar.Visibility = Visibility.Visible;
        TitleText.Text = "正在检查更新...";
        VersionText.Text = $"当前版本: v{UpdateService.GetCurrentVersion()}";

        try
        {
            _result = await UpdateService.CheckForUpdateAsync();
            ShowResult();
        }
        catch (Exception ex)
        {
            TitleText.Text = "检查更新失败";
            VersionText.Text = ex.Message;
            ProgressBar.Visibility = Visibility.Collapsed;
        }
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
        if (_result == null) return;

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

            // 下载完成，应用更新
            ProgressText.Text = "正在安装更新...";
            var targetDir = AppDomain.CurrentDomain.BaseDirectory;
            UpdateService.ApplyUpdate(downloadedPath, targetDir);

            // 退出程序，让更新脚本完成替换
            System.Windows.MessageBox.Show(
                "更新已下载完成，程序将关闭并自动更新。\n更新完成后程序会自动重启。",
                "更新中", MessageBoxButton.OK, MessageBoxImage.Information);

            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
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
