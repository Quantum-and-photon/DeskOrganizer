using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using DeskOrganizer.Model;
using DeskOrganizer.Win32;
using NotifyIcon = System.Windows.Forms.NotifyIcon;
using App = DeskOrganizer.App;
using Win32ModifierKeys = DeskOrganizer.Win32.ModifierKeys;

namespace DeskOrganizer;

public partial class MainWindow : Window
{
    public static MainWindow? Instance { get; private set; }
    private NotifyIcon? _notifyIcon;
    private SearchWindow? _searchWindow;
    private SettingsWindow? _settingsWindow;
    private readonly List<StickyNoteWindow> _stickyNotes = new();
    private readonly object _stickyNotesLock = new();

    // 活动日志（线程安全）
    private readonly List<(DateTime Time, string Type, string Message)> _activityLog = new();
    private readonly object _activityLogLock = new();
    private void LogActivity(string type, string message)
    {
        lock (_activityLogLock)
        {
            _activityLog.Add((DateTime.Now, type, message));
            if (_activityLog.Count > 200) _activityLog.RemoveAt(0);
        }
    }

    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID = 1;
    private const uint VK_SPACE = 0x20;

    private IntPtr _hwnd;
    private bool _hotKeyRegistered;
    private int _currentDesktopIndex = 1; // 当前虚拟桌面索引（1-based）
    private Win32MessageWindow? _msgWindow;
    private HttpListener? _ipcServer;
    private Thread? _ipcThread;
    private const string IPC_PREFIX = "http://localhost:19600/cmd/";
    private const string IPC_STATUS_PREFIX = "http://localhost:19600/status/";
    private const string IPC_FENCE_LIST_PREFIX = "http://localhost:19600/fence-list/";
    private const string IPC_DESKTOP_ITEMS_PREFIX = "http://localhost:19600/desktop-items/";

    public MainWindow()
    {
        InitializeComponent();
        App.CurrentDesktopIndex = _currentDesktopIndex;
    }

