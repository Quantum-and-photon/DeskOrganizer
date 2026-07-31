using System;

namespace DeskOrganizer.Model;

/// <summary>
/// 桌面便签数据模型，记录便签的内容、布局和样式信息。
/// </summary>
public class StickyNote
{
    /// <summary>便签唯一标识符。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>便签标题。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>便签正文内容（Markdown 格式）。</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>便签左上角 X 坐标 (DIP)。</summary>
    public double X { get; set; }

    /// <summary>便签左上角 Y 坐标 (DIP)。</summary>
    public double Y { get; set; }

    /// <summary>便签宽度 (DIP)。</summary>
    public double Width { get; set; } = 280;

    /// <summary>便签高度 (DIP)。</summary>
    public double Height { get; set; } = 350;

    /// <summary>背景颜色 (ARGB 十六进制字符串，例如 "#FFFFE0B2")。</summary>
    public string BackgroundColor { get; set; } = "#FFFFE0B2";

    /// <summary>不透明度 (0.0 ~ 1.0)。</summary>
    public double Opacity { get; set; } = 1.0;

    /// <summary>正文字体大小 (像素)。</summary>
    public double FontSize { get; set; } = 14;

    /// <summary>字体家族名称。</summary>
    public string FontFamily { get; set; } = "Microsoft YaHei UI";

    /// <summary>是否启用毛玻璃效果。</summary>
    public bool BlurEnabled { get; set; } = false;

    /// <summary>创建时间 (UTC)。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>最后修改时间 (UTC)。</summary>
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
}
