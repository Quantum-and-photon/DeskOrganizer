using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DeskOrganizer.Win32;

/// <summary>
/// 综合性 Win32 API 封装层，提供全局热键注册、窗口管理、Shell 操作等功能。
/// 所有 P/Invoke 声明集中在此，便于统一维护和管理。
/// </summary>
public static class Win32Helper
{
    // ================================================================
    //  常量定义
    // ================================================================

    /// <summary>WM_HOTKEY 消息 ID。</summary>
    public const int WM_HOTKEY = 0x0312;

    /// <summary>HWND_BOTTOM — 窗口置于 Z 序底部。</summary>
    public static readonly IntPtr HWND_BOTTOM = new(1);

    /// <summary>HWND_TOPMOST — 窗口置顶。</summary>
    public static readonly IntPtr HWND_TOPMOST = new(-1);

    /// <summary>HWND_NOTOPMOST — 取消窗口置顶。</summary>
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);

    /// <summary>默认热键搜索：Alt+Space。</summary>
    public const uint HOTKEY_SEARCH_ID = 1;

    /// <summary>默认搜索热键的虚拟键码 (VK_SPACE)。</summary>
    public const uint VK_SPACE = 0x20;

    /// <summary>默认搜索热键的修饰键 (MOD_ALT)。</summary>
    public const ModifierKeys HOTKEY_SEARCH_MOD = ModifierKeys.Alt;

    // SetWindowPos 标志
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    // GetAncestor 标志
    private const uint GA_PARENT = 1;
    private const uint GA_ROOT = 2;
    private const uint GA_ROOTOWNER = 3;

    // ShellExecute 显示方式
    private const int SW_SHOWNORMAL = 1;

    // ================================================================
    //  P/Invoke 声明
    // ================================================================

    /// <summary>注册全局热键。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(
        IntPtr hWnd,
        int id,
        uint fsModifiers,
        uint vk);

    /// <summary>注销全局热键。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(
        IntPtr hWnd,
        int id);

    /// <summary>设置窗口位置和 Z 序。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int X,
        int Y,
        int cx,
        int cy,
        uint uFlags);

    /// <summary>获取当前前台窗口句柄。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetForegroundWindow();

    /// <summary>获取桌面窗口句柄。</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr GetDesktopWindow();

    /// <summary>获取窗口样式/GWL_EXSTYLE (-20)。</summary>
    public const int GWL_EXSTYLE = -20;

    /// <summary>获取窗口信息。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    /// <summary>设置窗口信息。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    /// <summary>将窗口置于 Z 序底部（所有窗口之下，桌面之上）。</summary>
    public static void SetBottomWindow(IntPtr hWnd)
    {
        SetWindowPos(hWnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
    }

    /// <summary>释放鼠标捕获。</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReleaseCapture();

    /// <summary>发送消息到窗口。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern IntPtr SendMessage(IntPtr hWnd, int Msg, int wParam, IntPtr lParam);

    /// <summary>获取指定窗口的父窗口句柄。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetParent(IntPtr hWnd);

    /// <summary>获取指定窗口的祖先窗口句柄。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    /// <summary>通过 Shell 执行操作（打开文件/URL 等）。</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr ShellExecuteW(
        IntPtr hWnd,
        string lpOperation,
        string lpFile,
        string lpParameters,
        string lpDirectory,
        int nShowCmd);

    /// <summary>获取指定窗口的线程 ID 和进程 ID。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    /// <summary>将指定窗口带至 Z 序顶部。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    /// <summary>获取工作区域（排除任务栏）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SystemParametersInfo(
        uint uiAction,
        uint uiParam,
        ref RECT pvParam,
        uint fWinIni);

    private const uint SPI_GETWORKAREA = 0x0030;

    // ================================================================
    //  结构体
    // ================================================================

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    // ================================================================
    //  公开辅助方法
    // ================================================================

    /// <summary>
    /// 注册全局热键。
    /// </summary>
    /// <param name="hWnd">接收 WM_HOTKEY 消息的窗口句柄。</param>
    /// <param name="id">热键标识符（应用程序内唯一）。</param>
    /// <param name="modifiers">修饰键组合。</param>
    /// <param name="key">虚拟键码。</param>
    /// <returns>注册成功返回 true。</returns>
    public static bool RegisterGlobalHotKey(IntPtr hWnd, int id, ModifierKeys modifiers, uint key)
    {
        var result = RegisterHotKey(hWnd, id, (uint)modifiers, key);
        if (!result)
        {
            var error = Marshal.GetLastWin32Error();
            var msg = $"RegisterHotKey 失败，错误码: 0x{error:X8}，ID: {id}，组合键: {modifiers}+{key}";
            System.Diagnostics.Debug.WriteLine(msg);
            try { App.Log($"[Win32Helper] {msg}"); } catch { }
        }
        return result;
    }

    /// <summary>
    /// 注销全局热键。
    /// </summary>
    /// <param name="hWnd">窗口句柄。</param>
    /// <param name="id">热键标识符。</param>
    /// <returns>注销成功返回 true。</returns>
    public static bool UnregisterGlobalHotKey(IntPtr hWnd, int id)
    {
        var result = UnregisterHotKey(hWnd, id);
        if (!result)
        {
            var error = Marshal.GetLastWin32Error();
            System.Diagnostics.Debug.WriteLine(
                $"UnregisterHotKey 失败，错误码: 0x{error:X8}，ID: {id}");
        }
        return result;
    }

    /// <summary>
    /// 将窗口设置为置顶或取消置顶。
    /// </summary>
    /// <param name="hWnd">窗口句柄。</param>
    /// <param name="topMost">true 置顶，false 取消置顶。</param>
    public static void SetTopMost(IntPtr hWnd, bool topMost)
    {
        var insertAfter = topMost ? HWND_TOPMOST : HWND_NOTOPMOST;
        SetWindowPos(hWnd, insertAfter, 0, 0, 0, 0,
            SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
    }

    /// <summary>
    /// 注册默认的搜索热键（Alt+Space）。
    /// </summary>
    /// <param name="hWnd">接收消息的窗口句柄。</param>
    /// <returns>注册成功返回 true。</returns>
    public static bool RegisterDefaultSearchHotKey(IntPtr hWnd)
    {
        return RegisterGlobalHotKey(
            hWnd,
            (int)HOTKEY_SEARCH_ID,
            ModifierKeys.Alt,
            VK_SPACE);
    }

    /// <summary>
    /// 注销默认的搜索热键。
    /// </summary>
    /// <param name="hWnd">窗口句柄。</param>
    /// <returns>注销成功返回 true。</returns>
    public static bool UnregisterDefaultSearchHotKey(IntPtr hWnd)
    {
        return UnregisterGlobalHotKey(hWnd, (int)HOTKEY_SEARCH_ID);
    }

    /// <summary>
    /// 使用系统默认程序打开文件或 URL。
    /// </summary>
    /// <param name="filePath">文件路径或 URL。</param>
    /// <param name="parentHwnd">父窗口句柄（可为 IntPtr.Zero）。</param>
    /// <returns>操作成功（返回值大于 32）返回 true。</returns>
    public static bool ShellOpen(string filePath, IntPtr parentHWnd = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        var result = ShellExecuteW(
            parentHWnd,
            "open",
            filePath,
            string.Empty,
            string.Empty,
            SW_SHOWNORMAL);

        // ShellExecute 返回值大于 32 表示成功
        return result.ToInt64() > 32;
    }

    /// <summary>
    /// 使用资源管理器打开并选中指定文件。
    /// </summary>
    /// <param name="filePath">要选中的文件路径。</param>
    /// <param name="parentHwnd">父窗口句柄。</param>
    /// <returns>操作成功返回 true。</returns>
    public static bool ShellExploreAndSelect(string filePath, IntPtr parentHWnd = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        if (!File.Exists(filePath))
            return false;

        var result = ShellExecuteW(
            parentHWnd,
            "explore",
            $"/select,\"{filePath}\"",
            string.Empty,
            Path.GetDirectoryName(filePath) ?? string.Empty,
            SW_SHOWNORMAL);

        return result.ToInt64() > 32;
    }

    /// <summary>
    /// 获取窗口的工作区域（排除任务栏的屏幕区域）。
    /// </summary>
    /// <returns>工作区域的 RECT 结构。</returns>
    public static RECT GetWorkArea()
    {
        var rect = new RECT();
        SystemParametersInfo(SPI_GETWORKAREA, 0, ref rect, 0);
        return rect;
    }
}
