using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;

namespace DeskOrganizer.Win32;

/// <summary>
/// 提供 Shell 文件图标的提取功能，通过 SHGetFileInfo API 实现。
/// 支持文件图标、文件夹图标、以及按扩展名获取默认图标。
/// </summary>
public static class IconHelper
{
    // ---------- SHGetFileInfo 相关常量 ----------

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_SMALLICON = 0x000000001;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;

    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

    // ---------- SHFILEINFO 结构体 ----------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    // ---------- P/Invoke ----------

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref SHFILEINFO psfi,
        uint cbSizeFileInfo,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    // ---------- 公开方法 ----------

    /// <summary>
    /// 获取指定文件的图标。
    /// </summary>
    /// <param name="path">文件完整路径。</param>
    /// <param name="large">true 返回大图标 (32x32)，false 返回小图标 (16x16)。</param>
    /// <returns>提取到的图标，失败返回 null。</returns>
    public static Icon? GetFileIcon(string path, bool large = false)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (!File.Exists(path) && !Directory.Exists(path))
            return null;

        return GetIconInternal(path, FILE_ATTRIBUTE_NORMAL, large, useFileAttributes: false);
    }

    /// <summary>
    /// 根据文件扩展名获取默认关联图标（无需真实文件存在）。
    /// </summary>
    /// <param name="extension">文件扩展名（含前导点，例如 ".txt"）。</param>
    /// <param name="large">true 返回大图标，false 返回小图标。</param>
    /// <returns>提取到的图标，失败返回 null。</returns>
    public static Icon? GetDefaultIconForExtension(string extension, bool large = false)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return null;

        // 确保扩展名以点开头
        var ext = extension.StartsWith('.') ? extension : '.' + extension;

        // 构造虚拟文件名以利用 SHGFI_USEFILEATTRIBUTES
        var dummyName = $"dummy{ext}";

        return GetIconInternal(dummyName, FILE_ATTRIBUTE_NORMAL, large, useFileAttributes: true);
    }

    /// <summary>
    /// 获取文件夹图标。
    /// </summary>
    /// <param name="large">true 返回大图标，false 返回小图标。</param>
    /// <returns>提取到的图标，失败返回 null。</returns>
    public static Icon? GetFolderIcon(bool large = false)
    {
        return GetIconInternal(null, FILE_ATTRIBUTE_DIRECTORY, large, useFileAttributes: true);
    }

    // ---------- 内部实现 ----------

    private static Icon? GetIconInternal(string? path, uint fileAttributes, bool large, bool useFileAttributes)
    {
        var shfi = new SHFILEINFO();
        var flags = SHGFI_ICON | (large ? SHGFI_LARGEICON : SHGFI_SMALLICON);

        if (useFileAttributes)
            flags |= SHGFI_USEFILEATTRIBUTES;

        var cbSize = (uint)Marshal.SizeOf<SHFILEINFO>();

        // 使用默认路径进行文件夹图标的查询
        var pszPath = path ?? "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";

        var result = SHGetFileInfo(pszPath, fileAttributes, ref shfi, cbSize, flags);

        if (result == IntPtr.Zero || shfi.hIcon == IntPtr.Zero)
            return null;

        try
        {
            return Icon.FromHandle(shfi.hIcon);
        }
        catch
        {
            return null;
        }
        finally
        {
            // SHGetFileInfo 返回的图标句柄需要手动销毁
            if (shfi.hIcon != IntPtr.Zero)
            {
                DestroyIcon(shfi.hIcon);
            }
        }
    }
}
