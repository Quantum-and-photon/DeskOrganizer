using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;

namespace DesktopManager;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // --show 参数：强制显示窗口（忽略最小化配置）
        bool argShow = e.Args.Contains("--show", System.StringComparer.OrdinalIgnoreCase);

        // --minimized 参数：强制最小化
        bool argMinimized = e.Args.Contains("--minimized", System.StringComparer.OrdinalIgnoreCase);

        // 检查配置文件中的启动最小化设置
        bool configMinimized = ReadStartMinimizedFromConfig();

        // --show 优先级最高，其次 --minimized，最后配置
        bool minimized = argShow ? false : (argMinimized || configMinimized);

        // 创建主窗口
        var mainWindow = new MainWindow();
        mainWindow.SetStartupArgs(minimized);
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DeskOrganizer_v2", "dashboard_config.json");

    private static bool ReadStartMinimizedFromConfig()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<JsonElement>(json);
                if (cfg.TryGetProperty("StartMinimized", out var val))
                    return val.GetBoolean();
            }
        }
        catch { }
        return false;
    }
}
