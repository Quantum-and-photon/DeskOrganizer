using System;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using DeskOrganizer.Win32;

namespace DeskOrganizer;

public class SearchResultItem
{
    public string FilePath { get; set; } = string.Empty;
    public BitmapSource? Icon { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string DisplayPath { get; set; } = string.Empty;
    public string DisplaySize { get; set; } = string.Empty;
    public string DisplayDate { get; set; } = string.Empty;

    public SearchResultItem() { }

    public SearchResultItem(SearchResult result)
    {
        FilePath = result.FilePath;
        DisplayName = result.FileName;
        DisplayPath = Path.GetDirectoryName(result.FilePath) ?? result.FilePath;
        DisplaySize = FormatSize(result.Size);
        DisplayDate = result.ModifiedDate.ToString("yyyy-MM-dd HH:mm");

        // Extract icon from file
        System.Drawing.Icon? icon = null;
        try
        {
            icon = IconHelper.GetFileIcon(result.FilePath, large: false);
            if (icon != null)
            {
                Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    icon.Handle,
                    System.Windows.Int32Rect.Empty,
                    BitmapSizeOptions.FromWidth(24));

                if (Icon == null)
                {
                    Icon = CreateFallbackIcon();
                }
            }
            else
            {
                Icon = CreateFallbackIcon();
            }
        }
        catch
        {
            Icon = CreateFallbackIcon();
        }
        finally
        {
            icon?.Dispose();
        }
    }

    private static BitmapSource? CreateFallbackIcon()
    {
        try
        {
            using var bmp = new System.Drawing.Bitmap(16, 16);
            using var g = System.Drawing.Graphics.FromImage(bmp);
            g.Clear(System.Drawing.Color.FromArgb(200, 200, 200));
            var hBitmap = bmp.GetHbitmap();
            return System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap,
                IntPtr.Zero,
                System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromWidth(16));
        }
        catch
        {
            return null;
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 0) return "0 B";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }
}
