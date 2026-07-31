namespace DeskOrganizer.NoFences.Model;

/// <summary>
/// 栅栏条目类型：文件、文件夹或快捷方式。
/// </summary>
public enum EntryType
{
    /// <summary>普通文件。</summary>
    File,

    /// <summary>文件夹。</summary>
    Folder,

    /// <summary>快捷方式 (.lnk)。</summary>
    Shortcut
}
