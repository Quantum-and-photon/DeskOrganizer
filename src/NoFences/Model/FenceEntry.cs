using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using DeskOrganizer.NoFences.Win32;

namespace DeskOrganizer.NoFences.Model;

/// <summary>
/// 栅栏中的单个条目（文件或文件夹）。
/// </summary>
public class FenceEntry
{
    /// <summary>文件或文件夹的完整路径。</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>条目类型。</summary>
    public EntryType EntryType { get; set; }

    /// <summary>快捷方式的目标路径（仅 Shortcut 类型有值）。</summary>
    public string? TargetPath { get; set; }

    /// <summary>自定义显示名称（为 null 时使用 DisplayName）。</summary>
    public string? CustomName { get; set; }

    /// <summary>缩略图（可能为 null，表示尚未加载或加载失败）。</summary>
    public Bitmap? Thumbnail { get; set; }

    /// <summary>缩略图是否已请求加载。</summary>
    public bool ThumbnailRequested { get; set; }

    /// <summary>条目显示名称（优先使用自定义名称，否则使用文件名，去除 .lnk/.url 后缀）。</summary>
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(CustomName))
                return CustomName;
            var name = Path.GetFileName(FilePath);
            if (name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
                name = Path.GetFileNameWithoutExtension(name);
            return name;
        }
    }

    /// <summary>
    /// 解析 .lnk 快捷方式的目标路径（使用 COM Shell Link 接口）。
    /// </summary>
    public static string? ResolveShortcut(string lnkPath)
    {
        if (!File.Exists(lnkPath) || !lnkPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            var link = (Shell32.IShellLink)new Shell32.ShellLinkCo();
            var persistFile = (Shell32.IPersistFile)link;
            persistFile.Load(lnkPath, 0);

            var sb = new System.Text.StringBuilder(260);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            link.Resolve(IntPtr.Zero, 1); // SLR_ANY_MATCH
            var result = sb.ToString();
            return string.IsNullOrEmpty(result) ? null : result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 从 .url 文件中解析自定义图标（IconFile/IconIndex）。
    /// .url 文件是 INI 格式，[InternetShortcut] 节包含 IconFile 和 IconIndex。
    /// </summary>
    private static Bitmap? ExtractUrlIcon(string urlPath, int size)
    {
        try
        {
            var lines = File.ReadAllLines(urlPath);
            string? iconFile = null;
            int iconIndex = 0;
            bool inShortcutSection = false;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("[", StringComparison.Ordinal))
                {
                    inShortcutSection = trimmed.Equals("[InternetShortcut]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inShortcutSection) continue;

                if (trimmed.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase))
                    iconFile = trimmed.Substring("IconFile=".Length).Trim();
                else if (trimmed.StartsWith("IconIndex=", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(trimmed.Substring("IconIndex=".Length).Trim(), out iconIndex);
            }

            if (string.IsNullOrEmpty(iconFile) || !File.Exists(iconFile))
                return null;

            // 从 IconFile 提取图标（支持 .ico/.exe/.dll）
            if (iconFile.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
            {
                using var icon = new System.Drawing.Icon(iconFile, size, size);
                var result = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                result.SetResolution(96f, 96f);
                using (var g = System.Drawing.Graphics.FromImage(result))
                {
                    g.Clear(System.Drawing.Color.Transparent);
                    g.DrawIcon(icon, new System.Drawing.Rectangle(0, 0, size, size));
                }
                return result;
            }

            // 从 exe/dll 提取图标
            var exeIcon = System.Drawing.Icon.ExtractAssociatedIcon(iconFile);
            if (exeIcon != null)
            {
                using (exeIcon)
                {
                    var iconBmp = exeIcon.ToBitmap();
                    var result = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    result.SetResolution(96f, 96f);
                    using (var g = System.Drawing.Graphics.FromImage(result))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.Clear(System.Drawing.Color.Transparent);
                        g.DrawImage(iconBmp, 0, 0, size, size);
                    }
                    iconBmp.Dispose();
                    return result;
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 异步提取文件图标并缩放到指定尺寸。
    /// </summary>
    public static Bitmap? ExtractIcon(string path, int size)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            // 方式1：直接通过 SHGetFileInfo 获取 Bitmap
            var bmp = IconUtil.GetFileIconBitmap(path, size);
            if (bmp != null)
            {
                Log("ExtractIcon OK", path, $"via GetFileIconBitmap {bmp.Width}x{bmp.Height}");
                return bmp;
            }

            // 方式2：对 .lnk 解析目标路径后重试
            if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            {
                var target = ResolveShortcut(path);
                if (!string.IsNullOrEmpty(target))
                {
                    bmp = IconUtil.GetFileIconBitmap(target, size);
                    if (bmp != null)
                    {
                        Log("ExtractIcon OK", path, $"via target {target}");
                        return bmp;
                    }
                }
            }

            // 方式2.5：对 .url 解析 IconFile 获取自定义图标
            if (path.EndsWith(".url", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            {
                bmp = ExtractUrlIcon(path, size);
                if (bmp != null)
                {
                    Log("ExtractIcon OK", path, "via .url IconFile");
                    return bmp;
                }
            }

            // 方式3：文件夹图标
            if (Directory.Exists(path))
            {
                bmp = IconUtil.GetFolderIconBitmap(size);
                if (bmp != null)
                {
                    Log("ExtractIcon OK", path, "via GetFolderIconBitmap");
                    return bmp;
                }
            }

            // 方式4：ExtractAssociatedIcon
            if (File.Exists(path))
            {
                try
                {
                    using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                    if (icon != null)
                    {
                        using var ib = icon.ToBitmap();
                        var result = new Bitmap(size, size, PixelFormat.Format32bppArgb);
                        result.SetResolution(96f, 96f);
                        using var g = Graphics.FromImage(result);
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                        g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                        g.Clear(Color.Transparent);
                        using var imgAttr = new System.Drawing.Imaging.ImageAttributes();
                        imgAttr.SetWrapMode(System.Drawing.Drawing2D.WrapMode.TileFlipXY);
                        var destRect = new Rectangle(0, 0, size, size);
                        g.DrawImage(ib, destRect, 0, 0, ib.Width, ib.Height, GraphicsUnit.Pixel, imgAttr);
                        Log("ExtractIcon OK", path, "via ExtractAssociatedIcon");
                        return result;
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"ExtractIcon ExtractAssociatedIcon failed for {path}: {ex.Message}"); }
            }

            Log("ExtractIcon FAIL", path, "all methods returned null");
            return null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ExtractIcon FAIL: {Path.GetFileName(path)} - {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 使用系统默认程序打开文件或文件夹。
    /// </summary>
    public void Open()
    {
        if (string.IsNullOrWhiteSpace(FilePath) || (!File.Exists(FilePath) && !Directory.Exists(FilePath)))
            return;

        try
        {
            ProcessStartInfo psi = new(FilePath)
            {
                UseShellExecute = true
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            System.Windows.Forms.MessageBox.Show(
                $"无法打开文件: {ex.Message}",
                "打开失败",
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Error);
        }
    }

    private static void Log(string status, string path, string detail)
    {
        var logDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DeskOrganizer");
        System.IO.Directory.CreateDirectory(logDir);
        try
        {
            var fileName = System.IO.Path.GetFileName(path);
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {status}: {fileName} - {detail}\r\n";
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(logDir, "icon_debug.log"), line);
        }
        catch { }
    }
}
