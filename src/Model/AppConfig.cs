using System;
using System.Collections.Generic;

namespace DeskOrganizer.Model;

/// <summary>
/// 应用程序全局配置数据模型，对应 JSON 持久化结构。
/// </summary>
public class AppConfig
{
    /// <summary>配置版本号。</summary>
    public string Version { get; set; } = typeof(App).Assembly.GetName().Version?.ToString() ?? "2.0";

    /// <summary>是否开机自启动。</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>关闭时是否最小化到系统托盘而非退出。</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>是否启用窗口模糊效果。</summary>
    public bool EnableBlur { get; set; }

    /// <summary>是否显示窗口阴影。</summary>
    public bool ShowShadow { get; set; } = true;

    /// <summary>是否自动保存配置。</summary>
    public bool AutoSave { get; set; } = true;

    /// <summary>便签是否启用 Markdown 渲染。</summary>
    public bool MarkdownRender { get; set; }

    /// <summary>围栏图标大小 (像素)。</summary>
    public int IconSize { get; set; } = 48;

    /// <summary>围栏标题栏高度 (像素)。</summary>
    public int TitleHeight { get; set; } = 32;

    /// <summary>便签默认字体大小 (像素)。</summary>
    public int FontSize { get; set; } = 14;

    /// <summary>围栏列表。</summary>
    public List<FenceInfo> Boxes { get; set; } = new();

    /// <summary>便签列表。</summary>
    public List<StickyNote> StickyNotes { get; set; } = new();

    /// <summary>配置最后保存时间 (UTC)。</summary>
    public DateTime LastSavedAt { get; set; }

    /// <summary>存储上限（MB），超过时提示清理。0 表示不限制。</summary>
    public int StorageLimitMB { get; set; } = 10;

    /// <summary>搜索索引文件上限。</summary>
    public int SearchIndexLimit { get; set; } = 200000;

    /// <summary>是否自动清理旧备份。</summary>
    public bool AutoCleanBackups { get; set; } = true;

    /// <summary>备份保留数量上限。</summary>
    public int MaxBackupCount { get; set; } = 20;

    /// <summary>搜索热键的修饰键 (Ctrl=2, Alt=1, Shift=4, Win=8，可组合)。</summary>
    public int SearchHotkeyModifiers { get; set; } = 1; // 默认 Alt

    /// <summary>搜索热键的虚拟键码 (VK_SPACE=0x20)。</summary>
    public int SearchHotkeyKey { get; set; } = 0x20; // 默认 Space

    /// <summary>是否启动时自动检查更新。</summary>
    public bool AutoCheckUpdate { get; set; } = true;

    /// <summary>是否静默下载更新包（后台下载，重启时自动应用）。</summary>
    public bool SilentDownloadUpdate { get; set; } = true;

    /// <summary>上次检查更新的时间。</summary>
    public DateTime LastUpdateCheck { get; set; }

    /// <summary>待应用更新的版本号（静默下载完成后设置，重启时读取）。</summary>
    public string PendingUpdateVersion { get; set; } = "";

    /// <summary>待应用更新的暂存文件路径（静默下载完成后设置，重启时读取）。</summary>
    public string PendingUpdatePath { get; set; } = "";

    /// <summary>待应用更新的下载 URL（用于校验暂存文件是否仍然有效）。</summary>
    public string PendingUpdateUrl { get; set; } = "";
}
