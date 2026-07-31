using System;
using System.Collections.Generic;

namespace DeskOrganizer.Model;

/// <summary>
/// 桌面围栏数据模型，记录围栏的布局、样式和收纳的文件列表。
/// JSON 序列化时使用 <see cref="FenceInfoConverter"/> 处理 FilePaths/Files 属性映射。
/// </summary>
public class FenceInfo
{
    /// <summary>围栏唯一标识符。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>围栏显示名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>围栏左上角 X 坐标 (DIP)。</summary>
    public double X { get; set; }

    /// <summary>围栏左上角 Y 坐标 (DIP)。</summary>
    public double Y { get; set; }

    /// <summary>围栏宽度 (DIP)。</summary>
    public double Width { get; set; } = 300;

    /// <summary>围栏高度 (DIP)。</summary>
    public double Height { get; set; } = 400;

    /// <summary>围栏左上角 X 坐标 (像素级，用于 WinForms 布局)。</summary>
    public int PosX { get; set; }

    /// <summary>围栏左上角 Y 坐标 (像素级，用于 WinForms 布局)。</summary>
    public int PosY { get; set; }

    /// <summary>是否锁定（禁止拖动和调整大小）。</summary>
    public bool Locked { get; set; }

    /// <summary>
    /// <see cref="Locked"/> 的别名属性，用于兼容不同版本的序列化字段。
    /// 读写均映射到 <see cref="Locked"/>。
    /// </summary>
    public bool IsLocked
    {
        get => Locked;
        set => Locked = value;
    }

    /// <summary>是否允许最小化。</summary>
    public bool CanMinify { get; set; } = true;

    /// <summary>标题栏高度 (像素)。</summary>
    public int TitleHeight { get; set; } = 30;

    /// <summary>
    /// 围栏内收纳的文件路径列表。
    /// 旧版 JSON 中键名为 "files"，新版使用 "filePaths"；
    /// 由 <see cref="FenceInfoConverter"/> 统一处理。
    /// </summary>
    public List<string> FilePaths { get; set; } = new();

    /// <summary>条目自定义显示名称（键为文件路径，值为自定义名称）。</summary>
    public Dictionary<string, string> EntryCustomNames { get; set; } = new();

    /// <summary>
    /// 旧版 JSON "files" 键的别名，读写均映射到 <see cref="FilePaths"/>。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    [Obsolete("此属性仅为向后兼容而保留，请使用 FilePaths 代替。")]
    public List<string> Files
    {
        get => FilePaths;
        set => FilePaths = value ?? new();
    }

    /// <summary>背景颜色 (ARGB 十六进制字符串，例如 "#80FFFFFF")。</summary>
    public string BackgroundColor { get; set; } = "#80FFFFFF";

    /// <summary>不透明度 (0.0 ~ 1.0)。</summary>
    public double Opacity { get; set; } = 0.5;

    /// <summary>是否启用毛玻璃效果。</summary>
    public bool BlurEnabled { get; set; } = true;

    /// <summary>圆角半径 (像素)。</summary>
    public int CornerRadius { get; set; } = 8;

    /// <summary>图标显示大小 (像素)。</summary>
    public int IconSize { get; set; } = 48;

    /// <summary>围栏所属虚拟桌面索引（1-based），默认为 1。</summary>
    public int DesktopIndex { get; set; } = 1;

    /// <summary>创建时间 (UTC)。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>最后修改时间 (UTC)。</summary>
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
}
