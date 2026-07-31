using System;

namespace DeskOrganizer.Win32;
/// </summary>
[Flags]
public enum ModifierKeys : uint
{
    /// <summary>无修饰键。</summary>
    None = 0x0000,

    /// <summary>ALT 键 (MOD_ALT = 0x0001)。</summary>
    Alt = 0x0001,

    /// <summary>CTRL 键 (MOD_CONTROL = 0x0002)。</summary>
    Ctrl = 0x0002,

    /// <summary>SHIFT 键 (MOD_SHIFT = 0x0004)。</summary>
    Shift = 0x0004,

    /// <summary>WIN 键 (MOD_WIN = 0x0008)。</summary>
    Win = 0x0008,

    /// <summary>NOREPEAT 标志 — 防止热键重复触发 (MOD_NOREPEAT = 0x4000)。</summary>
    NoRepeat = 0x4000
}
