using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace DeskOrganizer.Updater;

public partial class MainWindow : Window
{
    private readonly string _sourceFile;
    private readonly string _targetExePath;
    private readonly string _exeName;
    private readonly string _logPath;

    public MainWindow()
    {
        InitializeComponent();

        _sourceFile = (string)Application.Current.Properties["SourceFile"];
        _targetExePath = (string)Application.Current.Properties["TargetExePath"];
        _exeName = (string)Application.Current.Properties["ExeName"];
        _logPath = Path.Combine(Path.GetTempPath(), "DeskOrganizerUpdate.log");

        Loaded += (_, _) => _ = RunUpdateAsync();
    }

    private void Log(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
        try { File.AppendAllText(_logPath, line + "\n"); } catch { }
    }

    private void SetStatus(string text)
    {
        Dispatcher.BeginInvoke(() => { StatusText.Text = text; });
    }

    private async Task RunUpdateAsync()
    {
        try
        {
            Log("=== Updater started ===");
            Log($"Source: {_sourceFile}");
            Log($"Target: {_targetExePath}");

            // 1. 等待主程序退出（最多 30 秒）
            SetStatus("等待程序退出...");
            Log("Waiting for main process exit...");
            var processName = Path.GetFileNameWithoutExtension(_exeName);
            for (int i = 0; i < 30; i++)
            {
                var procs = Process.GetProcessesByName(processName);
                if (procs.Length == 0)
                {
                    Log($"Process exited after {i} seconds");
                    break;
                }
                foreach (var p in procs) p.Dispose();
                await Task.Delay(1000);
            }

            // 2. 强制终止残留进程
            SetStatus("正在关闭程序...");
            try
            {
                var procs = Process.GetProcessesByName(processName);
                foreach (var p in procs)
                {
                    Log($"Killing PID={p.Id}");
                    p.Kill();
                    p.WaitForExit(3000);
                    p.Dispose();
                }
            }
            catch { }

            await Task.Delay(2000);

            // 3. 替换文件（重试 20 次，每次 2 秒，处理 OneDrive 文件锁）
            SetStatus("正在替换文件...");
            Log("Replacing file...");
            bool replaced = false;
            for (int i = 0; i < 20; i++)
            {
                try
                {
                    if (File.Exists(_targetExePath))
                        File.Delete(_targetExePath);
                    File.Move(_sourceFile, _targetExePath);
                    replaced = true;
                    Log($"File replaced on attempt {i + 1}");
                    SetStatus("文件替换成功！");
                    break;
                }
                catch (Exception ex)
                {
                    Log($"Attempt {i + 1} failed: {ex.Message}");
                    SetStatus($"正在重试 ({i + 1}/20)...");
                    await Task.Delay(2000);
                }
            }

            // 4. move 失败则 copy
            if (!replaced)
            {
                SetStatus("正在复制文件...");
                try
                {
                    File.Copy(_sourceFile, _targetExePath, true);
                    replaced = true;
                    Log("File copied as fallback");
                }
                catch (Exception ex)
                {
                    Log($"Copy fallback failed: {ex.Message}");
                }
            }

            // 5. 重启程序
            SetStatus("正在重启程序...");
            if (File.Exists(_targetExePath))
            {
                Log("Restarting program...");
                Process.Start(new ProcessStartInfo
                {
                    FileName = _targetExePath,
                    UseShellExecute = true
                });
                Log("Program restarted");
                SetStatus("更新完成！");
            }
            else
            {
                Log("ERROR: exe not found after update!");
                SetStatus("更新失败：文件不存在");
            }

            // 6. 清理
            try { if (File.Exists(_sourceFile)) File.Delete(_sourceFile); } catch { }
            Log("=== Update finished ===");

            // 短暂显示后退出
            await Task.Delay(1500);
            Dispatcher.BeginInvoke(() => Application.Current.Shutdown());
        }
        catch (Exception ex)
        {
            Log($"FATAL: {ex}");
            SetStatus("更新失败: " + ex.Message);
            await Task.Delay(3000);
            Dispatcher.BeginInvoke(() => Application.Current.Shutdown());
        }
    }
}
