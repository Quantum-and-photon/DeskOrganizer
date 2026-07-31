using System;
using System.Runtime.InteropServices;

namespace DeskOrganizer.Win32;

/// <summary>
/// Windows 虚拟桌面切换辅助类。
/// 通过 SendInput 模拟 Win+Ctrl+方向键 来切换虚拟桌面。
/// Windows 10 和 Windows 11 均支持。
/// </summary>
public static class VirtualDesktopHelper
{
    /// <summary>
    /// 通过模拟键盘快捷键切换虚拟桌面。
    /// 先回到第一个桌面，再移动到目标桌面。
    /// </summary>
    public static void SwitchToDesktop(int index)
    {
        if (index < 1) return;

        try
        {
            // 回到第一个桌面（最多发送 9 次 Win+Ctrl+左）
            for (int i = 0; i < 9; i++)
            {
                SendDesktopSwitchKey(goLeft: true);
                System.Threading.Thread.Sleep(80);
            }

            // 移动到目标桌面（发送 index-1 次 Win+Ctrl+右）
            for (int i = 1; i < index; i++)
            {
                SendDesktopSwitchKey(goLeft: false);
                System.Threading.Thread.Sleep(80);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[VirtualDesktop] Switch error: {ex.Message}");
        }
    }

    /// <summary>
    /// 发送 Win+Ctrl+方向键 来切换虚拟桌面。
    /// </summary>
    private static void SendDesktopSwitchKey(bool goLeft)
    {
        ushort arrowVk = goLeft ? (ushort)0x25 : (ushort)0x27; // VK_LEFT : VK_RIGHT

        // Win key down
        SendKey((ushort)0x5B, keyDown: true);  // VK_LWIN
        // Ctrl key down
        SendKey((ushort)0x11, keyDown: true);  // VK_CONTROL
        System.Threading.Thread.Sleep(10);
        // Arrow key press/release
        SendKey(arrowVk, keyDown: true);
        System.Threading.Thread.Sleep(10);
        SendKey(arrowVk, keyDown: false);
        System.Threading.Thread.Sleep(10);
        // Ctrl up
        SendKey((ushort)0x11, keyDown: false);
        // Win up
        SendKey((ushort)0x5B, keyDown: false);
    }

    #region SendInput P/Invoke

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    private static void SendKey(ushort vk, bool keyDown)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = 0,
                    dwFlags = keyDown ? 0u : KEYEVENTF_KEYUP,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };

        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    #endregion
}
