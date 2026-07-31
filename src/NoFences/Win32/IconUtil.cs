using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace DeskOrganizer.NoFences.Win32;

/// <summary>
/// 系统图标工具类：获取文件/文件夹的系统图标。
/// </summary>
public static class IconUtil
{
    private const int SHGFI_ICON = 0x100;
    private const int SHGFI_LARGEICON = 0x0;
    private const int SHGFI_SMALLICON = 0x1;
    private const int SHGFI_USEFILEATTRIBUTES = 0x10;
    private const int SHGFI_SYSICONINDEX = 0x4000;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;

    // SHGetImageList 的标志
    private const int SHIL_LARGE = 0x0;
    private const int SHIL_SMALL = 0x1;
    private const int SHIL_EXTRALARGE = 0x2;
    private const int SHIL_JUMBO = 0x4;

    [DllImport("shell32.dll", EntryPoint = "#727")]
    private static extern int SHGetImageList(uint iImageList, ref Guid riid, ref IntPtr ppv);

    [DllImport("comctl32.dll")]
    private static extern IntPtr ImageList_GetIcon(IntPtr himl, int i, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct SHFILEINFOW
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        out SHFILEINFOW psfi,
        uint cbSizeFileInfo,
        uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr hgdiobj, int cbBuffer, out BITMAP lpvObject);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines,
        [Out] byte[] lpvBits, ref BITMAPINFO lpbi, uint uUsage);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr ho);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public uint[] bmiColors;
    }

    /// <summary>
    /// 获取指定文件的系统图标，直接返回缩放后的 Bitmap。
    /// 优先使用 SHGetImageList 获取大尺寸图标（48x48+），避免缩放模糊。
    /// </summary>
    public static Bitmap? GetFileIconBitmap(string path, int size)
    {
        // 优先尝试获取 Extra Large (48x48) 或 Jumbo (256x256) 图标
        var hIcon = GetLargeIconHandle(path, size);
        if (hIcon == IntPtr.Zero)
            hIcon = GetFileIconHandle(path, size);
        if (hIcon == IntPtr.Zero) return null;
        return HIconToBitmap(hIcon, size);
    }

    /// <summary>
    /// 通过 SHGetImageList 获取大尺寸图标句柄（48x48 或 256x256）。
    /// </summary>
    private static IntPtr GetLargeIconHandle(string path, int size)
    {
        if (string.IsNullOrWhiteSpace(path)) return IntPtr.Zero;

        try
        {
            // 先获取系统图标索引
            uint flags = SHGFI_SYSICONINDEX;
            if (size >= 48)
                flags |= SHGFI_LARGEICON;
            else
                flags |= SHGFI_SMALLICON;

            uint fileAttributes = 0;
            bool pathExists = File.Exists(path) || Directory.Exists(path);
            if (!pathExists)
            {
                fileAttributes = 0x80;
                flags |= SHGFI_USEFILEATTRIBUTES;
            }

            SHFILEINFOW shfi;
            IntPtr result = SHGetFileInfo(path, fileAttributes, out shfi,
                (uint)Marshal.SizeOf<SHFILEINFOW>(), flags);

            if (result == IntPtr.Zero || shfi.iIcon == 0)
                return IntPtr.Zero;

            // 根据请求尺寸选择合适的 ImageList
            uint imageListFlag = size >= 64 ? (uint)SHIL_JUMBO : (uint)SHIL_EXTRALARGE;
            IntPtr imageList = IntPtr.Zero;
            Guid iid = new Guid("46EB5926-582E-4017-9FDF-E899822AA8B3"); // IImageList
            int hr = SHGetImageList(imageListFlag, ref iid, ref imageList);
            if (hr != 0 || imageList == IntPtr.Zero)
                return IntPtr.Zero;

            // 从 ImageList 获取图标
            var iconHandle = ImageList_GetIcon(imageList, shfi.iIcon, 0);
            return iconHandle;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// 获取系统文件夹图标，直接返回缩放后的 Bitmap。
    /// </summary>
    public static Bitmap? GetFolderIconBitmap(int size)
    {
        uint flags = SHGFI_ICON | SHGFI_USEFILEATTRIBUTES | SHGFI_LARGEICON;
        SHFILEINFOW shfi;
        IntPtr result = SHGetFileInfo(null!, FILE_ATTRIBUTE_DIRECTORY, out shfi,
            (uint)Marshal.SizeOf<SHFILEINFOW>(), flags);

        if (result == IntPtr.Zero || shfi.hIcon == IntPtr.Zero)
            return null;

        return HIconToBitmap(shfi.hIcon, size);
    }

    /// <summary>
    /// 获取指定文件的系统图标句柄（调用者负责 DestroyIcon）。
    /// </summary>
    private static IntPtr GetFileIconHandle(string path, int size)
    {
        if (string.IsNullOrWhiteSpace(path)) return IntPtr.Zero;

        uint flags = SHGFI_ICON;
        if (size >= 48)
            flags |= SHGFI_LARGEICON;
        else
            flags |= SHGFI_SMALLICON;

        uint fileAttributes = 0;
        bool pathExists = File.Exists(path) || Directory.Exists(path);
        if (!pathExists)
        {
            fileAttributes = 0x80;
            flags |= SHGFI_USEFILEATTRIBUTES;
        }

        SHFILEINFOW shfi;
        IntPtr result = SHGetFileInfo(path, fileAttributes, out shfi,
            (uint)Marshal.SizeOf<SHFILEINFOW>(), flags);

        if (result == IntPtr.Zero || shfi.hIcon == IntPtr.Zero)
            return IntPtr.Zero;

        return shfi.hIcon;
    }

    /// <summary>
    /// 将图标句柄转换为缩放后的 Bitmap。
    /// 使用 Icon.ToBitmap() 转换（.NET 内部正确处理 mask 透明度）。
    /// </summary>
    private static Bitmap? HIconToBitmap(IntPtr hIcon, int size)
    {
        if (hIcon == IntPtr.Zero) return null;
        Bitmap? result = null;
        try
        {
            var icon = Icon.FromHandle(hIcon);
            var iconBmp = icon.ToBitmap();

            // 使用 Format32bppArgb 支持透明度，避免图标透明部分变黑或空白
            result = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            // 设置 DPI 为 96，避免高 DPI 屏幕下 DrawImage 二次缩放导致模糊
            result.SetResolution(96f, 96f);
            using var g = Graphics.FromImage(result);
            // 高质量缩放设置，避免图标模糊
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(Color.Transparent);
            using var imgAttr = new System.Drawing.Imaging.ImageAttributes();
            imgAttr.SetWrapMode(System.Drawing.Drawing2D.WrapMode.TileFlipXY);
            var destRect = new Rectangle(0, 0, size, size);
            g.DrawImage(iconBmp, destRect, 0, 0, iconBmp.Width, iconBmp.Height, GraphicsUnit.Pixel, imgAttr);
            iconBmp.Dispose();
        }
        catch
        {
            result?.Dispose();
            result = null;
        }
        finally
        {
            // Icon.FromHandle 不接管 hIcon 所有权，必须手动释放，避免 GDI 句柄泄漏
            try { DestroyIcon(hIcon); } catch { }
        }
        return result;
    }

    /// <summary>
    /// 获取指定文件的系统图标（返回 Icon 对象，调用者负责 Dispose）。
    /// </summary>
    public static Icon? GetFileIcon(string path, int size)
    {
        var hIcon = GetFileIconHandle(path, size);
        if (hIcon == IntPtr.Zero) return null;
        try
        {
            // Icon.FromHandle(hIcon) 的 ownHandle=false，Dispose 不释放原句柄。
            // Clone() 通过 DuplicateIcon 创建新句柄；原 hIcon 必须手动 DestroyIcon，否则泄漏。
            var icon = Icon.FromHandle(hIcon);
            var clone = (Icon)icon.Clone();
            icon.Dispose();
            DestroyIcon(hIcon);
            return clone;
        }
        catch { DestroyIcon(hIcon); return null; }
    }

    /// <summary>
    /// 获取系统文件夹图标（返回 Icon 对象，调用者负责 Dispose）。
    /// </summary>
    public static Icon? GetFolderIcon(int size)
    {
        uint flags = SHGFI_ICON | SHGFI_USEFILEATTRIBUTES | SHGFI_LARGEICON;
        SHFILEINFOW shfi;
        IntPtr result = SHGetFileInfo(null!, FILE_ATTRIBUTE_DIRECTORY, out shfi,
            (uint)Marshal.SizeOf<SHFILEINFOW>(), flags);
        if (result == IntPtr.Zero || shfi.hIcon == IntPtr.Zero) return null;
        try
        {
            var icon = Icon.FromHandle(shfi.hIcon);
            var clone = (Icon)icon.Clone();
            icon.Dispose();
            DestroyIcon(shfi.hIcon);
            return clone;
        }
        catch { DestroyIcon(shfi.hIcon); return null; }
    }
}
