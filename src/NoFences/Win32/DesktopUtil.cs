using System;
using System.Runtime.InteropServices;

namespace DeskOrganizer.NoFences.Win32;

/// <summary>
/// 桌面集成工具类：防止最小化、将窗口粘附到桌面。
/// </summary>
public static class DesktopUtil
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    private const string PROGMAN_CLASS = "Progman";
    private const string WORKERW_CLASS = "WorkerW";
    private const string DESKTOP_DLG_CLASS = "#32769";

    private static IntPtr _cachedDesktopWorker = IntPtr.Zero;
    private static IntPtr _cachedDesktopWindow = IntPtr.Zero;

    /// <summary>
    /// 获取桌面 WorkerW 窗口句柄（桌面图标所在层）。
    /// 如果找不到 WorkerW（例如在某些配置下），则返回桌面窗口本身。
    /// </summary>
    public static IntPtr GetDesktopWorkerWindow()
    {
        if (_cachedDesktopWorker != IntPtr.Zero)
            return _cachedDesktopWorker;

        IntPtr progman = FindWindow(PROGMAN_CLASS, null);
        if (progman == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        IntPtr shellWindow = GetShellWindow();

        // 发送 0x052C 消息让 Progman 派生一个 WorkerW 窗口
        SendMessage(progman, 0x052C, shellWindow, IntPtr.Zero);

        // 遍历顶层窗口，找到包含 SHELLDLL_DefView 的窗口，
        // 然后该窗口的下一个兄弟就是 WorkerW
        IntPtr workerW = IntPtr.Zero;

        EnumWindows((hwnd, lParam) =>
        {
            IntPtr shellDllDefView = FindWindowEx(hwnd, IntPtr.Zero, SHELL_DLL_DEFVIEW_CLASS, null);
            if (shellDllDefView != IntPtr.Zero)
            {
                // 找到了 SHELLDLL_DefView 的父窗口，查找其兄弟 WorkerW
                workerW = FindWindowEx(IntPtr.Zero, hwnd, WORKERW_CLASS, null);
                return false; // 停止枚举
            }
            return true; // 继续枚举
        }, IntPtr.Zero);

        if (workerW != IntPtr.Zero)
        {
            _cachedDesktopWorker = workerW;
            return workerW;
        }

        // 找不到 WorkerW，不回退（保持独立 TopMost 窗口）
        return IntPtr.Zero;
    }

    private const string SHELL_DLL_DEFVIEW_CLASS = "SHELLDLL_DefView";

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// 将窗口粘附到桌面（使其成为桌面的子窗口，从而不会被 Alt-Tab 选中）。
    /// </summary>
    /// <param name="hwnd">需要粘附的窗口句柄。</param>
    public static void GlueToDesktop(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        IntPtr desktop = GetDesktopWorkerWindow();
        if (desktop != IntPtr.Zero)
        {
            SetParent(hwnd, desktop);
        }
        // 如果找不到 WorkerW，不执行 SetParent，窗口保持独立（TopMost + ToolWindow）
    }

    /// <summary>
    /// 将窗口从桌面分离（恢复为普通窗口）。
    /// </summary>
    public static void UnhookFromDesktop(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        SetParent(hwnd, IntPtr.Zero);
    }

    /// <summary>
    /// 防止窗口被最小化（拦截 WMSIZE 中的 SIZE_MINIMIZED）。
    /// 此方法通常在 WndProc 中处理，在收到 SIZE_MINIMIZED 时恢复窗口。
    /// </summary>
    /// <param name="hwnd">窗口句柄。</param>
    public static void RestoreFromMinimized(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        ShowWindow(hwnd, SW_RESTORE);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;
}
