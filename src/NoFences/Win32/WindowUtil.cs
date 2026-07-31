using System;
using System.Runtime.InteropServices;

namespace DeskOrganizer.NoFences.Win32;

/// <summary>
/// 窗口样式管理工具类：控制扩展样式、Z-Order、TopMost 等。
/// </summary>
public static class WindowUtil
{
    // 扩展窗口样式常量
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_APPWINDOW = 0x00040000;
    public const int WS_EX_LAYERED = 0x00080000;

    // 窗口样式常量
    public const int WS_POPUP = unchecked((int)0x80000000);
    public const int WS_VISIBLE = 0x10000000;

    // 窗口消息
    public const int WM_NCHITTEST = 0x0084;
    public const int WM_NCLBUTTONDOWN = 0x00A1;
    public const int WM_MOUSEACTIVATE = 0x0021;
    public const int WM_WINDOWPOSCHANGING = 0x0046;

    // NCHITTEST 返回值
    public const int HTCAPTION = 0x02;
    public const int HTCLIENT = 0x01;
    public const int HTLEFT = 0x0A;
    public const int HTRIGHT = 0x0B;
    public const int HTTOP = 0x0C;
    public const int HTBOTTOM = 0x0F;
    public const int HTTOPLEFT = 0x0D;
    public const int HTTOPRIGHT = 0x0E;
    public const int HTBOTTOMLEFT = 0x10;
    public const int HTBOTTOMRIGHT = 0x11;

    // MA_NOACTIVATE
    public const int MA_ACTIVATE = 1;
    public const int MA_NOACTIVATE = 3;
    public const int MA_NOACTIVATEANDEAT = 4;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hwnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hwnd, int nIndex, int dwNewLong);

    /// <summary>获取窗口扩展样式。</summary>
    public static int GetWindowLongEx(IntPtr hwnd, int nIndex) => GetWindowLong(hwnd, nIndex);

    /// <summary>设置窗口扩展样式。</summary>
    public static void SetWindowLongEx(IntPtr hwnd, int nIndex, int dwNewLong) => SetWindowLong(hwnd, nIndex, dwNewLong);

    [DllImport("user32.dll")]
    public static extern int SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private const int GWL_EXSTYLE = -20;
    private const int GWL_STYLE = -16;

    // SetWindowPos 标志
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_NOACTIVATE = 0x0010;

    // Z-Order 句柄
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private static readonly IntPtr HWND_TOP = new(0);
    private static readonly IntPtr HWND_BOTTOM = new(1);

    /// <summary>
    /// 获取窗口扩展样式。
    /// </summary>
    public static int GetExtendedStyle(IntPtr hwnd)
    {
        return GetWindowLong(hwnd, GWL_EXSTYLE);
    }

    /// <summary>
    /// 设置窗口扩展样式。
    /// </summary>
    public static void SetExtendedStyle(IntPtr hwnd, int exStyle)
    {
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);
        // 强制刷新窗口框架
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED | SWP_NOACTIVATE);
    }

    /// <summary>
    /// 添加扩展窗口样式（按位或）。
    /// </summary>
    public static void AddExtendedStyle(IntPtr hwnd, int styleToAdd)
    {
        int current = GetExtendedStyle(hwnd);
        SetExtendedStyle(hwnd, current | styleToAdd);
    }

    /// <summary>
    /// 移除扩展窗口样式（按位与取反）。
    /// </summary>
    public static void RemoveExtendedStyle(IntPtr hwnd, int styleToRemove)
    {
        int current = GetExtendedStyle(hwnd);
        SetExtendedStyle(hwnd, current & ~styleToRemove);
    }

    /// <summary>
    /// 启用工具窗口模式（从 Alt-Tab 列表中隐藏）。
    /// </summary>
    public static void EnableToolWindow(IntPtr hwnd)
    {
        AddExtendedStyle(hwnd, WS_EX_TOOLWINDOW);
        RemoveExtendedStyle(hwnd, WS_EX_APPWINDOW);
    }

    /// <summary>
    /// 禁用工具窗口模式。
    /// </summary>
    public static void DisableToolWindow(IntPtr hwnd)
    {
        RemoveExtendedStyle(hwnd, WS_EX_TOOLWINDOW);
    }

    /// <summary>
    /// 设置窗口为 TopMost（置顶）。
    /// </summary>
    public static void SetTopMost(IntPtr hwnd, bool topMost)
    {
        IntPtr insertAfter = topMost ? HWND_TOPMOST : HWND_NOTOPMOST;
        SetWindowPos(hwnd, insertAfter, 0, 0, 0, 0,
            SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
    }

    /// <summary>
    /// 将窗口置顶到所有窗口之上。
    /// </summary>
    public static void BringToFront(IntPtr hwnd)
    {
        SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0,
            SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
    }

    /// <summary>
    /// 将窗口置底到所有窗口之下。
    /// </summary>
    public static void SendToBack(IntPtr hwnd)
    {
        SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0,
            SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
    }

    /// <summary>
    /// 启用窗口不激活模式（点击窗口不会抢夺焦点）。
    /// </summary>
    public static void EnableNoActivate(IntPtr hwnd)
    {
        AddExtendedStyle(hwnd, WS_EX_NOACTIVATE);
    }

    /// <summary>
    /// 禁用窗口不激活模式。
    /// </summary>
    public static void DisableNoActivate(IntPtr hwnd)
    {
        RemoveExtendedStyle(hwnd, WS_EX_NOACTIVATE);
    }
}

/// <summary>WINDOWPOS 结构体，用于 WM_WINDOWPOSCHANGING 消息。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct WINDOWPOS
{
    public IntPtr hWnd;
    public IntPtr hWndInsertAfter;
    public int x;
    public int y;
    public int cx;
    public int cy;
    public uint flags;
}

/// <summary>WINDOWPOS 标志常量。</summary>
public static class SWPFlags
{
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOREDRAW = 0x0008;
    public const uint SWP_NOACTIVATE = 0x0010;
}
