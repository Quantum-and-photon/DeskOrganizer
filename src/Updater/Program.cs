using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace DeskOrganizer.Updater;

/// <summary>
/// 独立更新程序：等待主程序退出 -> 替换 exe -> 重启程序。
/// 作为独立 exe 运行，不受主进程退出影响。
/// </summary>
internal static class Program
{
    static void Main(string[] args)
    {
        if (args.Length < 3)
        {
            return;
        }

        var downloadedFilePath = args[0];
        var exePath = args[1];
        var exeName = args[2];
        var logFile = Path.Combine(Path.GetTempPath(), "DeskOrganizerUpdate.log");

        try
        {
            File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss.fff}] Updater started\n");
            File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss.fff}] Source: {downloadedFilePath}\n");
            File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss.fff}] Target: {exePath}\n");

            // 等待主程序退出（最多 30 秒）
            File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss.fff}] Waiting for process exit...\n");
            for (int i = 0; i < 30; i++)
            {
                var procs = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exeName));
                if (procs.Length == 0)
                {
                    File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss.fff}] Process exited after {i} seconds\n");
                    break;
                }
                foreach (var p in procs) p.Dispose();
                Thread.Sleep(1000);
            }

            // 强制终止残留进程
            try
            {
                var procs = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exeName));
                foreach (var p in procs)
                {
                    File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss.fff}] Killing PID={p.Id}\n");
                    p.Kill();
                    p.WaitForExit(3000);
                    p.Dispose();
                }
            }
            catch { }

            Thread.Sleep(2000);

            // 替换文件（重试 20 次，每次 2 秒，处理 OneDrive 文件锁）
            File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss.fff}] Replacing file...\n");
            bool replaced = false;
            for (int i = 0; i < 20; i++)
            {
                try
                {
                    // 先尝试删除旧文件
                    if (File.Exists(exePath))
                    {
                        File.Delete(exePath);
                    }
                    // 移动新文件
                    File.Move(downloadedFilePath, exePath);
                    replaced = true;
                    File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss.fff}] File replaced on attempt {i + 1}\n");
                    break;
                }
                catch (Exception ex)
                {
                    File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss.fff}] Attempt {i + 1} failed: {ex.Message}\n");
                    Thread.Sleep(2000);
                }
            }

            // 如果 move 失败，尝试 copy
            if (!replaced)
            {
                try
                {
                    File.Copy(downloadedFilePath, exePath, true);
                    replaced = true;
                    File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss.fff}] File copied as fallback\n");
                }
                catch (Exception ex)
                {
                    File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss.fff}] Copy fallback failed: {ex.Message}\n");
                }
            }

            // 重启程序
            if (File.Exists(exePath))
            {
                File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss.fff}] Restarting program...\n");
                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true
                });
                File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss.fff}] Program restarted\n");
            }

            // 清理
            try { if (File.Exists(downloadedFilePath)) File.Delete(downloadedFilePath); } catch { }
            File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss.fff}] Updater finished\n");
        }
        catch (Exception ex)
        {
            File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss.fff}] FATAL: {ex}\n");
        }
    }
}
