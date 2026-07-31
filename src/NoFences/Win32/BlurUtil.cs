using System;
using System.Runtime.InteropServices;

namespace DeskOrganizer.NoFences.Win32;

/// <summary>
/// 窗口模糊效果工具类，使用 SetWindowCompositionAttribute 实现 Windows Aero 模糊。
/// </summary>
public static class BlurUtil
{
    private const int ACCENT_ENABLE_BLURBEHIND = 3;
    private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;
    private const int ACCENT_INVALID_STATE = 0;

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    public enum AccentState
    {
        ACCENT_DISABLED = 0,
        ACCENT_ENABLE_GRADIENT = 1,
        ACCENT_ENABLE_TRANSPARENTGRADIENT = 2,
        ACCENT_ENABLE_BLURBEHIND = 3,
        ACCENT_ENABLE_ACRYLICBLURBEHIND = 4,
        ACCENT_ENABLE_HOSTBACKDROP = 5,
        ACCENT_INVALID_STATE = 6
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public AccentState AccentState;
        public uint AccentFlags;
        public uint GradientColor;
        public uint AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public WindowCompositionAttribute Attribute;
        public IntPtr Data;
        public uint SizeOfData;
    }

    private enum WindowCompositionAttribute
    {
        WCA_ACCENT_POLICY = 19
    }

    /// <summary>
    /// 为窗口启用透明渐变效果（背景透明透出桌面，但前景内容不模糊）。
    /// 这是真正的分层：背景半透明，图标文字清晰。
    /// </summary>
    /// <param name="hwnd">窗口句柄。</param>
    /// <param name="gradientColor">渐变颜色（ABGR 格式，A 为背景不透明度）。</param>
    public static void EnableTransparentGradient(IntPtr hwnd, uint gradientColor = 0x80000000)
    {
        if (hwnd == IntPtr.Zero) return;

        var accent = new AccentPolicy
        {
            AccentState = AccentState.ACCENT_ENABLE_TRANSPARENTGRADIENT,
            GradientColor = gradientColor
        };

        var data = new WindowCompositionAttributeData
        {
            Attribute = WindowCompositionAttribute.WCA_ACCENT_POLICY,
            Data = Marshal.AllocHGlobal(Marshal.SizeOf(accent)),
            SizeOfData = (uint)Marshal.SizeOf(accent)
        };

        Marshal.StructureToPtr(accent, data.Data, false);
        SetWindowCompositionAttribute(hwnd, ref data);
        Marshal.FreeHGlobal(data.Data);
    }

    /// <summary>
    /// 为窗口启用 Aero Blur 毛玻璃模糊效果。
    /// </summary>
    /// <param name="hwnd">窗口句柄。</param>
    /// <param name="gradientColor">渐变颜色（ABGR 格式，A 为不透明度）。</param>
    public static void EnableBlur(IntPtr hwnd, uint gradientColor = 0x40000000)
    {
        if (hwnd == IntPtr.Zero) return;

        var accent = new AccentPolicy
        {
            AccentState = AccentState.ACCENT_ENABLE_BLURBEHIND,
            GradientColor = gradientColor
        };

        var data = new WindowCompositionAttributeData
        {
            Attribute = WindowCompositionAttribute.WCA_ACCENT_POLICY,
            Data = Marshal.AllocHGlobal(Marshal.SizeOf(accent)),
            SizeOfData = (uint)Marshal.SizeOf(accent)
        };

        Marshal.StructureToPtr(accent, data.Data, false);
        SetWindowCompositionAttribute(hwnd, ref data);
        Marshal.FreeHGlobal(data.Data);
    }

    /// <summary>
    /// 为窗口启用 Acrylic 亚克力模糊效果（Windows 10 1803+）。
    /// </summary>
    /// <param name="hwnd">窗口句柄。</param>
    /// <param name="gradientColor">渐变颜色（ABGR 格式）。</param>
    public static void EnableAcrylic(IntPtr hwnd, uint gradientColor = 0x99000000)
    {
        if (hwnd == IntPtr.Zero) return;

        var accent = new AccentPolicy
        {
            AccentState = AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND,
            GradientColor = gradientColor
        };

        var data = new WindowCompositionAttributeData
        {
            Attribute = WindowCompositionAttribute.WCA_ACCENT_POLICY,
            Data = Marshal.AllocHGlobal(Marshal.SizeOf(accent)),
            SizeOfData = (uint)Marshal.SizeOf(accent)
        };

        Marshal.StructureToPtr(accent, data.Data, false);
        SetWindowCompositionAttribute(hwnd, ref data);
        Marshal.FreeHGlobal(data.Data);
    }

    /// <summary>
    /// 移除窗口模糊效果。
    /// </summary>
    public static void DisableBlur(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        var accent = new AccentPolicy
        {
            AccentState = AccentState.ACCENT_DISABLED
        };

        var data = new WindowCompositionAttributeData
        {
            Attribute = WindowCompositionAttribute.WCA_ACCENT_POLICY,
            Data = Marshal.AllocHGlobal(Marshal.SizeOf(accent)),
            SizeOfData = (uint)Marshal.SizeOf(accent)
        };

        Marshal.StructureToPtr(accent, data.Data, false);
        SetWindowCompositionAttribute(hwnd, ref data);
        Marshal.FreeHGlobal(data.Data);
    }

    /// <summary>
    /// 将 Color 转换为 ABGR 格式的 GradientColor。
    /// </summary>
    public static uint ColorToAbgr(System.Drawing.Color color, byte alpha = 64)
    {
        return (uint)(alpha << 24) | (uint)(color.B << 16) | (uint)(color.G << 8) | color.R;
    }
}