    public void InitializeApplication()
    {
        Instance = this;
        // 使用 WinForms NativeWindow 创建消息窗口（不依赖 WPF 窗口句柄）
        _msgWindow = new Win32MessageWindow();
        _msgWindow.HotkeyReceived += OnHotkeyReceived;
        _hwnd = _msgWindow.Handle;
        App.Log($"Message window handle created: {_hwnd}");

        App.Log("Initializing NotifyIcon...");
        InitializeNotifyIcon();
        App.Log("Registering hotkeys...");
        RegisterHotKeys();

        App.Log("About to load fences...");
        try
        {
            // Load fences from config
            FenceManager.Instance.LoadFences(ConfigService.Instance.Config);
            App.Log($"Fences loaded: {FenceManager.Instance.ActiveFenceCount}");

            // 只显示当前桌面（默认桌面1）的围栏
            FenceManager.Instance.ShowFencesForDesktop(_currentDesktopIndex);
        }
        catch (Exception ex)
        {
            App.Log($"LoadFences CRASHED: {ex.GetType().Name}: {ex.Message}");
        }

        App.Log("About to load sticky notes...");
        try
        {
            // Load sticky notes
            LoadStickyNotes();
            App.Log($"Sticky notes loaded: {GetStickyNoteCount()}");
        }
        catch (Exception ex)
        {
            App.Log($"LoadStickyNotes CRASHED: {ex.GetType().Name}: {ex.Message}");
        }

        App.Log("Application initialized successfully.");
        LogActivity("system", "应用启动");

        // 启动 IPC HTTP 服务（供 Dashboard 调用）
        StartIpcServer();

        // 启动后延迟自动检查更新（异步，不阻塞启动）
        Dispatcher.BeginInvoke(new Action(() =>
        {
            System.Windows.Threading.DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(5) };
            timer.Tick += (_, _) => { timer.Stop(); AutoCheckUpdateOnStartup(); };
            timer.Start();
        }));
    }

    // ---- NotifyIcon ----

    private void InitializeNotifyIcon()
    {
        if (_notifyIcon != null) return; // 防止重复创建

        // 确保退出时清理托盘图标
        Closed += (_, _) =>
        {
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Icon?.Dispose();
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
        };
        // 加载自定义图标（用 ProcessPath 获取 exe 实际目录，单文件发布时 BaseDirectory 可能是临时目录）
        System.Drawing.Icon? appIcon = null;
        var exeDir = string.IsNullOrEmpty(Environment.ProcessPath)
            ? AppDomain.CurrentDomain.BaseDirectory
            : System.IO.Path.GetDirectoryName(Environment.ProcessPath)!;
        var icoPath = System.IO.Path.Combine(exeDir, "app.ico");
        if (System.IO.File.Exists(icoPath))
        {
            try { appIcon = new System.Drawing.Icon(icoPath); } catch { }
        }
        // 回退：尝试从 exe 本身提取图标
        appIcon ??= System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "");
        appIcon ??= System.Drawing.SystemIcons.Application;

        _notifyIcon = new NotifyIcon
        {
            Text = "桌面管理",
            Visible = true,
            Icon = appIcon
        };

        _notifyIcon.DoubleClick += (_, _) => ShowDashboard();

        _notifyIcon.ContextMenuStrip = new System.Windows.Forms.ContextMenuStrip();
        _notifyIcon.ContextMenuStrip.Items.Add(new System.Windows.Forms.ToolStripMenuItem("新建围栏", null, (_, _) => CreateNewFence()));
        _notifyIcon.ContextMenuStrip.Items.Add(new System.Windows.Forms.ToolStripMenuItem("新建便签", null, (_, _) => CreateNewStickyNote()));
        _notifyIcon.ContextMenuStrip.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _notifyIcon.ContextMenuStrip.Items.Add(new System.Windows.Forms.ToolStripMenuItem("显示 Dashboard", null, (_, _) => ShowDashboard()));
        _notifyIcon.ContextMenuStrip.Items.Add(new System.Windows.Forms.ToolStripMenuItem("搜索文件", null, (_, _) => ShowSearchWindow()));
        _notifyIcon.ContextMenuStrip.Items.Add(new System.Windows.Forms.ToolStripMenuItem("设置", null, (_, _) => ShowSettingsWindow()));
        _notifyIcon.ContextMenuStrip.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _notifyIcon.ContextMenuStrip.Items.Add(new System.Windows.Forms.ToolStripMenuItem("显示所有围栏", null, (_, _) => { FenceManager.Instance.ShowAllFences(); }));
        _notifyIcon.ContextMenuStrip.Items.Add(new System.Windows.Forms.ToolStripMenuItem("隐藏所有围栏", null, (_, _) => { FenceManager.Instance.HideAllFences(); }));
        _notifyIcon.ContextMenuStrip.Items.Add(new System.Windows.Forms.ToolStripMenuItem("自动排布围栏", null, (_, _) => { FenceManager.Instance.AutoArrangeFences(); }));
        _notifyIcon.ContextMenuStrip.Items.Add(new System.Windows.Forms.ToolStripMenuItem("自动排布便签", null, (_, _) => { AutoArrangeStickyNotes(); }));
        _notifyIcon.ContextMenuStrip.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _notifyIcon.ContextMenuStrip.Items.Add(new System.Windows.Forms.ToolStripMenuItem("一键整理桌面快捷方式", null, (_, _) => { OrganizeDesktopShortcuts(); }));
        _notifyIcon.ContextMenuStrip.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _notifyIcon.ContextMenuStrip.Items.Add(new System.Windows.Forms.ToolStripMenuItem("检查更新", null, (_, _) => { CheckForUpdate(); }));
        _notifyIcon.ContextMenuStrip.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _notifyIcon.ContextMenuStrip.Items.Add(new System.Windows.Forms.ToolStripMenuItem("退出", null, (_, _) => ExitApplication()));
    }

    // ---- Hotkeys ----

    private void RegisterHotKeys()
    {
        // Must be called after window handle is available (OnSourceInitialized)
        // _hwnd is set there; if not yet, we retry via Loaded event
        if (_hwnd != IntPtr.Zero)
        {
            try
            {
                // 从配置读取热键，默认 Alt+Space
                var cfg = ConfigService.Instance.Config;
                var mod = (Win32ModifierKeys)(cfg.SearchHotkeyModifiers > 0 ? cfg.SearchHotkeyModifiers : (int)Win32ModifierKeys.Alt);
                var key = (uint)(cfg.SearchHotkeyKey > 0 ? cfg.SearchHotkeyKey : (int)VK_SPACE);
                _hotKeyRegistered = Win32Helper.RegisterGlobalHotKey(_hwnd, HOTKEY_ID, mod, key);
                if (!_hotKeyRegistered)
                {
                    App.Log($"搜索热键 ({mod}+VK_{key:X2}) 注册失败，可能被其他程序占用");
                    // 延迟提示，避免阻塞启动流程；仅首次提示
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        System.Windows.MessageBox.Show(
                            $"搜索热键 ({mod}+VK_{key:X2}) 注册失败，可能被其他程序占用。\n你仍可通过托盘菜单 \"搜索文件\" 使用搜索功能。\n可在设置中更换热键。",
                            "热键提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }));
                }
            }
            catch (Exception ex)
            {
                _hotKeyRegistered = false;
                App.Log($"RegisterHotKeys exception: {ex.Message}");
            }
        }
    }

    private void UnregisterHotKeys()
    {
        if (_hwnd != IntPtr.Zero && _hotKeyRegistered)
        {
            try
            {
                Win32Helper.UnregisterGlobalHotKey(_hwnd, HOTKEY_ID);
                _hotKeyRegistered = false;
            }
            catch
            {
                // Ignore unregister errors
            }
        }
    }

    private void OnHotkeyReceived()
    {
        ShowSearchWindow();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // WPF 窗口句柄现在仅用于 WPF 内部，热键已由 _msgWindow 处理
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 热键由 NativeWindow (_msgWindow) 处理，此处保留 WndProc 供其他消息使用
        return IntPtr.Zero;
    }

    // ---- Search Window ----

    private void ShowSearchWindow()
    {
        if (_searchWindow == null || !_searchWindow.IsLoaded)
        {
            _searchWindow = new SearchWindow();
            _searchWindow.Closed += (_, _) => _searchWindow = null;
        }

        _searchWindow.Show();
        _searchWindow.Activate();
        _searchWindow.FocusSearchBox();
    }

    // ---- Settings Window ----

    private void ShowSettingsWindow()
    {
        if (_settingsWindow == null || !_settingsWindow.IsLoaded)
        {
            _settingsWindow = new SettingsWindow();
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }

        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    // ---- Update ----

    /// <summary>手动检查更新。通过 Dispatcher 确保在 WPF UI 线程执行。</summary>
    private void CheckForUpdate()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var updateWindow = new UpdateWindow();
            // MainWindow 是隐藏窗口，不能作为 Owner（会抛 InvalidOperationException）
            updateWindow.CheckOnLoad();
            updateWindow.ShowDialog();
        }));
    }

    /// <summary>启动时自动检查更新（静默，仅在有新版本时弹窗提示）。</summary>
    private void AutoCheckUpdateOnStartup()
    {
        try
        {
            var config = ConfigService.Instance.Config;
            App.Log($"[MainWindow] AutoCheckUpdate: enabled={config.AutoCheckUpdate}, lastCheck={config.LastUpdateCheck}, currentVer={Model.UpdateService.GetCurrentVersion()}");

            if (!config.AutoCheckUpdate) return;

            // 24 小时内只检查一次
            if (config.LastUpdateCheck != DateTime.MinValue &&
                (DateTime.Now - config.LastUpdateCheck).TotalHours < 24)
            {
                App.Log("[MainWindow] Skip update check: checked within 24h");
                return;
            }

            config.LastUpdateCheck = DateTime.Now;
            ConfigService.Instance.Save();

            App.Log("[MainWindow] Checking for updates...");
            // 用 Task.Run 避免混合 WPF/WinForms 环境下的 async void 异常路由问题
            Task.Run(async () => await Model.UpdateService.CheckForUpdateAsync().ConfigureAwait(false))
                .ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        App.Log($"[MainWindow] AutoCheckUpdate failed: {t.Exception?.GetBaseException().Message}");
                        return;
                    }

                    var result = t.Result;
                    App.Log($"[MainWindow] AutoCheckUpdate result: hasUpdate={result.HasUpdate}, error={result.Error}");

                    if (result.HasUpdate && string.IsNullOrEmpty(result.Error))
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                var msg = $"发现新版本 v{result.LatestVersion}!\n\n当前版本: v{result.CurrentVersion}\n\n";
                                if (!string.IsNullOrEmpty(result.ReleaseNotes))
                                    msg += result.ReleaseNotes + "\n\n";
                                if (!string.IsNullOrEmpty(result.DownloadUrl))
                                    msg += "是否立即下载并更新？";
                                else
                                    msg += "是否前往 GitHub 下载？";

                                var dialogResult = System.Windows.MessageBox.Show(msg, "发现新版本",
                                    MessageBoxButton.YesNo, MessageBoxImage.Information);

                                if (dialogResult == MessageBoxResult.Yes)
                                {
                                    var updateWindow = new UpdateWindow();
                                    updateWindow.ShowUpdateResult(result);
                                    updateWindow.ShowDialog();
                                }
                            }
                            catch (Exception ex)
                            {
                                App.Log($"[MainWindow] AutoCheckUpdate UI error: {ex.Message}");
                            }
                        }));
                    }
                });
        }
        catch (Exception ex)
        {
            App.Log($"[MainWindow] AutoCheckUpdate error: {ex.Message}");
        }
    }

    // ---- Fences ----

    /// <summary>显示 Dashboard 窗口。通过命名事件信号通知 Dashboard 进程。</summary>
    private void ShowDashboard()
    {
        try
        {
            // 查找 Dashboard 进程
            var dashProc = System.Diagnostics.Process.GetProcessesByName("DesktopManagerDashboard")
                .FirstOrDefault();
            if (dashProc == null)
            {
                // Dashboard 未运行，用 --show 参数启动它（强制显示窗口）
                // 单文件发布时 Assembly.Location 返回空字符串，改用 ProcessPath / BaseDirectory
                var exePath = Environment.ProcessPath;
                var baseDir = string.IsNullOrEmpty(exePath) ? AppDomain.CurrentDomain.BaseDirectory : System.IO.Path.GetDirectoryName(exePath)!;
                var candidates = new[]
                {
                    System.IO.Path.Combine(baseDir, "DesktopManagerDashboard.exe"),
                    System.IO.Path.Combine(baseDir, "..", "..", "..", "DesktopManagerDashboard", "bin", "Release", "net8.0-windows", "DesktopManagerDashboard.exe"),
                    System.IO.Path.Combine(baseDir, "..", "..", "..", "DesktopManagerDashboard", "bin", "Release", "net8.0-windows", "win-x64", "DesktopManagerDashboard.exe"),
                    System.IO.Path.Combine(baseDir, "..", "..", "..", "DesktopManagerDashboard", "bin", "Debug", "net8.0-windows", "DesktopManagerDashboard.exe"),
                    System.IO.Path.Combine(baseDir, "..", "..", "..", "DesktopManagerDashboard", "bin", "Debug", "net8.0-windows", "win-x64", "DesktopManagerDashboard.exe"),
                    System.IO.Path.Combine(baseDir, "..", "..", "DesktopManagerDashboard", "bin", "Release", "net8.0-windows", "DesktopManagerDashboard.exe"),
                };
                foreach (var p in candidates)
                {
                    var full = System.IO.Path.GetFullPath(p);
                    if (System.IO.File.Exists(full))
                    {
                        App.Log($"Starting Dashboard from: {full}");
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = full,
                            Arguments = "--show",
                            UseShellExecute = true
                        });
                        break;
                    }
                }
                App.Log("Dashboard exe not found in any candidate path");
                return;
            }

            // Dashboard 已在运行，通过信号文件通知它显示
            var signalPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DeskOrganizer", "show_dashboard.signal");
            System.IO.File.WriteAllText(signalPath, DateTime.Now.ToString("o"));
        }
        catch (Exception ex)
        {
            App.Log($"ShowDashboard error: {ex.Message}");
        }
    }

    private void CreateNewFence()
    {
        try
        {
            App.Log("Creating new fence...");
            FenceManager.Instance.CreateFence("新建围栏");
            App.Log($"Fence created. Active count: {FenceManager.Instance.ActiveFenceCount}");
            LogActivity("fence", "新建围栏");
        }
        catch (Exception ex)
        {
            App.Log($"Create fence failed: {ex.Message}");
        }
    }

    // ---- Sticky Notes ----

    private void LoadStickyNotes()
    {
        var notes = ConfigService.Instance.Config.StickyNotes;
        if (notes == null) return;

        // 复制列表避免遍历时集合被修改
        foreach (var note in notes.ToList())
        {
            CreateStickyNoteFromModel(note);
        }
    }

    private void CreateStickyNoteFromModel(Model.StickyNote noteModel)
    {
        var note = new StickyNoteWindow(noteModel);
        note.Closed += (_, _) =>
        {
            lock (_stickyNotesLock)
            {
                _stickyNotes.Remove(note);
            }
        };
        lock (_stickyNotesLock)
        {
            _stickyNotes.Add(note);
        }

        // 设置便签间吸附回调
        StickyNoteWindow.GetOtherNotes = (self) =>
        {
            lock (_stickyNotesLock)
            {
                return _stickyNotes.Where(n => n != self && n.IsLoaded).ToList();
            }
        };

        note.Show();
    }

    /// <summary>自动排布所有便签（保持各自尺寸，网格平铺）。</summary>
    private void OrganizeDesktopShortcuts()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                // 二次确认
                var confirm = System.Windows.MessageBox.Show(
                    "将扫描桌面上的快捷方式并按类型分类到围栏中，是否继续？",
                    "一键整理", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (confirm != MessageBoxResult.Yes) return;

                var (total, organized, unmatched) = FenceManager.Instance.OrganizeDesktopShortcuts();
                if (total == 0)
                {
                    System.Windows.MessageBox.Show("桌面上没有找到快捷方式。", "一键整理",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else if (organized == 0)
                {
                    System.Windows.MessageBox.Show($"桌面共 {total} 个快捷方式，均已整理到围栏中。", "一键整理",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    var msg = $"扫描到 {total} 个桌面快捷方式\n" +
                              $"已整理 {organized} 个到围栏中\n" +
                              $"其中 {unmatched} 个归入\"未分类\"围栏";
                    System.Windows.MessageBox.Show(msg, "一键整理完成",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                App.Log($"OrganizeDesktopShortcuts error: {ex.Message}");
                System.Windows.MessageBox.Show($"整理失败: {ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }));
    }

    private void AutoArrangeStickyNotes()
    {
        List<StickyNoteWindow> notes;
        lock (_stickyNotesLock)
        {
            notes = _stickyNotes.Where(n => n.IsLoaded).ToList();
        }

        if (notes.Count == 0) return;

        var screen = SystemParameters.WorkArea;
        const double gap = 10;
        const double slotW = 280;
        const double slotH = 300;
        int maxCols = Math.Max(1, (int)((screen.Width - gap * 2) / (slotW + gap)));

        for (int i = 0; i < notes.Count; i++)
        {
            int col = i % maxCols;
            int row = i / maxCols;

            double x = screen.Left + gap + col * (slotW + gap);
            double y = screen.Top + gap + row * (slotH + gap);

            // 确保不超出屏幕底部
            double noteH = notes[i].ActualHeight > 0 ? notes[i].ActualHeight : slotH;
            if (y + noteH > screen.Bottom)
            {
                // 超出底部，换到下一组起始列
                col = 0;
                row = (int)((screen.Height - gap * 2) / (slotH + gap));
                x = screen.Left + gap;
                y = screen.Top + gap + row * (slotH + gap);
                if (y + noteH > screen.Bottom) y = screen.Bottom - noteH - gap;
            }

            notes[i].Left = x;
            notes[i].Top = y;
        }
    }

    private void CreateNewStickyNote()
    {
        var screen = SystemParameters.WorkArea;
        var model = new Model.StickyNote
        {
            Id = Guid.NewGuid().ToString(),
            Title = "新便签",
            Content = "",
            X = screen.Left + (GetStickyNoteCount() * 30) % 400,
            Y = screen.Top + (GetStickyNoteCount() * 30) % 300,
            Width = 300,
            Height = 350,
            BackgroundColor = "#FFFFE066",
            Opacity = 1.0,
            FontSize = 14,
            CreatedAt = DateTime.Now,
            ModifiedAt = DateTime.Now
        };

        ConfigService.Instance.Config.StickyNotes ??= new List<Model.StickyNote>();
        ConfigService.Instance.Config.StickyNotes.Add(model);
        ConfigService.Instance.Save();

        CreateStickyNoteFromModel(model);
        LogActivity("sticky", "新建便签");
    }

    private int GetStickyNoteCount()
    {
        lock (_stickyNotesLock)
        {
            return _stickyNotes.Count;
        }
    }

    /// <summary>
    /// 从窗口列表中移除便签窗口引用（不删除配置数据）。
    /// 关闭便签 = 隐藏窗口，重启后恢复；配置数据保留。
    /// </summary>
    public void UnregisterStickyNoteWindow(StickyNoteWindow window)
    {
        lock (_stickyNotesLock)
        {
            _stickyNotes.Remove(window);
        }
    }

    /// <summary>
    /// 彻底删除便签（从配置中移除），重启后不存在。
    /// </summary>
    public void DeleteStickyNote(string noteId)
    {
        lock (_stickyNotesLock)
        {
            var window = _stickyNotes.FirstOrDefault(w => w.NoteId == noteId);
            if (window != null)
            {
                _stickyNotes.Remove(window);
                window.Close();
            }
        }

        var notes = ConfigService.Instance.Config.StickyNotes;
        var note = notes?.FirstOrDefault(n => n.Id == noteId);
        if (note != null)
        {
            notes!.Remove(note);
            ConfigService.Instance.Save();
            LogActivity("sticky", $"删除便签: {note.Title}");
        }
    }

    // ---- Exit ----

    private bool _isExiting;

    public void ExitApplication()
    {
        if (_isExiting) return; // 防重入：托盘退出与 IPC exit 命令可能并发
        _isExiting = true;
        App.Log("Exiting...");

        PrepareForExit();

        // Shutdown WPF application（围栏线程是 IsBackground=true，主线程退出后自动终止）
        Application.Current.Shutdown();

        // 兜底：如果 Shutdown 后 2 秒进程仍未退出，强制终止
        // 用独立线程计时，避免被 Shutdown 阻塞
        new Thread(() =>
        {
            Thread.Sleep(2000);
            App.Log("Process force exiting (timeout)...");
            try { System.Diagnostics.Process.GetCurrentProcess().Kill(); } catch { }
        }) { IsBackground = true }.Start();
    }

    /// <summary>清理资源（IPC、热键、围栏、便签、托盘），不退出进程。用于更新流程中先清理再 Environment.Exit。</summary>
    public void PrepareForExit()
    {
        try
        {
            // 停止 IPC 服务（先释放端口，避免下次启动冲突）
            StopIpcServer();

            // Unregister hotkeys
            UnregisterHotKeys();

            // 关闭所有围栏窗口
            FenceManager.Instance.CloseAllFences();

            // Save and close all sticky notes
            List<StickyNoteWindow> notesToClose;
            lock (_stickyNotesLock)
            {
                notesToClose = _stickyNotes.ToList();
            }
            foreach (var note in notesToClose)
            {
                note.Save();
                note.Close();
            }
            lock (_stickyNotesLock)
            {
                _stickyNotes.Clear();
            }

            // Save config
            ConfigService.Instance.Save();

            // Cleanup notify icon
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }

            // 强制终止所有围栏 STA 线程的消息泵
            FenceManager.Instance.ForceTerminateAllFenceThreads();

            App.Log("PrepareForExit completed");
        }
        catch (Exception ex)
        {
            App.Log($"PrepareForExit error: {ex.Message}");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        // Prevent closing via normal means; use ExitApplication
        _notifyIcon?.Dispose();
        _msgWindow?.DestroyHandle();
        base.OnClosed(e);
    }

    // ---- IPC HTTP Server ----

    private void StartIpcServer()
    {
        try
        {
            _ipcServer = new HttpListener();
            _ipcServer.Prefixes.Add(IPC_PREFIX);
            _ipcServer.Prefixes.Add(IPC_STATUS_PREFIX);
            _ipcServer.Prefixes.Add(IPC_FENCE_LIST_PREFIX);
            _ipcServer.Prefixes.Add(IPC_DESKTOP_ITEMS_PREFIX);

            _ipcThread = new Thread(IpcListenerLoop)
            {
                IsBackground = true,
                Name = "IPC-Server"
            };
            _ipcThread.Start();
            // "started" 日志由 IpcListenerLoop 在确认 IsListening 后输出，避免误报
        }
        catch (Exception ex)
        {
            App.Log($"IPC server failed to start: {ex.Message}");
        }
    }

    private void StopIpcServer()
    {
        try
        {
            _ipcServer?.Stop();
            _ipcServer?.Close();
            App.Log("IPC server stopped.");
        }
        catch (Exception ex)
        {
            App.Log($"IPC server stop error: {ex.Message}");
        }
    }

    private void IpcListenerLoop()
    {
        try
        {
            _ipcServer?.Start();
            if (_ipcServer?.IsListening == true)
            {
                App.Log($"IPC server started on {IPC_PREFIX}");
            }
            else
            {
                App.Log("IPC server failed to start (not listening)");
                return;
            }
            while (_ipcServer?.IsListening == true)
            {
                try
                {
                    var ctx = _ipcServer.GetContext();
                    HandleIpcRequest(ctx);
                }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    App.Log($"IPC request error: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"IPC listener crashed: {ex.Message}");
        }
    }

    private void HandleIpcRequest(HttpListenerContext ctx)
    {
        var url = ctx.Request.Url;
        var path = url!.AbsolutePath;
        string responseJson;

        ctx.Response.ContentType = "application/json";
        ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
        ctx.Response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
        ctx.Response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Accept, Origin");

        // 处理 CORS 预检请求
        if (ctx.Request.HttpMethod == "OPTIONS")
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.Close();
            return;
        }

        try
        {
            if (path.StartsWith("/cmd/"))
            {
                var cmd = path.Substring(5); // 去掉 "/cmd/"
                responseJson = ProcessCommand(cmd, ctx);
            }
            else if (path.StartsWith("/status/"))
            {
                responseJson = GetStatus();
            }
            else if (path.StartsWith("/fence-list/"))
            {
                responseJson = GetFenceList();
            }
            else if (path.StartsWith("/desktop-items/"))
            {
                responseJson = GetDesktopItems();
            }
            else
            {
                responseJson = """{"ok":false,"error":"unknown endpoint"}""";
            }

            var bytes = Encoding.UTF8.GetBytes(responseJson);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
        }
        catch (Exception ex)
        {
            try
            {
                var errBytes = Encoding.UTF8.GetBytes($$"""{"ok":false,"error":"{{ex.Message}}"}""");
                ctx.Response.StatusCode = 500;
                ctx.Response.ContentLength64 = errBytes.Length;
                ctx.Response.OutputStream.Write(errBytes, 0, errBytes.Length);
            }
            catch { }
            finally
            {
                try { ctx.Response.OutputStream.Close(); } catch { }
                try { ctx.Response.Close(); } catch { }
            }
        }
    }

    private string ProcessCommand(string cmd, HttpListenerContext ctx)
    {
        // 在 WPF 线程上异步执行命令（避免 Dispatcher.Invoke 同步阻塞导致鼠标异常）
        var result = new Dictionary<string, object> { ["ok"] = false };

        switch (cmd)
        {
            case "create-fence":
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { FenceManager.Instance.CreateFence("新建围栏"); }
                    catch (Exception ex) { App.Log($"IPC create-fence error: {ex.Message}"); }
                }));
                result["ok"] = true;
                result["message"] = "围栏已创建";
                break;

            case "create-sticky":
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { CreateNewStickyNote(); }
                    catch (Exception ex) { App.Log($"IPC create-sticky error: {ex.Message}"); }
                }));
                result["ok"] = true;
                result["message"] = "便签已创建";
                break;

            case "search":
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { ShowSearchWindow(); }
                    catch (Exception ex) { App.Log($"IPC search error: {ex.Message}"); }
                }));
                result["ok"] = true;
                result["message"] = "搜索已打开";
                break;

            case "open-settings":
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { ShowSettingsWindow(); }
                    catch (Exception ex) { App.Log($"IPC open-settings error: {ex.Message}"); }
                }));
                result["ok"] = true;
                result["message"] = "设置已打开";
                break;

            case "switch-desktop":
                // 切换虚拟桌面：body 中包含 { "index": 1-9 }
                if (ctx.Request.HasEntityBody)
                {
                    using var switchReader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
                    var switchBody = switchReader.ReadToEnd();
                    try
                    {
                        var switchDoc = JsonDocument.Parse(switchBody);
                        if (switchDoc.RootElement.TryGetProperty("index", out var idxElem))
                        {
                            var desktopIndex = idxElem.GetInt32();
                            _currentDesktopIndex = desktopIndex;
                            App.CurrentDesktopIndex = desktopIndex;

                            // 显示目标桌面的围栏，隐藏其他桌面的围栏
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                FenceManager.Instance.ShowFencesForDesktop(desktopIndex);
                            }));

                            // 发送模拟按键切换 Windows 虚拟桌面
                            new System.Threading.Thread(() =>
                            {
                                try { Win32.VirtualDesktopHelper.SwitchToDesktop(desktopIndex); }
                                catch (Exception ex) { App.Log($"IPC switch-desktop error: {ex.Message}"); }
                            })
                            { IsBackground = true, Name = "DesktopSwitch" }.Start();

                            result["ok"] = true;
                            result["currentDesktop"] = desktopIndex;
                            result["message"] = $"切换到桌面 {desktopIndex}";
                        }
                        else
                        {
                            result["error"] = "missing desktop index";
                        }
                    }
                    catch (Exception ex) { result["error"] = ex.Message; }
                }
                else { result["error"] = "missing request body"; }
                break;

            case "backup":
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { ConfigService.Instance.CreateBackup(); }
                    catch (Exception ex) { App.Log($"IPC backup error: {ex.Message}"); }
                }));
                result["ok"] = true;
                result["message"] = "备份完成";
                break;

            case "restore-backup":
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        var restored = ConfigService.Instance.TryRestoreFromBackup();
                        if (restored != null)
                        {
                            ConfigService.Instance.Config.Boxes = restored.Boxes;
                            ConfigService.Instance.Config.StickyNotes = restored.StickyNotes;
                            ConfigService.Instance.Save();
                            App.Log("IPC restore-backup: restored successfully");
                        }
                    }
                    catch (Exception ex) { App.Log($"IPC restore-backup error: {ex.Message}"); }
                }));
                result["ok"] = true;
                result["message"] = "恢复完成，请重启应用";
                break;

            case "clean-backups":
                try
                {
                    var backupDir = ConfigService.Instance.BackupDirectoryPath;
                    var removed = 0;
                    if (backupDir != null && System.IO.Directory.Exists(backupDir))
                    {
                        foreach (var file in System.IO.Directory.GetFiles(backupDir, "config_*.json"))
                        {
                            try { System.IO.File.Delete(file); removed++; } catch (Exception ex) { App.Log($"[MainWindow] Delete backup failed for {file}: {ex.Message}"); }
                        }
                    }
                    result["ok"] = true;
                    result["message"] = $"已清理 {removed} 个备份文件";
                    result["removedCount"] = removed;
                }
                catch (Exception ex) { App.Log($"IPC clean-backups error: {ex.Message}"); result["error"] = ex.Message; }
                break;

            case "set-storage-limit":
                if (ctx.Request.HasEntityBody)
                {
                    using var limitReader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
                    var limitBody = limitReader.ReadToEnd();
                    try
                    {
                        var limitDoc = JsonDocument.Parse(limitBody);
                        if (limitDoc.RootElement.TryGetProperty("limitMB", out var limitElem))
                        {
                            var limitMB = limitElem.GetInt32();
                            ConfigService.Instance.Config.StorageLimitMB = limitMB;
                            ConfigService.Instance.Save();
                            result["ok"] = true;
                            result["storageLimitMB"] = limitMB;
                            result["message"] = $"存储限制已设置为 {limitMB} MB";
                        }
                        else
                        {
                            result["error"] = "missing limitMB";
                        }
                    }
                    catch (Exception ex) { result["error"] = ex.Message; }
                }
                else { result["error"] = "missing request body"; }
                break;

            case "show-dashboard":
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { ShowDashboard(); }
                    catch (Exception ex) { App.Log($"IPC show-dashboard error: {ex.Message}"); }
                }));
                result["ok"] = true;
                result["message"] = "Dashboard 已唤起";
                break;

            case "organize-desktop":
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        var (total, organized, unmatched) = FenceManager.Instance.OrganizeDesktopShortcuts();
                        App.Log($"IPC organize-desktop: total={total} organized={organized} unmatched={unmatched}");
                    }
                    catch (Exception ex) { App.Log($"IPC organize-desktop error: {ex.Message}"); }
                }));
                result["ok"] = true;
                result["message"] = "桌面整理已启动";
                break;

            case "exit":
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { ExitApplication(); }
                    catch (Exception ex) { App.Log($"IPC exit error: {ex.Message}"); }
                }));
                result["ok"] = true;
                result["message"] = "程序退出中";
                break;

            case "remove-fence":
                if (ctx.Request.HasEntityBody)
                {
                    using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
                    var body = reader.ReadToEnd();
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("id", out var idElem))
                    {
                        var fenceId = idElem.GetString();
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                var fence = ConfigService.Instance.Config.Boxes?.FirstOrDefault(b => b.Id == fenceId);
                                if (fence != null) FenceManager.Instance.RemoveFence(fence);
                            }
                            catch (Exception ex) { App.Log($"IPC remove-fence error: {ex.Message}"); }
                        }));
                        result["ok"] = true;
                        result["message"] = "围栏已删除";
                    }
                    else
                    {
                        result["error"] = "missing fence id";
                    }
                }
                else
                {
                    result["error"] = "missing request body";
                }
                break;

            case "update-fence":
                if (ctx.Request.HasEntityBody)
                {
                    using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
                    var body = reader.ReadToEnd();
                    using var doc2 = JsonDocument.Parse(body);
                    if (doc2.RootElement.TryGetProperty("id", out var idElem))
                    {
                        var fenceId = idElem.GetString();
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                var fence = ConfigService.Instance.Config.Boxes?.FirstOrDefault(b => b.Id == fenceId);
                                if (fence != null)
                                {
                                    var newX = fence.X;
                                    var newY = fence.Y;
                                    var newW = fence.Width;
                                    var newH = fence.Height;

                                    if (doc2.RootElement.TryGetProperty("x", out var xElem))
                                    {
                                        newX = xElem.GetDouble();
                                        fence.X = newX;
                                        fence.PosX = (int)newX;
                                    }
                                    if (doc2.RootElement.TryGetProperty("y", out var yElem))
                                    {
                                        newY = yElem.GetDouble();
                                        fence.Y = newY;
                                        fence.PosY = (int)newY;
                                    }
                                    if (doc2.RootElement.TryGetProperty("width", out var wElem))
                                    {
                                        newW = wElem.GetDouble();
                                        fence.Width = newW;
                                    }
                                    if (doc2.RootElement.TryGetProperty("height", out var hElem))
                                    {
                                        newH = hElem.GetDouble();
                                        fence.Height = newH;
                                    }
                                    if (doc2.RootElement.TryGetProperty("name", out var nElem))
                                        fence.Name = nElem.GetString()!;

                                    fence.ModifiedAt = DateTime.UtcNow;

                                    // 保存配置
                                    ConfigService.Instance.Save();

                                    // 移动实际围栏窗口到新位置
                                    var win = FenceManager.Instance.GetFenceWindow(fenceId!);
                                    if (win != null && win.IsHandleCreated && !win.IsDisposed)
                                    {
                                        win.BeginSuppressEvents();
                                        var hwnd = win.Handle;
                                        var flags = NoFences.Win32.SWPFlags.SWP_NOACTIVATE | NoFences.Win32.SWPFlags.SWP_NOZORDER;
                                        NoFences.Win32.WindowUtil.SetWindowPos(hwnd, IntPtr.Zero,
                                            (int)newX, (int)newY, (int)newW, (int)newH, flags);
                                        win.EndSuppressEvents(300);
                                    }
                                }
                            }
                            catch (Exception ex) { App.Log($"IPC update-fence error: {ex.Message}"); }
                        }));
                        result["ok"] = true;
                        result["message"] = "围栏已更新";
                    }
                    else
                    {
                        result["error"] = "missing fence id";
                    }
                }
                else
                {
                    result["error"] = "missing request body";
                }
                break;

            case "get-sticky-notes":
                result["ok"] = true;
                result["notes"] = (ConfigService.Instance.Config.StickyNotes ?? new List<Model.StickyNote>())
                    .Select(n => new
                    {
                        id = n.Id,
                        title = n.Title,
                        content = n.Content ?? "",
                        x = n.X,
                        y = n.Y,
                        width = n.Width,
                        height = n.Height,
                        backgroundColor = n.BackgroundColor,
                        fontSize = n.FontSize,
                        createdAt = n.CreatedAt,
                        modifiedAt = n.ModifiedAt
                    });
                break;

            case "get-config":
                {
                    var cfg = ConfigService.Instance.Config;
                    var boxes = cfg.Boxes ?? new List<FenceInfo>();
                    var notes = cfg.StickyNotes ?? new List<Model.StickyNote>();
                    result["ok"] = true;
                    result["config"] = new
                    {
                        version = cfg.Version,
                        fenceCount = boxes.Count,
                        stickyNoteCount = notes.Count,
                        totalFiles = boxes.Sum(b => b.FilePaths?.Count ?? 0),
                        screenResolution = $"{(int)SystemParameters.PrimaryScreenWidth}x{(int)SystemParameters.PrimaryScreenHeight}",
                        currentDesktop = _currentDesktopIndex,
                        uptime = (DateTime.Now - App.StartTime).ToString(@"hh\:mm\:ss"),
                        dataPath = System.IO.Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskOrganizer")
                    };
                }
                break;

            case "get-activity":
                result["ok"] = true;
                lock (_activityLogLock)
                {
                    result["activities"] = _activityLog.Select(a => new
                    {
                        time = a.Time.ToString("HH:mm:ss"),
                        type = a.Type,
                        message = a.Message
                    }).TakeLast(50);
                }
                break;

            default:
                result["error"] = $"unknown command: {cmd}";
                break;
        }

        return JsonSerializer.Serialize(result);
    }

    private string GetStatus()
    {
        // 统计所有围栏中的收纳文件总数
        int totalFiles = 0;
        var fences = ConfigService.Instance.Config.Boxes ?? new List<FenceInfo>();
        foreach (var f in fences)
        {
            totalFiles += f.FilePaths?.Count ?? 0;
        }

        // 获取工作区分辨率（排除任务栏），围栏只能在工作区内移动
        var workArea = System.Windows.SystemParameters.WorkArea;
        var screen = workArea.Width;
        var screenH = workArea.Height;

        // 计算存储占用（配置目录总大小）
        long storageBytes = 0;
        try
        {
            var configDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskOrganizer");
            if (System.IO.Directory.Exists(configDir))
            {
                foreach (var file in System.IO.Directory.GetFiles(configDir, "*", System.IO.SearchOption.AllDirectories))
                {
                    try { storageBytes += new System.IO.FileInfo(file).Length; } catch { }
                }
            }
        }
        catch { }

        // 备份信息
        int backupCount = 0;
        string lastBackup = "无";
        try
        {
            backupCount = ConfigService.Instance.GetBackupCount();
            var backupDir = ConfigService.Instance.BackupDirectoryPath;
            if (backupDir != null && System.IO.Directory.Exists(backupDir))
            {
                var latest = System.IO.Directory.GetFiles(backupDir, "config_*.json")
                    .Select(System.IO.Path.GetFileName)
                    .OrderByDescending(n => n)
                    .FirstOrDefault();
                if (latest != null)
                {
                    // 从文件名 config_YYYYMMDD_HHMMSS.json 提取时间
                    var match = System.Text.RegularExpressions.Regex.Match(latest, @"config_(\d{8})_(\d{6})");
                    if (match.Success)
                    {
                        var dt = DateTime.ParseExact(
                            match.Groups[1].Value + match.Groups[2].Value,
                            "yyyyMMddHHmmss",
                            System.Globalization.CultureInfo.InvariantCulture);
                        lastBackup = dt.ToString("yyyy-MM-dd HH:mm");
                    }
                }
            }
        }
        catch { }

        var status = new Dictionary<string, object>
        {
            ["ok"] = true,
            ["fences"] = FenceManager.Instance.ActiveFenceCount,
            ["stickyNotes"] = GetStickyNoteCount(),
            ["totalFiles"] = totalFiles,
            ["version"] = App.Version,
            ["uptime"] = (DateTime.Now - App.StartTime).ToString(@"hh\:mm\:ss"),
            ["screenWidth"] = (int)screen,
            ["screenHeight"] = (int)screenH,
            ["currentDesktop"] = _currentDesktopIndex,
            ["storageBytes"] = storageBytes,
            ["storageLimitMB"] = ConfigService.Instance.Config.StorageLimitMB,
            ["backupCount"] = backupCount,
            ["lastBackup"] = lastBackup,
            ["searchIndexLimit"] = ConfigService.Instance.Config.SearchIndexLimit,
            ["indexedFiles"] = SearchService.Instance.IndexedCount
        };
        return JsonSerializer.Serialize(status);
    }

    private string GetFenceList()
    {
        var fences = ConfigService.Instance.Config.Boxes ?? new List<FenceInfo>();
        var list = fences.Select(f => new
        {
            id = f.Id,
            name = f.Name,
            fileCount = f.FilePaths?.Count ?? 0,
            x = f.X,
            y = f.Y,
            width = f.Width,
            height = f.Height,
            desktopIndex = f.DesktopIndex,
            filePaths = f.FilePaths ?? new List<string>()
        });
        var result = new Dictionary<string, object>
        {
            ["ok"] = true,
            ["fences"] = list
        };
        return JsonSerializer.Serialize(result);
    }

    /// <summary>扫描桌面上的所有文件、快捷方式和文件夹。</summary>
    private string GetDesktopItems()
    {
        var items = new List<object>();
        try
        {
            // 桌面路径（用户桌面 + 公共桌面）
            var userDesktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var publicDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

            var desktopPaths = new[] { userDesktop, publicDesktop }.Distinct().ToList();

            // 图标位置缓存文件（desktop.ini 或注册表中的图标位置）
            foreach (var desktopPath in desktopPaths)
            {
                if (!Directory.Exists(desktopPath)) continue;

                var entries = Directory.EnumerateFileSystemEntries(desktopPath)
                    .Where(p => !Path.GetFileName(p).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                    .Where(p => !Path.GetFileName(p).StartsWith(".", StringComparison.Ordinal));

                foreach (var entry in entries)
                {
                    var fileName = Path.GetFileName(entry);
                    var ext = Path.GetExtension(entry).ToLowerInvariant();
                    var isFolder = Directory.Exists(entry);
                    var isShortcut = ext == ".lnk";
                    var isFile = File.Exists(entry);

                    // 确定类型
                    string itemType;
                    string? targetPath = null;
                    if (isFolder)
                        itemType = "folder";
                    else if (isShortcut)
                    {
                        itemType = "shortcut";
                        // 尝试解析快捷方式目标
                        try { targetPath = ResolveShortcut(entry); } catch { }
                    }
                    else
                        itemType = "file";

                    // 获取文件大小（仅文件）
                    long fileSize = 0;
                    if (isFile)
                    {
                        try { fileSize = new FileInfo(entry).Length; } catch { }
                    }

                    items.Add(new
                    {
                        name = fileName,
                        path = entry,
                        type = itemType,
                        extension = isFolder ? "" : ext,
                        size = fileSize,
                        targetPath = targetPath ?? ""
                    });
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"GetDesktopItems error: {ex.Message}");
        }

        var result = new Dictionary<string, object>
        {
            ["ok"] = true,
            ["items"] = items,
            ["count"] = items.Count
        };
        return JsonSerializer.Serialize(result);
    }

    /// <summary>解析 .lnk 快捷方式的目标路径。</summary>
    private string ResolveShortcut(string shortcutPath)
    {
        try
        {
            // 使用 WScript.Shell COM 对象解析快捷方式
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return "";
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            string target = shortcut.TargetPath;
            return target ?? "";
        }
        catch { return ""; }
    }
}

/// <summary>纯 Win32 消息窗口，用于注册全局热键和接收 WM_HOTKEY 消息。</summary>
internal class Win32MessageWindow : System.Windows.Forms.NativeWindow
{
    private const int WM_HOTKEY = 0x0312;

    public event Action? HotkeyReceived;

    public Win32MessageWindow()
    {
        CreateHandle(new System.Windows.Forms.CreateParams
        {
            Caption = "DeskOrganizer_v2_MsgWindow",
            Parent = IntPtr.Zero,
            Style = 0,
            ExStyle = 0
        });
    }

    protected override void WndProc(ref System.Windows.Forms.Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == WM_HOTKEY)
        {
            HotkeyReceived?.Invoke();
        }
    }
}
