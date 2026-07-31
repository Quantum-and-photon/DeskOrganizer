using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using DeskOrganizer.Model;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;

namespace DeskOrganizer;

public partial class SearchWindow : Window
{
    private CancellationTokenSource? _indexCts;
    private readonly object _searchLock = new();
    private DispatcherTimer? _debounceTimer;

    public SearchWindow()
    {
        InitializeComponent();

        // Center on screen
        var screen = SystemParameters.WorkArea;
        Left = (screen.Width - Width) / 2 + screen.Left;
        Top = (screen.Height - Height) / 2 + screen.Top;

        // Subscribe to index progress
        SearchService.Instance.IndexProgressChanged += OnIndexProgressChanged;

        // Start initial indexing if needed
        StartIndexing();
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        Hide();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    // ---- Search ----

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var keyword = SearchBox.Text.Trim();

        // Toggle placeholder visibility
        PlaceholderText.Visibility = string.IsNullOrEmpty(keyword)
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (string.IsNullOrEmpty(keyword))
        {
            ResultsList.ItemsSource = null;
            StatusText.Text = "Alt+Space 打开 | 输入关键词搜索 | Enter 打开 | Esc 关闭";
            _debounceTimer?.Stop();
            return;
        }

        // 防抖：200ms 延迟后执行搜索
        _debounceTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _debounceTimer.Stop();
        _debounceTimer.Tick -= DebounceSearch_Tick;
        _debounceTimer.Tick += DebounceSearch_Tick;
        _debounceTimer.Tag = keyword;
        _debounceTimer.Start();
    }

    private void DebounceSearch_Tick(object? sender, EventArgs e)
    {
        _debounceTimer?.Stop();
        var keyword = _debounceTimer?.Tag as string;
        if (string.IsNullOrEmpty(keyword))
            return;

        // Throttled search
        lock (_searchLock)
        {
            var results = SearchService.Instance.Search(keyword);
            DisplayResults(results);
        }
    }

    private void DisplayResults(List<SearchResult> results)
    {
        var items = results.Select(r => new SearchResultItem(r)).ToList();

        Dispatcher.Invoke(() =>
        {
            ResultsList.ItemsSource = items;
            StatusText.Text = items.Count > 0
                ? $"找到 {items.Count} 个结果 (共索引 {SearchService.Instance.IndexedCount} 个文件)"
                : "未找到匹配结果";
        });
    }

    // ---- Keyboard Navigation ----

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                if (ResultsList.Items.Count > 0)
                {
                    ResultsList.SelectedIndex = 0;
                    ResultsList.Focus();
                    e.Handled = true;
                }
                break;

            case Key.Enter:
                OpenSelectedResult();
                e.Handled = true;
                break;
        }
    }

    private void ResultsList_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Up:
                if (ResultsList.SelectedIndex <= 0)
                {
                    SearchBox.Focus();
                    ResultsList.SelectedIndex = -1;
                    e.Handled = true;
                }
                break;

            case Key.Enter:
                OpenSelectedResult();
                e.Handled = true;
                break;

            case Key.Back:
                SearchBox.Focus();
                e.Handled = true;
                break;
        }
    }

    private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        OpenSelectedResult();
    }

    private void OpenSelectedResult()
    {
        if (ResultsList.SelectedItem is SearchResultItem item)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = item.FilePath,
                    UseShellExecute = true
                });
                Hide();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"无法打开文件: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    // ---- Indexing ----

    private void StartIndexing()
    {
        var paths = GetIndexPaths();
        if (paths.Length == 0) return;

        _indexCts = new CancellationTokenSource();
        // 使用配置的索引上限
        SearchService.Instance.MaxIndexedFiles = ConfigService.Instance.Config.SearchIndexLimit;
        _ = SearchService.Instance.BuildIndexAsync(paths, _indexCts.Token);
    }

    private static string[] GetIndexPaths()
    {
        var paths = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        // 加入围栏存储目录，确保围栏内文件可被搜索
        var fenceStorage = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DeskOrganizer", "FenceStorage");
        if (System.IO.Directory.Exists(fenceStorage))
            paths.Add(fenceStorage);
        return paths.Where(Directory.Exists).ToArray();
    }

    private void OnIndexProgressChanged(object? sender, IndexProgressEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            ProgressBar.Visibility = e.Status == IndexStatus.Idle
                ? Visibility.Collapsed
                : Visibility.Visible;

            ProgressText.Text = e.Status switch
            {
                IndexStatus.Indexing => $"正在索引... ({e.CurrentCount}/{e.TotalFiles})",
                IndexStatus.Complete => $"索引完成 ({e.TotalFiles} 个文件)",
                IndexStatus.Stopped => "索引已停止",
                _ => "准备就绪"
            };

            if (e.TotalFiles > 0)
            {
                ProgressBar.Value = (double)e.CurrentCount / e.TotalFiles * 100;
            }
        });
    }

    // ---- Public Methods ----

    public void FocusSearchBox()
    {
        SearchBox.Text = string.Empty;
        SearchBox.Focus();
    }

    protected override void OnClosed(EventArgs e)
    {
        _indexCts?.Cancel();
        _indexCts?.Dispose();
        _debounceTimer?.Stop();
        SearchService.Instance.IndexProgressChanged -= OnIndexProgressChanged;
        base.OnClosed(e);
    }
}
