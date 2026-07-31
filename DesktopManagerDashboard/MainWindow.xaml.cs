using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace DesktopManager;

public partial class MainWindow : Window
{
    private bool _startMinimized;
    private bool _isClosing;
    private bool _isFullyLoaded;
    private DispatcherTimer? _signalTimer;

    // 信号文件路径（主程序写入此文件来唤起 Dashboard）
    private static readonly string SignalPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DeskOrganizer_v2", "show_dashboard.signal");

    // 窗口配置文件路径
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DeskOrganizer_v2", "dashboard_config.json");

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
    }

    /// <summary>设置启动参数（由 App.OnStartup 传入）。</summary>
    public void SetStartupArgs(bool minimized)
    {
        _startMinimized = minimized;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 从配置文件恢复窗口尺寸和位置
        LoadWindowSettings();

        // 如果配置文件不存在，创建默认配置
        if (!File.Exists(ConfigPath))
        {
            _startMinimized = true; // 首次启动默认最小化
            SaveWindowSettings();
        }

        // 启动最小化：不显示窗口
        if (_startMinimized)
        {
            ShowInTaskbar = false;
        }
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        // 窗口渲染完成后，如果需要最小化则隐藏
        if (_startMinimized && WindowState != WindowState.Minimized)
        {
            WindowState = WindowState.Minimized;
            ShowInTaskbar = false;
            Hide();
        }
        _isFullyLoaded = true;

        // 启动信号文件轮询（每 500ms 检查一次主程序是否发来显示信号）
        _signalTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _signalTimer.Tick += CheckShowSignal;
        _signalTimer.Start();
    }

    /// <summary>检查信号文件，如果存在则显示窗口并删除信号。</summary>
    private void CheckShowSignal(object? sender, EventArgs e)
    {
        try
        {
            if (File.Exists(SignalPath))
            {
                File.Delete(SignalPath);
                ShowDashboard();
            }
        }
        catch { }
    }

    private void LoadWindowSettings()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<WindowConfig>(json);
                if (cfg != null)
                {
                    var screen = SystemParameters.WorkArea;
                    if (cfg.X >= 0 && cfg.Y >= 0 && cfg.X < screen.Width && cfg.Y < screen.Height)
                    {
                        Left = cfg.X;
                        Top = cfg.Y;
                    }
                    else
                    {
                        Left = (screen.Width - Width) / 2;
                        Top = (screen.Height - Height) / 2;
                    }
                    Width = Math.Max(cfg.Width, MinWidth);
                    Height = Math.Max(cfg.Height, MinHeight);
                    if (cfg.Maximized)
                        WindowState = WindowState.Maximized;
                }
            }
            else
            {
                var screen = SystemParameters.WorkArea;
                Left = (screen.Width - Width) / 2;
                Top = (screen.Height - Height) / 2;
            }
        }
        catch (Exception)
        {
            var screen = SystemParameters.WorkArea;
            Left = (screen.Width - Width) / 2;
            Top = (screen.Height - Height) / 2;
        }
    }

    private void SaveWindowSettings()
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            double saveW = Width;
            double saveH = Height;
            if (WindowState == WindowState.Normal && _isFullyLoaded)
            {
                saveW = ActualWidth > 0 ? ActualWidth : Width;
                saveH = ActualHeight > 0 ? ActualHeight : Height;
            }

            var cfg = new WindowConfig
            {
                X = RestoreBounds.X > 0 ? RestoreBounds.X : Left,
                Y = RestoreBounds.Y > 0 ? RestoreBounds.Y : Top,
                Width = saveW,
                Height = saveH,
                Maximized = WindowState == WindowState.Maximized,
                StartMinimized = _startMinimized
            };
            var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
        }
        catch (Exception) { }
    }

    /// <summary>显示窗口（供外部信号调用）。</summary>
    public void ShowDashboard()
    {
        WindowState = WindowState.Normal;
        ShowInTaskbar = true;
        Visibility = Visibility.Visible;
        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    /// <summary>隐藏窗口（供外部信号调用）。</summary>
    public void HideDashboard()
    {
        if (WindowState == WindowState.Normal)
            SaveWindowSettings();
        WindowState = WindowState.Minimized;
        ShowInTaskbar = false;
        Hide();
    }

    /// <summary>切换"启动时最小化"设置。</summary>
    public void SetStartMinimized(bool value)
    {
        _startMinimized = value;
        SaveWindowSettings();
    }

    /// <summary>获取启动最小化状态。</summary>
    public bool GetStartMinimized() => _startMinimized;

    /// <summary>检测主程序是否运行，如果没有则自动启动（异步，不阻塞 UI）。</summary>
    private async Task EnsureMainAppRunningAsync()
    {
        try
        {
            if (IsIpcAlive())
                return;

            // 检查主程序进程是否已在运行（避免重复启动）
            var existing = System.Diagnostics.Process.GetProcessesByName("DeskOrganizer_v2");
            if (existing.Length > 0)
            {
                for (int i = 0; i < 20; i++)
                {
                    await Task.Delay(500);
                    if (IsIpcAlive())
                        return;
                }
                return;
            }

            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(baseDir, "DeskOrganizer_v2.exe"),
                Path.Combine(baseDir, "..", "..", "..", "..", "..", "bin", "Release", "net8.0-windows", "DeskOrganizer_v2.exe"),
                Path.Combine(baseDir, "..", "..", "..", "..", "..", "bin", "Debug", "net8.0-windows", "DeskOrganizer_v2.exe"),
                Path.Combine(baseDir, "..", "..", "..", "..", "bin", "Release", "net8.0-windows", "DeskOrganizer_v2.exe"),
                Path.Combine(baseDir, "..", "..", "..", "..", "bin", "Debug", "net8.0-windows", "DeskOrganizer_v2.exe"),
            };

            string? mainExe = null;
            foreach (var p in candidates)
            {
                var full = Path.GetFullPath(p);
                if (File.Exists(full))
                {
                    mainExe = full;
                    break;
                }
            }

            if (mainExe != null)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = mainExe,
                    UseShellExecute = true,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
                });

                for (int i = 0; i < 20; i++)
                {
                    await Task.Delay(500);
                    if (IsIpcAlive())
                        return;
                }
            }
        }
        catch (Exception) { }
    }

    /// <summary>检查 IPC 服务是否在线。</summary>
    private static bool IsIpcAlive()
    {
        try
        {
            var req = System.Net.WebRequest.Create("http://localhost:19600/status/");
            req.Timeout = 1500;
            using var resp = req.GetResponse();
            return true;
        }
        catch { return false; }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await EnsureMainAppRunningAsync();
            await webView.EnsureCoreWebView2Async(null);

            var htmlDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dashboard");
            var indexPath = Path.Combine(htmlDir, "pages", "dashboard.html");

            if (File.Exists(indexPath))
            {
                var uri = new Uri(indexPath, UriKind.Absolute).AbsoluteUri;
                webView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
                webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
                webView.CoreWebView2.Navigate(uri);
            }
            else
            {
                var html = $@"
                <html>
                <body style='background:#f5f0eb;font-family:sans-serif;display:flex;align-items:center;justify-content:center;height:100vh;margin:0;'>
                <div style='text-align:center;color:#7a756e;'>
                <h2 style='color:#1a1614;font-size:24px;margin-bottom:12px;'>Dashboard 文件未找到</h2>
                <p style='font-size:14px;'>请将 dashboard/ 目录复制到程序所在目录下</p>
                <p style='font-size:12px;margin-top:8px;color:#a39e97;'>期望路径: {indexPath}</p>
                </div>
                </body>
                </html>";
                webView.CoreWebView2.NavigateToString(html);
            }

            // 页面加载完成后，检查是否有遗留的显示信号（主程序启动 Dashboard 时可能已写入）
            await Task.Delay(1000);
            CheckShowSignal(null, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"WebView2 初始化失败: {ex.Message}\n\n请确保已安装 Microsoft Edge WebView2 Runtime。",
                "桌面管理", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    private void WebView_NavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (e.Uri.StartsWith("https://") || e.Uri.StartsWith("http://"))
        {
            e.Cancel = true;
        }
    }

    /// <summary>页面加载完成后注入 IPC 代理脚本（绕过 CORS）。</summary>
    private void CoreWebView2_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            webView.CoreWebView2.ExecuteScriptAsync(@"
                window.__ipcResults = {};
                window.__ipcId = 0;
                window.__ipcResolvers = {};

                // 监听宿主返回的消息
                window.chrome.webview.addEventListener('message', function(e) {
                    try {
                        var data = JSON.parse(e.data);
                        if (data.id && window.__ipcResolvers[data.id]) {
                            window.__ipcResolvers[data.id](data.result || {});
                            delete window.__ipcResolvers[data.id];
                        }
                    } catch(ex) {}
                });

                // 重写 fetch，通过 WebView2 宿主代理请求
                window.__originalFetch = window.fetch;
                window.fetch = async function(url, options) {
                    // 只代理 localhost:19600 的请求
                    if (typeof url === 'string' && url.includes('localhost:19600')) {
                        const id = ++window.__ipcId;
                        const msg = JSON.stringify({
                            id: id,
                            url: url,
                            method: options?.method || 'GET',
                            body: options?.body || null
                        });
                        // 发送到宿主
                        window.chrome.webview.postMessage(msg);
                        // 等待结果（用 Promise + 事件监听，不用 setInterval）
                        return new Promise((resolve) => {
                            window.__ipcResolvers[id] = function(result) {
                                resolve({
                                    ok: true,
                                    status: 200,
                                    json: async () => result,
                                    text: async () => JSON.stringify(result)
                                });
                            };
                        });
                    }
                    return window.__originalFetch.apply(this, arguments);
                };
            ");
        }
    }

    /// <summary>接收页面发来的 IPC 请求并代理转发。</summary>
    private void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var msg = e.TryGetWebMessageAsString();
            if (string.IsNullOrEmpty(msg)) return;

            var req = System.Text.Json.JsonDocument.Parse(msg);
            var id = req.RootElement.GetProperty("id").GetInt32();
            var url = req.RootElement.GetProperty("url").GetString()!;
            var method = req.RootElement.GetProperty("method").GetString() ?? "GET";

            // 在后台线程执行 HTTP 请求
            System.Threading.Tasks.Task.Run(async () =>
            {
                string result;
                try
                {
                    var httpReq = System.Net.WebRequest.Create(url);
                    httpReq.Method = method;
                    httpReq.Timeout = 5000;

                    if (method == "POST" && req.RootElement.TryGetProperty("body", out var bodyElem) && bodyElem.ValueKind != System.Text.Json.JsonValueKind.Null)
                    {
                        var body = bodyElem.GetString()!;
                        httpReq.ContentType = "application/json";
                        var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                        httpReq.ContentLength = bytes.Length;
                        using var stream = await httpReq.GetRequestStreamAsync();
                        await stream.WriteAsync(bytes);
                    }

                    using var resp = await httpReq.GetResponseAsync();
                    using var reader = new System.IO.StreamReader(resp.GetResponseStream());
                    result = await reader.ReadToEndAsync();
                }
                catch (Exception ex)
                {
                    result = $$"""{"ok":false,"error":"{{ex.Message.Replace("\"", "\\\"")}}"}""";
                }

                // 返回结果给页面（用 postMessage 而非 ExecuteScriptAsync，避免页面重新布局）
                await Dispatcher.InvokeAsync(() =>
                {
                    var msg = $"{{\"id\":{id},\"result\":{result}}}";
                    webView.CoreWebView2.PostWebMessageAsString(msg);
                });
            });
        }
        catch { }
    }

    private void Window_StateChanged(object sender, EventArgs e)
    {
        // 用户点击最小化按钮时隐藏窗口
        if (WindowState == WindowState.Minimized && _isFullyLoaded)
        {
            ShowInTaskbar = false;
            Hide();
        }
    }

    private void Window_LocationChanged(object sender, EventArgs e)
    {
        if (WindowState == WindowState.Normal && _isFullyLoaded)
        {
            SaveWindowSettings();
        }
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (WindowState == WindowState.Normal && _isFullyLoaded)
        {
            SaveWindowSettings();
        }
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isClosing)
        {
            // 点击关闭按钮时隐藏窗口，而不是退出
            e.Cancel = true;
            HideDashboard();
            return;
        }

        SaveWindowSettings();
        _signalTimer?.Stop();
    }
}

/// <summary>窗口配置（尺寸/位置/启动行为）。</summary>
internal class WindowConfig
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 1400;
    public double Height { get; set; } = 900;
    public bool Maximized { get; set; }
    public bool StartMinimized { get; set; }
}
