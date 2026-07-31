using System;
using System.Collections.Generic;

namespace DeskOrganizer.Model;

/// <summary>
/// 围栏条目持久化数据（每个围栏一个 JSON 文件）。
/// </summary>
public class FenceData
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<string> FilePaths { get; set; } = new();
    public int DesktopIndex { get; set; } = 1;
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
}
