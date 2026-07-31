using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using DeskOrganizer.Model;
using WpfApplication = System.Windows.Application;

namespace DeskOrganizer;

public partial class App : WpfApplication
{
    private Mutex? _mutex;
    private bool _ownsMutex;
    private const string MutexName = "DeskOrganizer_v2_Mutex_2026";

    public static readonly string Version = "2.0.0";
    public static readonly DateTime StartTime = DateTime.Now;
    /// <summary>当前虚拟桌面索引（1-based），由 MainWindow 更新，FenceManager 读取。</summary>
    public static int CurrentDesktopIndex = 1;
    private static string LogPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug.log");

    /// <summary>写入调试日志（同时输出到 debug.log 和控制台）。</summary>
    public static void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        Trace.WriteLine(line);
        try { File.AppendAllText(LogPath, line + "\n"); } catch { }
    }

    private void OnStartup(object sender, StartupEventArgs e)
    {
        // Debug output
        Trace.Listeners.Add(new ConsoleTraceListener());
        Log($"DeskOrganizer v2.0 starting");

        // Single instance check
        try
        {
            _mutex = new Mutex(true, MutexName, out bool createdNew);
            if (!createdNew)
            {
                // 已有实例运行，静默退出（不弹窗干扰其他版本的桌面围栏）
                Log("Another instance is already running. Exiting silently.");
                _mutex?.Dispose();
                _mutex = null;
                Shutdown();
                return;
            }
            _ownsMutex = true;
        }
        catch (UnauthorizedAccessException)
        {
            LogException(new System.Exception("Mutex creation failed: UnauthorizedAccessException"));
            // Allow the app to continue - single instance check skipped
        }

        // Global exception handlers
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        System.Windows.Forms.Application.ThreadException += OnThreadException; // WinForms Application (fully qualified)
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // 后台预热 Shell 右键菜单（预加载外壳扩展 DLL，消除首次右键卡顿）
        NoFences.Win32.ShellContextMenu.Warmup();

        // Load configuration
        ConfigService.Instance.Load();
        Log($"Config loaded: Version={ConfigService.Instance.Config.Version}, Boxes={ConfigService.Instance.Config.Boxes?.Count ?? 0}, Notes={ConfigService.Instance.Config.StickyNotes?.Count ?? 0}");

        // Create and show MainWindow (no visual, system tray only)
        var mainWindow = new MainWindow();
        mainWindow.InitializeApplication();
        MainWindow = mainWindow;
        Log("Application initialized successfully.");
    }

    private bool _isExiting;

    private void OnExit(object sender, ExitEventArgs e)
    {
        if (_isExiting) return;
        _isExiting = true;

        if (MainWindow is MainWindow mainWin)
        {
            mainWin.ExitApplication();
        }

        // 只有拥有 Mutex 的实例才能释放
        if (_ownsMutex && _mutex != null)
        {
            try { _mutex.ReleaseMutex(); } catch { }
        }
        _mutex?.Dispose();
    }

    // ---- Exception Handlers ----

    private void OnThreadException(object sender, System.Threading.ThreadExceptionEventArgs e)
    {
        LogException(e.Exception);
        ShowErrorSafe(e.Exception, "线程异常");
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            LogException(ex);
            ShowErrorSafe(ex, "未处理异常");
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogException(e.Exception);
        e.SetObserved();
    }

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        LogException(e.Exception);
        e.Handled = true;
        ShowErrorSafe(e.Exception, "UI 异常");
    }

    private static void LogException(Exception ex)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch
        {
            // Ignore logging failures
        }
    }

    private static void ShowErrorSafe(Exception ex, string title)
    {
        try
        {
            MessageBox.Show(
                $"{title}: {ex.Message}",
                "DeskOrganizer 错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            // If UI is dead, just write to console
            Console.Error.WriteLine($"{title}: {ex}");
        }
    }
}
