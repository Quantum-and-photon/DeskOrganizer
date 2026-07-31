using System;
using System.Runtime.InteropServices;

namespace DeskOrganizer.NoFences.Win32;

/// <summary>
/// DWM 投影阴影工具类，使用 DwmExtendFrameIntoClientArea 实现。
/// </summary>
public static class DropShadow
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS pMarInset);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attr, ref int attrValue, int attrSize);

    private const uint DWMWA_NCRENDERING_POLICY = 2;
    private const uint DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMNCRP_ENABLED = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;

        public MARGINS(int all)
        {
            cxLeftWidth = all;
            cxRightWidth = all;
            cyTopHeight = all;
            cyBottomHeight = all;
        }
    }

    /// <summary>
    /// 为窗口启用 DWM 投影阴影效果。
    /// </summary>
    /// <param name="hwnd">窗口句柄。</param>
    public static void Enable(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        // 先启用 NC 渲染
        int renderPolicy = DWMNCRP_ENABLED;
        DwmSetWindowAttribute(hwnd, DWMWA_NCRENDERING_POLICY, ref renderPolicy, sizeof(int));

        // 扩展帧到客户区以触发阴影
        var margins = new MARGINS(1);
        DwmExtendFrameIntoClientArea(hwnd, ref margins);
    }

    /// <summary>
    /// 为窗口禁用 DWM 投影阴影。
    /// </summary>
    /// <param name="hwnd">窗口句柄。</param>
    public static void Disable(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        var margins = new MARGINS(0);
        DwmExtendFrameIntoClientArea(hwnd, ref margins);
    }
}
