using System;
using System.IO;
using Microsoft.Win32;

namespace DeskOrganizer.Win32;

/// <summary>
/// 提供基于注册表的应用程序开机自启动管理功能。
/// 通过操作 HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run 来实现。
/// </summary>
public static class AutoStartHelper
{
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "DeskOrganizer_v2";

    /// <summary>
    /// 启用或禁用开机自启动。
    /// </summary>
    /// <param name="enable">true 表示启用自启动，false 表示禁用。</param>
    public static void SetAutoStart(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
        if (key is null)
        {
            // 如果注册表项不存在，尝试创建它
            using var baseKey = Registry.CurrentUser;
            using var createdKey = baseKey.CreateSubKey(RunKeyPath);
            if (createdKey is null)
            {
                throw new InvalidOperationException("无法打开或创建注册表启动项路径。");
            }

            if (enable)
            {
                createdKey.SetValue(AppName, GetExecutablePath());
            }
            else
            {
                createdKey.DeleteValue(AppName, false);
            }

            return;
        }

        if (enable)
        {
            key.SetValue(AppName, GetExecutablePath());
        }
        else
        {
            key.DeleteValue(AppName, false);
        }
    }

    /// <summary>
    /// 检查当前是否已启用开机自启动。
    /// </summary>
    /// <returns>如果注册表中存在自启动条目则返回 true，否则返回 false。</returns>
    public static bool IsAutoStartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
        if (key is null)
            return false;

        var value = key.GetValue(AppName) as string;
        if (string.IsNullOrEmpty(value))
            return false;

        // 验证注册表中保存的路径是否仍然有效
        return string.Equals(
            Path.GetFullPath(value.Trim('"')),
            GetExecutablePath(),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 比较当前自启动状态与期望值，仅在状态不一致时才更新注册表。
    /// </summary>
    /// <param name="desired">期望的自启动状态。</param>
    /// <returns>如果实际进行了注册表更新则返回 true。</returns>
    public static bool SyncAutoStartState(bool desired)
    {
        var current = IsAutoStartEnabled();
        if (current == desired)
            return false;

        SetAutoStart(desired);
        return true;
    }

    /// <summary>
    /// 获取当前应用程序的可执行文件完整路径，并加双引号包裹以处理路径中的空格。
    /// </summary>
    private static string GetExecutablePath()
    {
        var exePath = Environment.ProcessPath
                     ?? throw new InvalidOperationException("无法确定当前进程的可执行文件路径。");

        // 注册表值中的路径应使用双引号包裹，以正确处理含空格的路径
        return $"\"{exePath}\"";
    }
}
