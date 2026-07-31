using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskOrganizer.Model;

/// <summary>
/// 配置持久化服务（单例）。
/// 负责 JSON 配置文件的加载、保存、备份和数据丢失恢复。
/// 配置文件路径：%APPDATA%\DeskOrganizer\config.json
/// 备份目录：%APPDATA%\DeskOrganizer\backup\
/// </summary>
public class ConfigService
{
    private static readonly Lazy<ConfigService> _lazy = new(() => new ConfigService());
    public static ConfigService Instance => _lazy.Value;

    private const string AppFolderName = "DeskOrganizer";
    private const string ConfigFileName = "config.json";
    private const string BackupFolderName = "backup";
    private const string FenceDataFolderName = "fences";
    private const string NotesFolderName = "notes";
    private const int MaxBackupFiles = 10;

    private string? _fenceDataDir;
    private string? _notesDir;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new FenceInfoConverter() }
    };

    private string? _configDir;
    private string? _backupDir;
    private AppConfig? _cachedConfig;
    private readonly object _lock = new();

    private ConfigService() { }

    /// <summary>获取当前已加载的配置对象。首次访问时自动加载。</summary>
    public AppConfig Config => _cachedConfig ??= Load();

    /// <summary>获取配置文件的完整路径。</summary>
    public string? ConfigFilePath
    {
        get
        {
            try { return Path.Combine(GetConfigDir(), ConfigFileName); }
            catch { return null; }
        }
    }

    /// <summary>获取备份目录的完整路径。</summary>
    public string? BackupDirectoryPath
    {
        get
        {
            try { return GetBackupDir(); }
            catch { return null; }
        }
    }

    /// <summary>获取当前备份数量。</summary>
    public int GetBackupCount()
    {
        try
        {
            var backupDir = GetBackupDir();
            if (!Directory.Exists(backupDir))
                return 0;
            return Directory.GetFiles(backupDir, "config_*.json").Length;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>保存当前缓存配置的便捷方法。</summary>
    public void Save()
    {
        Save(Config);
    }

    /// <summary>获取配置文件所在目录路径。</summary>
    public string GetConfigDir()
    {
        if (_configDir != null)
            return _configDir;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _configDir = Path.Combine(appData, AppFolderName);
        return _configDir;
    }

    /// <summary>获取备份文件所在目录路径。</summary>
    public string GetBackupDir()
    {
        if (_backupDir != null)
            return _backupDir;

        _backupDir = Path.Combine(GetConfigDir(), BackupFolderName);
        return _backupDir;
    }

    /// <summary>获取围栏数据目录路径（每个围栏一个 JSON 文件）。</summary>
    public string GetFenceDataDir()
    {
        if (_fenceDataDir != null)
            return _fenceDataDir;

        _fenceDataDir = Path.Combine(GetConfigDir(), FenceDataFolderName);
        if (!Directory.Exists(_fenceDataDir))
            Directory.CreateDirectory(_fenceDataDir);
        return _fenceDataDir;
    }

    /// <summary>保存围栏条目数据到独立文件。</summary>
    public void SaveFenceData(FenceInfo fence)
    {
        try
        {
            var dir = GetFenceDataDir();
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            var filePath = Path.Combine(dir, $"{fence.Id}.json");
            var data = new FenceData
            {
                Id = fence.Id,
                Name = fence.Name,
                FilePaths = fence.FilePaths ?? new(),
                DesktopIndex = fence.DesktopIndex,
                ModifiedAt = fence.ModifiedAt
            };
            var json = JsonSerializer.Serialize(data, _jsonOptions);
            WriteFileAtomic(filePath, json);
        }
        catch (Exception ex)
        {
            App.Log($"[ConfigService] SaveFenceData failed for '{fence.Name}': {ex.Message}");
        }
    }

    /// <summary>从独立文件加载围栏条目数据，合并到 FenceInfo。</summary>
    public void LoadFenceData(FenceInfo fence)
    {
        try
        {
            var filePath = Path.Combine(GetFenceDataDir(), $"{fence.Id}.json");
            if (!File.Exists(filePath))
                return;

            var json = File.ReadAllText(filePath);
            var data = JsonSerializer.Deserialize<FenceData>(json, _jsonOptions);
            if (data != null && data.FilePaths != null)
            {
                fence.FilePaths = data.FilePaths;
            }
        }
        catch (Exception ex)
        {
            App.Log($"[ConfigService] LoadFenceData failed for '{fence.Name}': {ex.Message}");
        }
    }

    /// <summary>删除围栏数据文件。</summary>
    public void DeleteFenceData(string fenceId)
    {
        try
        {
            var filePath = Path.Combine(GetFenceDataDir(), $"{fenceId}.json");
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
        catch (Exception ex)
        {
            App.Log($"[ConfigService] DeleteFenceData failed for '{fenceId}': {ex.Message}");
        }
    }

    /// <summary>获取便签数据目录路径。</summary>
    public string GetNotesDir()
    {
        if (_notesDir != null)
            return _notesDir;

        _notesDir = Path.Combine(GetConfigDir(), NotesFolderName);
        if (!Directory.Exists(_notesDir))
            Directory.CreateDirectory(_notesDir);
        return _notesDir;
    }

    /// <summary>保存便签内容为 .md 文件。</summary>
    public void SaveNoteContent(StickyNote note)
    {
        try
        {
            var dir = GetNotesDir();
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var filePath = Path.Combine(dir, $"{note.Id}.md");
            // 头部 YAML 元数据
            var mdContent = $"---\ntitle: {note.Title}\ncreated: {note.CreatedAt:yyyy-MM-dd HH:mm}\nmodified: {note.ModifiedAt:yyyy-MM-dd HH:mm}\nopacity: {note.Opacity}\nblur: {note.BlurEnabled}\nfont-size: {note.FontSize}\nfont-family: {note.FontFamily}\n---\n\n{note.Content}";
            WriteFileAtomic(filePath, mdContent, System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            App.Log($"[ConfigService] SaveNoteContent failed for '{note.Id}': {ex.Message}");
        }
    }

    /// <summary>从 .md 文件加载便签内容。</summary>
    public string? LoadNoteContent(string noteId)
    {
        try
        {
            var filePath = Path.Combine(GetNotesDir(), $"{noteId}.md");
            if (!File.Exists(filePath))
                return null;

            var lines = File.ReadAllLines(filePath, System.Text.Encoding.UTF8);
            // 跳过 YAML 头部（--- 到 --- 之间的行）
            int i = 0;
            if (lines.Length > 0 && lines[0] == "---")
            {
                i = 1;
                while (i < lines.Length && lines[i] != "---")
                    i++;
                i++; // 跳过第二个 ---
            }
            return string.Join("\n", lines.Skip(i));
        }
        catch (Exception ex)
        {
            App.Log($"[ConfigService] LoadNoteContent failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>删除便签 .md 文件。</summary>
    public void DeleteNoteFile(string noteId)
    {
        try
        {
            var filePath = Path.Combine(GetNotesDir(), $"{noteId}.md");
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
        catch (Exception ex)
        {
            App.Log($"[ConfigService] DeleteNoteFile failed for '{noteId}': {ex.Message}");
        }
    }

    /// <summary>
    /// 原子写入文件：优先临时文件 + 原子替换；若临时文件被拒（如受限环境），回退到加锁直写。
    /// 通过 _lock 串行化，防止多便栏/多围栏并发保存时的 Access denied。
    /// </summary>
    private void WriteFileAtomic(string filePath, string content, System.Text.Encoding? encoding = null)
    {
        encoding ??= new System.Text.UTF8Encoding(false);
        lock (_lock)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var tempPath = filePath + ".tmp";
            try
            {
                File.WriteAllText(tempPath, content, encoding);
                // .NET 5+ File.Move 支持 overwrite，原子替换原文件（NTFS 上为原子操作）
                File.Move(tempPath, filePath, overwrite: true);
            }
            catch (UnauthorizedAccessException)
            {
                // 受限环境（沙箱/安全软件）可能拒绝 .tmp 写入，回退到加锁直写
                CleanupTemp(tempPath);
                File.WriteAllText(filePath, content, encoding);
            }
        }
    }

    private static void CleanupTemp(string tempPath)
    {
        try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
    }

    /// <summary>确保配置目录、备份目录和围栏数据目录存在。</summary>
    public void EnsureDirectories()
    {
        var configDir = GetConfigDir();
        if (!Directory.Exists(configDir))
            Directory.CreateDirectory(configDir);

        var backupDir = GetBackupDir();
        if (!Directory.Exists(backupDir))
            Directory.CreateDirectory(backupDir);

        var fenceDir = GetFenceDataDir();
        if (!Directory.Exists(fenceDir))
            Directory.CreateDirectory(fenceDir);

        var notesDir = GetNotesDir();
        if (!Directory.Exists(notesDir))
            Directory.CreateDirectory(notesDir);
    }

    /// <summary>获取配置文件的完整路径。</summary>
    public string GetConfigFilePath()
    {
        return Path.Combine(GetConfigDir(), ConfigFileName);
    }

    /// <summary>
    /// 加载配置文件。若文件不存在，返回默认配置实例。
    /// 加载后会执行数据丢失检测和自动恢复。
    /// </summary>
    public AppConfig Load()
    {
        lock (_lock)
        {
            try
            {
                EnsureDirectories();

                var configPath = GetConfigFilePath();

                if (!File.Exists(configPath))
                {
                    var defaultConfig = new AppConfig();
                    _cachedConfig = defaultConfig;
                    return defaultConfig;
                }

                var json = File.ReadAllText(configPath);
                var config = JsonSerializer.Deserialize<AppConfig>(json, _jsonOptions);

                if (config == null)
                {
                    config = new AppConfig();
                }
                else
                {
                    try
                    {
                        config = ValidateConfig(config);
                    }
                    catch (Exception)
                    {
                        // ValidateConfig failed, use default
                        config = new AppConfig();
                    }
                }

                TryAutoRestoreIfDataLoss(config);

                _cachedConfig = config;
                return config;
            }
            catch (Exception)
            {
                // 反序列化失败时尝试从备份恢复
                _cachedConfig = TryRestoreFromBackup() ?? new AppConfig();
                return _cachedConfig;
            }
        }
    }

    /// <summary>保存配置到 JSON 文件，并自动创建备份。</summary>
    public void Save(AppConfig config)
    {
        if (config == null)
            return;

        lock (_lock)
        {
            try
            {
                EnsureDirectories();

                config.LastSavedAt = DateTime.UtcNow;

                var json = JsonSerializer.Serialize(config, _jsonOptions);
                var configPath = GetConfigFilePath();

                WriteFileAtomic(configPath, json);

                CreateBackup();

                _cachedConfig = config;
            }
            catch (Exception ex)
            {
                // 保存失败记录日志，便于排查配置丢失问题（不再静默吞掉）
                App.Log($"[ConfigService] Save failed: {ex.Message}");
            }
        }
    }

    /// <summary>创建当前配置文件的时间戳备份。</summary>
    public void CreateBackup()
    {
        FileStream? fs = null;
        try
        {
            EnsureDirectories();

            var configPath = GetConfigFilePath();
            if (!File.Exists(configPath))
                return;

            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var backupFileName = $"config_{timestamp}.json";
            var backupPath = Path.Combine(GetBackupDir(), backupFileName);

            using (fs = new FileStream(backupPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using (var sourceStream = File.OpenRead(configPath))
                {
                    sourceStream.CopyTo(fs);
                }
            }
            fs = null;

            // 清理旧备份，仅保留最近 MaxBackupFiles 个
            CleanupOldBackups();
        }
        catch (Exception)
        {
            // 备份失败不影响主流程
        }
        finally
        {
            fs?.Dispose();
        }
    }

    /// <summary>
    /// 检测配置是否可能存在数据丢失（围栏数据异常减少），尝试自动恢复。
    /// </summary>
    public void TryAutoRestoreIfDataLoss(AppConfig currentConfig)
    {
        if (currentConfig == null)
            return;

        // 如果围栏数量为 0 但存在备份文件，可能是数据丢失
        if (currentConfig.Boxes.Count == 0)
        {
            var latestBackup = GetLatestBackupFile();
            if (latestBackup != null)
            {
                try
                {
                    var backupJson = File.ReadAllText(latestBackup);
                    var backupConfig = JsonSerializer.Deserialize<AppConfig>(backupJson, _jsonOptions);

                    if (backupConfig?.Boxes.Count > 0)
                    {
                        // 从备份恢复围栏数据
                        currentConfig.Boxes = backupConfig.Boxes;
                    }
                }
                catch (Exception)
                {
                    // 恢复失败，保持当前配置
                }
            }
        }
    }

    /// <summary>从最新的备份文件尝试恢复配置。失败返回 null。</summary>
    public AppConfig? TryRestoreFromBackup()
    {
        try
        {
            var latestBackup = GetLatestBackupFile();
            if (latestBackup == null)
                return null;

            var json = File.ReadAllText(latestBackup);
            var config = JsonSerializer.Deserialize<AppConfig>(json, _jsonOptions);

            if (config != null)
            {
                config = ValidateConfig(config);
                Save(config);
            }

            return config;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>验证并修正配置数据，确保所有字段合法。</summary>
    public AppConfig ValidateConfig(AppConfig config)
    {
        if (config == null)
            return new AppConfig();

        if (string.IsNullOrWhiteSpace(config.Version))
            config.Version = "2.0";

        // 验证围栏数据
        if (config.Boxes == null)
            config.Boxes = new List<FenceInfo>();

        for (int i = config.Boxes.Count - 1; i >= 0; i--)
        {
            var box = config.Boxes[i];

            // 移除无效围栏
            if (string.IsNullOrWhiteSpace(box.Id) || string.IsNullOrWhiteSpace(box.Name))
            {
                config.Boxes.RemoveAt(i);
                continue;
            }

            config.Boxes[i] = SanitizeBox(box);
        }

        // 验证便签数据
        if (config.StickyNotes == null)
            config.StickyNotes = new List<StickyNote>();

        for (int i = config.StickyNotes.Count - 1; i >= 0; i--)
        {
            var note = config.StickyNotes[i];

            if (string.IsNullOrWhiteSpace(note.Id))
            {
                config.StickyNotes.RemoveAt(i);
                continue;
            }

            // 修正便签数值范围
            note.Opacity = Math.Clamp(note.Opacity, 0.05, 1.0);
            note.FontSize = Math.Clamp(note.FontSize, 8, 72);
            note.Width = Math.Max(note.Width, 150);
            note.Height = Math.Max(note.Height, 150);
        }

        return config;
    }

    /// <summary>修正围栏数据中的异常值。</summary>
    public FenceInfo SanitizeBox(FenceInfo fence)
    {
        if (fence == null)
            return new FenceInfo();

        // 修正尺寸
        fence.Width = Math.Max(fence.Width, 100);
        fence.Height = Math.Max(fence.Height, 100);

        // 修正位置（防止超出屏幕边界）
        // 取负值修正为 0
        fence.X = Math.Max(fence.X, -1000);
        fence.Y = Math.Max(fence.Y, -1000);

        // 同步像素级位置
        fence.PosX = (int)Math.Max(fence.X, 0);
        fence.PosY = (int)Math.Max(fence.Y, 0);

        // 修正不透明度
        fence.Opacity = Math.Clamp(fence.Opacity, 0.05, 1.0);

        // 修正圆角
        fence.CornerRadius = Math.Clamp(fence.CornerRadius, 0, 50);

        // 修正图标大小
        fence.IconSize = fence.IconSize switch
        {
            16 => 16,
            24 => 24,
            32 => 32,
            48 => 48,
            64 => 64,
            _ => 48
        };

        // 修正标题栏高度
        fence.TitleHeight = Math.Clamp(fence.TitleHeight, 20, 60);

        // 确保文件路径列表不为 null
        fence.FilePaths ??= new List<string>();

        // 过滤无效路径
        fence.FilePaths = fence.FilePaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        // 修正背景颜色
        if (string.IsNullOrWhiteSpace(fence.BackgroundColor))
            fence.BackgroundColor = "#80FFFFFF";

        return fence;
    }

    /// <summary>获取最新的备份文件路径，不存在返回 null。</summary>
    private string? GetLatestBackupFile()
    {
        try
        {
            var backupDir = GetBackupDir();
            if (!Directory.Exists(backupDir))
                return null;

            return Directory.GetFiles(backupDir, "config_*.json")
                .OrderByDescending(f => f)
                .FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>清理旧备份文件，仅保留最近 MaxBackupFiles 个。</summary>
    private void CleanupOldBackups()
    {
        try
        {
            var backupDir = GetBackupDir();
            if (!Directory.Exists(backupDir))
                return;

            var files = Directory.GetFiles(backupDir, "config_*.json")
                .OrderByDescending(f => f)
                .ToList();

            if (files.Count <= MaxBackupFiles)
                return;

            foreach (var file in files.Skip(MaxBackupFiles))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception)
                {
                    // 单个文件删除失败不影响其余
                }
            }
        }
        catch (Exception)
        {
            // 清理失败不影响主流程
        }
    }
}
