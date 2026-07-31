using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace DeskOrganizer.NoFences.Util;

/// <summary>
/// Graphics 绘图辅助扩展方法。
/// </summary>
public static class Extensions
{
    /// <summary>
    /// 绘制带圆角的填充矩形。
    /// </summary>
    public static void FillRoundedRectangle(this Graphics g, Brush brush, Rectangle rect, float radius)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;
        radius = Math.Max(0, Math.Min(radius, Math.Min(rect.Width / 2f, rect.Height / 2f)));

        using var path = CreateRoundedRectPath(rect, radius);
        g.FillPath(brush, path);
    }

    /// <summary>
    /// 绘制带圆角的矩形边框。
    /// </summary>
    public static void DrawRoundedRectangle(this Graphics g, Pen pen, Rectangle rect, float radius)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;
        radius = Math.Max(0, Math.Min(radius, Math.Min(rect.Width / 2f, rect.Height / 2f)));

        using var path = CreateRoundedRectPath(rect, radius);
        g.DrawPath(pen, path);
    }

    /// <summary>
    /// 在指定矩形区域内居中绘制文字。
    /// </summary>
    public static void DrawCenteredString(this Graphics g, string text, Font font, Brush brush, RectangleF rect)
    {
        if (string.IsNullOrEmpty(text)) return;

        var size = g.MeasureString(text, font);
        var x = rect.X + (rect.Width - size.Width) / 2f;
        var y = rect.Y + (rect.Height - size.Height) / 2f;
        g.DrawString(text, font, brush, x, y);
    }

    /// <summary>
    /// 在指定矩形区域内居中绘制文字，支持自动换行（单行截断）。
    /// </summary>
    public static void DrawCenteredTruncatedString(this Graphics g, string text, Font font, Brush brush, RectangleF rect)
    {
        if (string.IsNullOrEmpty(text)) return;

        var format = new StringFormat
        {
            Trimming = StringTrimming.EllipsisCharacter,
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        g.DrawString(text, font, brush, rect, format);
    }

    /// <summary>
    /// 在指定矩形区域左侧绘制文字，支持自动换行（单行截断）。
    /// </summary>
    public static void DrawLeftTruncatedString(this Graphics g, string text, Font font, Brush brush, RectangleF rect)
    {
        if (string.IsNullOrEmpty(text)) return;

        var format = new StringFormat
        {
            Trimming = StringTrimming.EllipsisCharacter,
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Center
        };
        g.DrawString(text, font, brush, rect, format);
    }

    /// <summary>
    /// 创建圆角矩形路径。
    /// </summary>
    private static GraphicsPath CreateRoundedRectPath(Rectangle rect, float radius)
    {
        var path = new GraphicsPath();
        var r = radius;
        var x = rect.X;
        var y = rect.Y;
        var w = rect.Width;
        var h = rect.Height;

        path.AddArc(x, y, r * 2, r * 2, 180, 90);
        path.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
        path.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
        path.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
        path.CloseFigure();

        return path;
    }

    /// <summary>
    /// 将颜色转换为半透明色。
    /// </summary>
    public static Color WithAlpha(this Color color, int alpha)
    {
        return Color.FromArgb(Math.Clamp(alpha, 0, 255), color);
    }

    /// <summary>
    /// 将十六进制颜色字符串解析为 Color。
    /// </summary>
    public static Color FromHex(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 6)
        {
            return Color.FromArgb(
                int.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber),
                int.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber),
                int.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber));
        }

        if (hex.Length == 8)
        {
            return Color.FromArgb(
                int.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber),
                int.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber),
                int.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber),
                int.Parse(hex.Substring(6, 2), System.Globalization.NumberStyles.HexNumber));
        }

        return Color.Transparent;
    }
}
