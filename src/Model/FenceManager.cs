using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DeskOrganizer.NoFences;
using App = DeskOrganizer.App;

namespace DeskOrganizer.Model;

/// <summary>
/// 围栏生命周期管理器（单例）。
/// 负责围栏窗口的创建、销毁、显示/隐藏等操作，
/// 使用 <see cref="ConcurrentDictionary{TKey,TValue}"/> 追踪活跃围栏窗口。
/// </summary>
public class FenceManager
{
    private static readonly Lazy<FenceManager> _lazy = new(() => new FenceManager());
    public static FenceManager Instance => _lazy.Value;

    /// <summary>
    /// 围栏窗口追踪表：Key = 围栏 Id，Value = 围栏窗口实例。
    /// 使用 ConcurrentDictionary 保证线程安全。
    /// </summary>
    private readonly ConcurrentDictionary<string, NoFences.FenceWindow> _fenceWindows = new();

    private static readonly System.Text.Json.JsonSerializerOptions _jsonOpts = new() { WriteIndented = true };

    private FenceManager() { }

    /// <summary>从应用配置中加载所有围栏窗口。</summary>
    public void LoadFences(AppConfig config)
    {
        if (config?.Boxes == null)
            return;

        // 修正超屏围栏坐标
        int screenW = (int)System.Windows.SystemParameters.PrimaryScreenWidth;
        int screenH = (int)System.Windows.SystemParameters.PrimaryScreenHeight;
        bool needSave = false;
        foreach (var fence in config.Boxes)
        {
            if (fence.X >= screenW || fence.Y >= screenH || fence.X < 0 || fence.Y < 0)
            {
                int idx = config.Boxes.IndexOf(fence);
                int maxCols = Math.Max(1, (screenW - 100) / 350);
                int col = idx % maxCols;
                int row = idx / maxCols;
                fence.X = 100 + col * 350;
                fence.Y = 50 + (row * 60) % Math.Max(100, screenH - 500);
                fence.PosX = (int)fence.X;
                fence.PosY = (int)fence.Y;
                needSave = true;
                App.Log($"[FenceManager] Fixed out-of-bounds fence '{fence.Name}' to ({fence.X}, {fence.Y})");
            }
        }
        if (needSave) ConfigService.Instance.Save();

        // 修正旧版全白背景的围栏（#40FFFFFF → #40202A3A）
        bool bgUpdated = false;
        foreach (var fence in config.Boxes)
        {
            if (fence.BackgroundColor == "#40FFFFFF" || fence.BackgroundColor == "#40ffffff")
            {
                fence.BackgroundColor = "#40202A3A";
                fence.Opacity = 0.75;
                bgUpdated = true;
            }
        }
        if (bgUpdated) ConfigService.Instance.Save();

        // 从独立文件加载围栏条目数据
        foreach (var fence in config.Boxes)
        {
            ConfigService.Instance.LoadFenceData(fence);
        }

        // 迁移不存在的文件路径：如果路径指向旧存储（HiddenStorage等），
        // 但 FenceStorage 中有同名文件，则自动更新为 FenceStorage 路径
        var fenceStorageDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DeskOrganizer", "FenceStorage");
        bool migrated = false;
        int migrateCount = 0;
        if (System.IO.Directory.Exists(fenceStorageDir))
        {
            var storageFiles = new HashSet<string>(
                System.IO.Directory.GetFiles(fenceStorageDir)
                    .Select(p => System.IO.Path.GetFileName(p)),
                StringComparer.OrdinalIgnoreCase);

            foreach (var fence in config.Boxes)
            {
                if (fence.FilePaths == null) continue;
                for (int i = 0; i < fence.FilePaths.Count; i++)
                {
                    var p = fence.FilePaths[i];
                    // 如果文件存在且不在旧存储目录中，跳过
                    if (System.IO.File.Exists(p) && !p.Contains("HiddenStorage", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var fileName = System.IO.Path.GetFileName(p);
                    if (string.IsNullOrEmpty(fileName)) continue;
                    if (storageFiles.Contains(fileName))
                    {
                        var newPath = System.IO.Path.Combine(fenceStorageDir, fileName);
                        fence.FilePaths[i] = newPath;
                        migrated = true;
                        migrateCount++;
                    }
                    else if (p.Contains("HiddenStorage", StringComparison.OrdinalIgnoreCase))
                    {
                        // 文件在 HiddenStorage 中但不在 FenceStorage 中，移除无效路径
                        fence.FilePaths.RemoveAt(i);
                        i--;
                        migrateCount++;
                    }
                }
            }
        }
        if (migrated)
        {
            DirectSaveConfig(config);
            App.Log($"[FenceManager] Path migration: {migrateCount} paths updated, saved directly");
            foreach (var fence in config.Boxes)
            {
                ConfigService.Instance.SaveFenceData(fence);
            }
        }

        // 修复：FenceStorage 中有文件但围栏中引用很少，说明之前的一键整理
        // 因为去重逻辑问题只整理了少量文件。将未引用的文件补充到已存在的匹配围栏中。
        // 注意：不自动创建新围栏，避免每次重启都出现"未分类"围栏。
        if (System.IO.Directory.Exists(fenceStorageDir))
        {
            var allStorageFiles = System.IO.Directory.GetFiles(fenceStorageDir);
            var referencedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in config.Boxes)
            {
                if (f.FilePaths == null) continue;
                foreach (var p in f.FilePaths)
                {
                    referencedFiles.Add(System.IO.Path.GetFileName(p));
                }
            }
            var unreferenced = allStorageFiles
                .Where(f => !referencedFiles.Contains(System.IO.Path.GetFileName(f)))
                .ToList();

            if (unreferenced.Count > 0)
            {
                App.Log($"[FenceManager] Found {unreferenced.Count} unreferenced files in FenceStorage, assigning to existing fences...");
                // 按分类规则分组
                var categorized = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var fp in unreferenced)
                {
                    var category = CategorizeShortcut(fp);
                    if (!categorized.ContainsKey(category))
                        categorized[category] = new();
                    categorized[category].Add(fp);
                }

                var changedFenceIds = new HashSet<string>();
                foreach (var kv in categorized)
                {
                    if (kv.Value.Count == 0) continue;
                    // 只补充到已存在的围栏，不自动创建新围栏
                    var fence = config.Boxes.FirstOrDefault(f =>
                        f.Name.Equals(kv.Key, StringComparison.OrdinalIgnoreCase));
                    if (fence == null)
                    {
                        // 找同名带 (数字) 后缀的
                        fence = config.Boxes.FirstOrDefault(f =>
                            f.Name.StartsWith(kv.Key + " (", StringComparison.OrdinalIgnoreCase));
                    }
                    if (fence == null)
                    {
                        // 没有匹配的围栏，跳过（不创建"未分类"围栏）
                        App.Log($"[FenceManager] No existing fence for category '{kv.Key}', skipping {kv.Value.Count} files");
                        continue;
                    }
                    if (fence.FilePaths == null) fence.FilePaths = new List<string>();
                    fence.FilePaths.AddRange(kv.Value);
                    fence.ModifiedAt = DateTime.UtcNow;
                    changedFenceIds.Add(fence.Id);
                }

                if (changedFenceIds.Count > 0)
                {
                    DirectSaveConfig(config);
                    foreach (var fence in config.Boxes)
                        ConfigService.Instance.SaveFenceData(fence);
                    App.Log($"[FenceManager] Assigned {unreferenced.Count} files into existing fences");
                }
            }
        }

        // 清理空的围栏（0 个有效文件路径）
        var emptyFences = config.Boxes
            .Where(f => f.FilePaths == null || f.FilePaths.Count == 0)
            .ToList();
        if (emptyFences.Count > 0)
        {
            foreach (var ef in emptyFences)
            {
                config.Boxes.Remove(ef);
                ConfigService.Instance.DeleteFenceData(ef.Id);
                App.Log($"[FenceManager] Removed empty fence: {ef.Name}");
            }
            DirectSaveConfig(config);
        }

        // 修复：将"未分类"围栏中的文件重新归类到正确的围栏，然后移除"未分类"围栏
        // 这解决之前版本每次重启自动创建"未分类"围栏遗留的问题
        var uncatFence = config.Boxes.FirstOrDefault(f => f.Name == "未分类");
        if (uncatFence != null && uncatFence.FilePaths != null && uncatFence.FilePaths.Count > 0)
        {
            App.Log($"[FenceManager] Found '未分类' fence with {uncatFence.FilePaths.Count} files, re-categorizing...");
            var unclassifiedFiles = new List<string>();
            foreach (var fp in uncatFence.FilePaths.ToList())
            {
                var category = CategorizeShortcut(fp);
                // 找已存在的匹配围栏（排除"未分类"自身）
                var targetFence = config.Boxes.FirstOrDefault(f =>
                    f.Name.Equals(category, StringComparison.OrdinalIgnoreCase) && f.Id != uncatFence.Id);
                if (targetFence == null)
                {
                    targetFence = config.Boxes.FirstOrDefault(f =>
                        f.Name.StartsWith(category + " (", StringComparison.OrdinalIgnoreCase) && f.Id != uncatFence.Id);
                }
                if (targetFence != null && category != "未分类")
                {
                    if (targetFence.FilePaths == null) targetFence.FilePaths = new List<string>();
                    if (!targetFence.FilePaths.Contains(fp, StringComparer.OrdinalIgnoreCase))
                    {
                        targetFence.FilePaths.Add(fp);
                        targetFence.ModifiedAt = DateTime.UtcNow;
                        App.Log($"[FenceManager] Moved '{System.IO.Path.GetFileName(fp)}' to '{targetFence.Name}'");
                    }
                }
                else
                {
                    // 无法归类到其他围栏，保留文件路径记录
                    unclassifiedFiles.Add(fp);
                    App.Log($"[FenceManager] Could not re-categorize '{System.IO.Path.GetFileName(fp)}', will keep in storage");
                }
            }

            // 无论是否成功归类，都移除"未分类"围栏
            // 未归类的文件保留在 FenceStorage 目录中，用户可通过"一键整理"重新分类
            config.Boxes.Remove(uncatFence);
            ConfigService.Instance.DeleteFenceData(uncatFence.Id);
            DirectSaveConfig(config);
            foreach (var fence in config.Boxes)
                ConfigService.Instance.SaveFenceData(fence);
            App.Log($"[FenceManager] Removed '未分类' fence ({unclassifiedFiles.Count} files could not be re-categorized)");
        }

        // 转为数组快照，避免遍历期间集合被修改
        var fences = config.Boxes.ToArray();
        foreach (var fence in fences)
        {
            try
            {
                CreateFenceWindow(fence);
            }
            catch (Exception ex)
            {
                App.Log($"[FenceManager] LoadFence failed for '{fence.Name}': {ex.Message}");
            }
        }
    }

    /// <summary>创建一个新围栏（数据层 + 窗口层），并返回围栏信息。</summary>
    public FenceInfo CreateFence(string name)
    {
        // 检查是否存在同名围栏，若存在则追加数字后缀
        var config = ConfigService.Instance.Config;
        var existingNames = new HashSet<string>(config.Boxes.Select(b => b.Name), StringComparer.OrdinalIgnoreCase);

        if (existingNames.Contains(name))
        {
            int suffix = 2;
            while (existingNames.Contains($"{name} ({suffix})"))
                suffix++;
            name = $"{name} ({suffix})";
        }

        var fence = new FenceInfo
        {
            Name = name,
            X = 100 + (_fenceWindows.Count % 5) * 320,
            Y = 50 + (_fenceWindows.Count % 5) * 50,
            Width = 300,
            Height = 400,
            BackgroundColor = "#202A3A",
            Opacity = 0.75,
            CornerRadius = 10,
            IconSize = 48,
            TitleHeight = 35,
            DesktopIndex = App.CurrentDesktopIndex,
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow
        };

        // 先添加到配置，用 config.Boxes.Count 计算位置
        config.Boxes.Add(fence);
        ConfigService.Instance.Save();

        // 根据已有围栏数量计算错开位置，确保在屏幕内
        int screenW = (int)System.Windows.SystemParameters.PrimaryScreenWidth;
        int screenH = (int)System.Windows.SystemParameters.PrimaryScreenHeight;
        int idx = config.Boxes.Count - 1; // 当前围栏的索引
        int maxCols = Math.Max(1, (screenW - 100) / 350);
        int col = idx % maxCols;
        int row = idx / maxCols;
        fence.X = 100 + col * 350;
        fence.Y = 50 + (row * 60) % Math.Max(100, screenH - 500);
        fence.PosX = (int)fence.X;
        fence.PosY = (int)fence.Y;
        ConfigService.Instance.Save();

        // 用事件等待窗口就绪，替代固定 Sleep(1000) 盲等
        var ready = new System.Threading.ManualResetEventSlim(false);
        CreateFenceWindow(fence, ready);

        // 后台等待窗口就绪后显示对应桌面（最多等 2 秒，超时则直接显示）
        var desktopIdx = App.CurrentDesktopIndex;
        new System.Threading.Thread(() =>
        {
            ready.Wait(2000);
            ShowFencesForDesktop(desktopIdx);
        })
        { IsBackground = true, Name = "FenceShowDesktop" }.Start();

        return fence;
    }

    /// <summary>移除指定围栏（关闭窗口 + 从配置中删除）。</summary>
    public void RemoveFence(FenceInfo fence)
    {
        if (fence == null)
            return;

        // 关闭窗口并从追踪表移除
        if (_fenceWindows.TryRemove(fence.Id, out var window))
        {
            try
            {
                if (window.InvokeRequired)
                    window.BeginInvoke(new Action(() => { window.Close(); }));
                else
                    window.Close();
            }
            catch (Exception ex)
            {
                App.Log($"[FenceManager] Close fence window failed: {ex.Message}");
            }
        }

        // 从配置中删除（使用缓存的 config，不重新 Load）
        var config = ConfigService.Instance.Config;
        config.Boxes.RemoveAll(b => b.Id == fence.Id);
        ConfigService.Instance.Save();
        // 删除围栏条目数据文件
        ConfigService.Instance.DeleteFenceData(fence.Id);
    }

    /// <summary>更新围栏数据并同步持久化。</summary>
    public void UpdateFence(FenceInfo fence)
    {
        if (fence == null)
            return;

        fence.ModifiedAt = DateTime.UtcNow;

        var config = ConfigService.Instance.Config;
        var index = config.Boxes.FindIndex(b => b.Id == fence.Id);
        if (index >= 0)
        {
            config.Boxes[index] = fence;
            ConfigService.Instance.Save();
        }
    }

    /// <summary>显示所有围栏窗口。</summary>
    public void ShowAllFences()
    {
        foreach (var kvp in _fenceWindows)
        {
            var window = kvp.Value;
            if (window.InvokeRequired)
                window.BeginInvoke(() => ShowFenceWindow(window));
            else
                ShowFenceWindow(window);
        }
    }

    /// <summary>恢复单个围栏窗口的显示和桌面底层状态。</summary>
    private static void ShowFenceWindow(NoFences.FenceWindow window)
    {
        window.Show();
        NoFences.Win32.WindowUtil.SendToBack(window.Handle);
    }

    /// <summary>隐藏所有围栏窗口。</summary>
    public void HideAllFences()
    {
        foreach (var kvp in _fenceWindows)
        {
            var window = kvp.Value;
            if (window.InvokeRequired)
                window.BeginInvoke(() => window.Hide());
            else
                window.Hide();
        }
    }

    /// <summary>切换所有围栏窗口的可见状态。</summary>
    public void ToggleAllFences()
    {
        foreach (var kvp in _fenceWindows)
        {
            var window = kvp.Value;
            if (window.InvokeRequired)
                window.BeginInvoke(() => window.Visible = !window.Visible);
            else
                window.Visible = !window.Visible;
        }
    }

    /// <summary>自动排布所有围栏（固定尺寸网格排列，不拉伸到全屏）。</summary>
    public void AutoArrangeFences()
    {
        var config = ConfigService.Instance.Config;
        if (config.Boxes == null || config.Boxes.Count == 0) return;

        var screen = System.Windows.SystemParameters.WorkArea;
        int margin = 12;
        int defaultW = 280;
        int defaultH = 360;
        int maxCols = Math.Max(1, (int)((screen.Width - margin) / (defaultW + margin)));

        int idx = 0;
        foreach (var fence in config.Boxes)
        {
            int col = idx % maxCols;
            int row = idx / maxCols;

            int x = (int)screen.Left + margin + col * (defaultW + margin);
            int y = (int)screen.Top + margin + row * (defaultH + margin);

            // 超出屏幕换行到下一行起始
            if (x + defaultW > screen.Right)
            {
                x = (int)screen.Left + margin;
                row++;
                y = (int)screen.Top + margin + row * (defaultH + margin);
            }

            fence.X = x;
            fence.Y = y;
            fence.PosX = x;
            fence.PosY = y;
            fence.Width = defaultW;
            fence.Height = defaultH;

            if (_fenceWindows.TryGetValue(fence.Id, out var window))
            {
                var snapX = x;
                var snapY = y;
                var w = window;
                if (w.InvokeRequired)
                    w.BeginInvoke(() =>
                    {
                        w.SuppressFenceChanged = true;
                        w.Location = new System.Drawing.Point(snapX, snapY);
                        w.Size = new System.Drawing.Size(defaultW, defaultH);
                        NoFences.Win32.WindowUtil.SendToBack(w.Handle);
                        var t = new System.Windows.Threading.DispatcherTimer();
                        t.Interval = System.TimeSpan.FromMilliseconds(200);
                        t.Tick += (_, _) => { w.SuppressFenceChanged = false; t.Stop(); };
                        t.Start();
                    });
                else
                {
                    w.SuppressFenceChanged = true;
                    w.Location = new System.Drawing.Point(snapX, snapY);
                    w.Size = new System.Drawing.Size(defaultW, defaultH);
                    NoFences.Win32.WindowUtil.SendToBack(w.Handle);
                    var t = new System.Windows.Threading.DispatcherTimer();
                    t.Interval = System.TimeSpan.FromMilliseconds(200);
                    t.Tick += (_, _) => { w.SuppressFenceChanged = false; t.Stop(); };
                    t.Start();
                }
            }

            idx++;
        }

        try { ConfigService.Instance.Save(); } catch (Exception ex) { App.Log($"[FenceManager] ConfigService.Save failed: {ex.Message}"); }
    }

    /// <summary>
    /// 一键整理桌面快捷方式：扫描桌面上的 .lnk/.url 文件，
    /// 按类型分类后自动添加到匹配的围栏中，未匹配的放入"未分类"围栏。
    /// </summary>
    public (int total, int organized, int unmatched) OrganizeDesktopShortcuts()
    {
        var config = ConfigService.Instance.Config;
        if (config.Boxes == null) config.Boxes = new List<FenceInfo>();

        // 1. 扫描桌面上所有快捷方式（.lnk 和 .url）
        var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var commonDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        var shortcuts = new List<string>();
        try
        {
            shortcuts.AddRange(System.IO.Directory.GetFiles(desktopPath, "*.lnk"));
            shortcuts.AddRange(System.IO.Directory.GetFiles(desktopPath, "*.url"));
            if (commonDesktop != desktopPath)
            {
                shortcuts.AddRange(System.IO.Directory.GetFiles(commonDesktop, "*.lnk"));
                shortcuts.AddRange(System.IO.Directory.GetFiles(commonDesktop, "*.url"));
            }
        }
        catch (Exception ex) { App.Log($"OrganizeDesktopShortcuts scan error: {ex.Message}"); }

        if (shortcuts.Count == 0) return (0, 0, 0);

        // 2. 收集已在围栏中的文件名（按文件名去重，避免路径不同但实际同一文件被重复整理）
        var existingFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in config.Boxes)
        {
            if (f.FilePaths != null)
                foreach (var p in f.FilePaths) existingFileNames.Add(System.IO.Path.GetFileName(p));
        }

        var newShortcuts = shortcuts.Where(s => !existingFileNames.Contains(System.IO.Path.GetFileName(s))).ToList();
        if (newShortcuts.Count == 0) return (shortcuts.Count, 0, 0);

        // 3. 创建围栏存储目录
        var storageDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DeskOrganizer", "FenceStorage");
        System.IO.Directory.CreateDirectory(storageDir);

        // 4. 移动快捷方式到存储目录，记录 原始名→新路径 映射
        var movedMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var srcPath in newShortcuts)
        {
            try
            {
                var fileName = System.IO.Path.GetFileName(srcPath);
                var destPath = System.IO.Path.Combine(storageDir, fileName);
                // 避免文件名冲突
                if (System.IO.File.Exists(destPath) && 
                    !string.Equals(srcPath, destPath, StringComparison.OrdinalIgnoreCase))
                {
                    var name = System.IO.Path.GetFileNameWithoutExtension(srcPath);
                    var ext = System.IO.Path.GetExtension(srcPath);
                    int i = 1;
                    while (System.IO.File.Exists(destPath))
                    {
                        destPath = System.IO.Path.Combine(storageDir, $"{name}_{i}{ext}");
                        i++;
                    }
                }

                if (!string.Equals(srcPath, destPath, StringComparison.OrdinalIgnoreCase))
                {
                    System.IO.File.Move(srcPath, destPath);
                    App.Log($"Organize: moved {System.IO.Path.GetFileName(srcPath)} -> storage");
                }
                movedMap[srcPath] = destPath;
            }
            catch (Exception ex)
            {
                App.Log($"Organize: move failed {srcPath} - {ex.Message}");
                movedMap[srcPath] = srcPath; // 移动失败则保留原路径
            }
        }

        // 5. 按分类规则匹配（使用新路径）
        var categorized = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["游戏"] = new(),
            ["办公"] = new(),
            ["开发"] = new(),
            ["浏览器"] = new(),
            ["媒体"] = new(),
            ["系统工具"] = new(),
            ["未分类"] = new()
        };

        foreach (var kv in movedMap)
        {
            var category = CategorizeShortcut(kv.Key); // 用原始路径分类（文件名相同）
            categorized[category].Add(kv.Value);       // 用新路径存储
        }

        // 4. 将分类结果分配到围栏，记录有变化的围栏 ID
        int organized = 0;
        var changedFenceIds = new HashSet<string>();
        foreach (var kv in categorized)
        {
            if (kv.Value.Count == 0) continue;

            // 查找同名的围栏，没有则创建
            var fence = config.Boxes.FirstOrDefault(f =>
                f.Name.Equals(kv.Key, StringComparison.OrdinalIgnoreCase));

            if (fence == null)
            {
                fence = new FenceInfo
                {
                    Name = kv.Key,
                    Width = 280,
                    Height = 360,
                    BackgroundColor = "#40202A3A",
                    Opacity = 0.75,
                    DesktopIndex = App.CurrentDesktopIndex,
                    CreatedAt = DateTime.UtcNow,
                    ModifiedAt = DateTime.UtcNow
                };
                config.Boxes.Add(fence);
            }

            if (fence.FilePaths == null) fence.FilePaths = new List<string>();
            fence.FilePaths.AddRange(kv.Value);
            fence.ModifiedAt = DateTime.UtcNow;
            organized += kv.Value.Count;
            changedFenceIds.Add(fence.Id);
        }

        // 5. 保存配置
        try { ConfigService.Instance.Save(); } catch (Exception ex) { App.Log($"[FenceManager] ConfigService.Save failed: {ex.Message}"); }

        // 6. 为新创建的围栏创建窗口
        foreach (var kv in categorized)
        {
            if (kv.Value.Count == 0) continue;
            var fence = config.Boxes.FirstOrDefault(f => f.Name.Equals(kv.Key, StringComparison.OrdinalIgnoreCase));
            if (fence != null && !_fenceWindows.ContainsKey(fence.Id))
            {
                CreateFenceWindow(fence);
            }
        }

        // 7. 只刷新有变化的已有围栏（不清空缩略图缓存）
        foreach (var fenceId in changedFenceIds)
        {
            if (_fenceWindows.TryGetValue(fenceId, out var w))
            {
                var info = config.Boxes.FirstOrDefault(f => f.Id == fenceId);
                if (info == null) continue;
                if (w.InvokeRequired)
                    w.BeginInvoke(() => w.AppendEntriesFromFenceInfo(info));
                else
                    w.AppendEntriesFromFenceInfo(info);
            }
        }

        return (shortcuts.Count, organized, categorized["未分类"].Count);
    }

    /// <summary>根据快捷方式文件名关键词分类。</summary>
    private static string CategorizeShortcut(string lnkPath)
    {
        try
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(lnkPath).ToLowerInvariant();

            // AI 工具
            var aiKeywords = new[] { "ai", "chatgpt", "claude", "gpt", "llm", "copilot",
                "blinko", "chatbox", "monica", "comfyui", "comfy", "sillytavern",
                "qwenpaw", "voicebox", "echo", "workbuddy", "codebuddy", "eim",
                "qclaw", "trae", "qoder", "cursor", "deepseek", "kimi", "doubao",
                "通义", "文心", "智谱", "豆包", "kling", "seedance" };
            if (aiKeywords.Any(k => name.Contains(k))) return "AI工具";

            // 游戏关键词（精确匹配，避免误判）
            var gameKeywords = new[] { "steam", "epic games", "origin", "ubisoft", "battle.net", "riot",
                "原神", "王者荣耀", "英雄联盟", "lol", "csgo", "dota", "minecraft",
                "gta", "侠盗", "subnautica", "无人深空", "鸣潮", "3dmark", "小黑盒",
                "rockstar", "roblox", "我的世界", "绝地求生", "pubg", "apex",
                "守望先锋", "overwatch", " valorant", "黑神话", "wukong",
                "幸福工厂", "satisfactory" };
            if (gameKeywords.Any(k => name.Contains(k))) return "游戏";

            // 浏览器
            var browserKeywords = new[] { "chrome", "firefox", "edge", "opera", "brave", "vivaldi",
                "浏览器", "360浏览器", "搜狗", "arc", "tor" };
            if (browserKeywords.Any(k => name.Contains(k))) return "浏览器";

            // 开发工具
            var devKeywords = new[] { "visual studio", "vscode", "idea", "pycharm", "eclipse",
                "git", "docker", "node", "python", "java", "devenv", "rider", "webstorm",
                "开发", "编程", "terminal", "powershell", "cmd", "arduino", "kicad",
                "嘉立创", "eda", "altium", "termius", "tabby", "postman", "insomnia",
                "mysql", "redis", "mongodb", "dbeaver", "navicat", "heidi",
                "intellij", "clion", "goland", "rustrover", "llvm", "cmake",
                "idf", "esp32", "stm32", "platformio", "arduino" };
            if (devKeywords.Any(k => name.Contains(k))) return "开发";

            // 办公/通信
            var officeKeywords = new[] { "word", "excel", "powerpoint", "outlook", "wps", "office",
                "办公", "钉钉", "飞书", "微信", "qq", "teams", "notion", "evernote",
                "印象笔记", "腾讯会议", "zoom", "slack", "xmind", "mindmaster",
                "亿图", "有道翻译", "typora", "obsidian", "logseq", "markmap" };
            if (officeKeywords.Any(k => name.Contains(k))) return "办公";

            // 媒体
            var mediaKeywords = new[] { "vlc", "potplayer", "kmplayer", "spotify",
                "网易云", "qq音乐", "foobar", "kuaishou", "抖音", "bilibili", "哔哩",
                "obs", "pr", "ae", "photoshop", "ps ", "剪映", "播放器",
                "优酷", "爱奇艺", "夸克", "腾讯视频", "迅雷", "百度网盘" };
            if (mediaKeywords.Any(k => name.Contains(k))) return "媒体";

            // 系统工具
            var sysKeywords = new[] { "控制面板", "registry", "taskmgr", "资源监视器", "磁盘",
                "backup", "还原", "清理", "defender", "安全", "vpn", "clash", "flclash",
                "7-zip", "winrar", "bandizip", "everything", "listary", "totalcommander",
                "ccleaner", "geek", "uninstaller", "sandboxie", "vmware", "virtualbox",
                "wallpaper engine", "wallpaper", "wallpaper", "displaywidget",
                "winhance", "antigravity", "hi bit", "onecommander", "3dsource" };
            if (sysKeywords.Any(k => name.Contains(k))) return "系统工具";
        }
        catch (Exception ex) { App.Log($"[FenceManager] CategorizeShortcut failed for '{lnkPath}': {ex.Message}"); }

        return "未分类";
    }

    /// <summary>
    /// 将一组条目路径按分类规则分配到对应围栏。
    /// </summary>
    /// <summary>直接序列化 config 到文件，绕过 ConfigService 的缓存机制。</summary>
    private void DirectSaveConfig(AppConfig config)
    {
        try
        {
            var configPath = ConfigService.Instance.GetConfigFilePath();
            var json = System.Text.Json.JsonSerializer.Serialize(config, _jsonOpts);
            System.IO.File.WriteAllText(configPath, json);
        }
        catch (Exception ex)
        {
            App.Log($"[FenceManager] DirectSaveConfig FAILED: {ex.Message}");
        }
    }

    private void OrganizeEntriesToFences(string sourceFenceId, List<string> paths)
    {
        var config = ConfigService.Instance.Config;
        if (config.Boxes == null) return;

        // 按分类
        var categorized = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in paths)
        {
            var cat = CategorizeShortcut(p);
            if (!categorized.ContainsKey(cat)) categorized[cat] = new();
            categorized[cat].Add(p);
        }

        // 从源围栏中移除这些路径
        var sourceFence = config.Boxes.FirstOrDefault(f => f.Id == sourceFenceId);
        if (sourceFence?.FilePaths != null)
        {
            var pathSet = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            sourceFence.FilePaths.RemoveAll(p => pathSet.Contains(p));
        }

        // 将分类后的路径添加到对应围栏
        var changedFenceIds = new HashSet<string> { sourceFenceId };
        foreach (var kv in categorized)
        {
            var fence = config.Boxes.FirstOrDefault(f => f.Name.Equals(kv.Key, StringComparison.OrdinalIgnoreCase));
            if (fence == null)
            {
                fence = new FenceInfo
                {
                    Name = kv.Key,
                    Width = 280,
                    Height = 360,
                    BackgroundColor = "#40202A3A",
                    Opacity = 0.75,
                    DesktopIndex = App.CurrentDesktopIndex,
                    CreatedAt = DateTime.UtcNow,
                    ModifiedAt = DateTime.UtcNow
                };
                config.Boxes.Add(fence);
                // 新围栏需要创建窗口（在 UI 线程上）
                App.Current.Dispatcher.BeginInvoke(new Action(() => CreateFence(kv.Key)));
            }

            if (fence.FilePaths == null) fence.FilePaths = new();
            var existing = new HashSet<string>(fence.FilePaths, StringComparer.OrdinalIgnoreCase);
            foreach (var p in kv.Value)
            {
                if (!existing.Contains(p))
                {
                    fence.FilePaths.Add(p);
                    existing.Add(p);
                }
            }
            fence.ModifiedAt = DateTime.UtcNow;
            changedFenceIds.Add(fence.Id);
        }

        try { ConfigService.Instance.Save(); } catch (Exception ex) { App.Log($"[FenceManager] ConfigService.Save failed: {ex.Message}"); }

        // 刷新源围栏（清空后重新加载）
        if (_fenceWindows.TryGetValue(sourceFenceId, out var srcWin))
        {
            var srcInfo = config.Boxes.FirstOrDefault(f => f.Id == sourceFenceId);
            if (srcInfo != null)
            {
                if (srcWin.InvokeRequired)
                    srcWin.BeginInvoke(() => srcWin.LoadFromModelFenceInfo(srcInfo));
                else
                    srcWin.LoadFromModelFenceInfo(srcInfo);
            }
        }

        // 刷新目标围栏（追加新条目，保留已有图标）
        foreach (var fenceId in changedFenceIds)
        {
            if (fenceId == sourceFenceId) continue;
            if (_fenceWindows.TryGetValue(fenceId, out var targetWin))
            {
                var targetInfo = config.Boxes.FirstOrDefault(f => f.Id == fenceId);
                if (targetInfo != null)
                {
                    if (targetWin.InvokeRequired)
                        targetWin.BeginInvoke(() => targetWin.AppendEntriesFromFenceInfo(targetInfo));
                    else
                        targetWin.AppendEntriesFromFenceInfo(targetInfo);
                }
            }
        }
    }

    /// <summary>刷新所有围栏窗口内容。</summary>
    private void RefreshAllFences()
    {
        var config = ConfigService.Instance.Config;
        foreach (var kv in _fenceWindows)
        {
            var w = kv.Value;
            var info = config.Boxes.FirstOrDefault(f => f.Id == kv.Key);
            if (info == null) continue;
            if (w.InvokeRequired)
                w.BeginInvoke(() => w.LoadFromModelFenceInfo(info));
            else
                w.LoadFromModelFenceInfo(info);
        }
    }

    /// <summary>根据虚拟桌面索引显示/隐藏围栏窗口。只显示归属于指定桌面的围栏。</summary>
    public void ShowFencesForDesktop(int desktopIndex)
    {
        var config = ConfigService.Instance.Config;
        foreach (var kvp in _fenceWindows)
        {
            var window = kvp.Value;
            var fenceInfo = config.Boxes?.FirstOrDefault(b => b.Id == kvp.Key);
            var belongsToDesktop = fenceInfo?.DesktopIndex == desktopIndex;

            if (window.InvokeRequired)
            {
                var visible = belongsToDesktop;
                window.BeginInvoke(() =>
                {
                    if (visible) ShowFenceWindow(window);
                    else window.Hide();
                });
            }
            else
            {
                if (belongsToDesktop) ShowFenceWindow(window); else window.Hide();
            }
        }
    }

    /// <summary>关闭所有围栏窗口并清空追踪表。</summary>
    public void CloseAllFences()
    {
        foreach (var kvp in _fenceWindows)
        {
            var window = kvp.Value;
            try
            {
                // 异步关闭，避免跨线程 Invoke 死锁（STA 线程的 Application.Run 会处理 Close）
                if (window.InvokeRequired)
                    window.BeginInvoke(new Action(() => { window.Close(); }));
                else
                    window.Close();
            }
            catch (Exception ex)
            {
                App.Log($"[FenceManager] Close fence failed: {ex.Message}");
            }
        }
        _fenceWindows.Clear();
    }

    /// <summary>强制关闭所有围栏线程（用于退出时确保进程终止）。</summary>
    public void ForceTerminateAllFenceThreads()
    {
        foreach (var kvp in _fenceWindows)
        {
            var window = kvp.Value;
            try
            {
                // 直接退出该线程的消息泵，不等 Close 回调
                if (window.InvokeRequired)
                {
                    window.BeginInvoke(new Action(() =>
                    {
                        System.Windows.Forms.Application.ExitThread();
                    }));
                }
            }
            catch { }
        }
        _fenceWindows.Clear();
    }

    /// <summary>根据 Id 获取围栏窗口实例。</summary>
    public NoFences.FenceWindow? GetFenceWindow(string fenceId)
    {
        _fenceWindows.TryGetValue(fenceId, out var window);
        return window;
    }

    /// <summary>吸附围栏到邻近围栏边缘并对齐，禁止重叠。</summary>
    public void SnapAndPreventOverlap(string fenceId, ref int x, ref int y, int width, int height)
    {
        const int SNAP_DISTANCE = 20;
        int bestDx = 0, bestDy = 0;
        int bestDxDist = SNAP_DISTANCE + 1;
        int bestDyDist = SNAP_DISTANCE + 1;

        var config = ConfigService.Instance.Config;
        foreach (var other in config.Boxes ?? Enumerable.Empty<FenceInfo>())
        {
            if (other.Id == fenceId) continue;
            var ow = (int)other.Width;
            var oh = (int)other.Height;
            var ox = (int)other.X;
            var oy = (int)other.Y;

            // 水平吸附检测（独立判断，不与垂直互相干扰）
            int dx = CheckSnapAxis(x, width, ox, ow);
            if (dx != 0 && Math.Abs(dx) < bestDxDist)
            {
                bestDx = dx;
                bestDxDist = Math.Abs(dx);
            }

            // 垂直吸附检测（独立判断）
            int dy = CheckSnapAxis(y, height, oy, oh);
            if (dy != 0 && Math.Abs(dy) < bestDyDist)
            {
                bestDy = dy;
                bestDyDist = Math.Abs(dy);
            }
        }

        x += bestDx;
        y += bestDy;

        // 禁止重叠：推出
        foreach (var other in config.Boxes ?? Enumerable.Empty<FenceInfo>())
        {
            if (other.Id == fenceId) continue;
            var ox = (int)other.X;
            var oy = (int)other.Y;
            var ow = (int)other.Width;
            var oh = (int)other.Height;

            if (x < ox + ow && x + width > ox && y < oy + oh && y + height > oy)
            {
                int pushRight = (ox + ow) - x + 2;   // 向右推
                int pushLeft = x - (ox - width) + 2;   // 向左推
                int pushDown = (oy + oh) - y + 2;      // 向下推
                int pushUp = y - (oy - height) + 2;     // 向上推

                int minPush = Math.Min(Math.Min(pushRight, pushLeft), Math.Min(pushDown, pushUp));

                if (minPush == pushRight) x += pushRight;
                else if (minPush == pushLeft) x -= pushLeft;
                else if (minPush == pushDown) y += pushDown;
                else y -= pushUp;
            }
        }

        // 限制在屏幕范围内
        int screenW = (int)System.Windows.SystemParameters.PrimaryScreenWidth;
        int screenH = (int)System.Windows.SystemParameters.PrimaryScreenHeight;
        x = Math.Max(0, Math.Min(x, screenW - width));
        y = Math.Max(0, Math.Min(y, screenH - height));
    }

    /// <summary>检测单轴吸附（左-左、右-左、左-右、右-右、居中对齐）。</summary>
    private static int CheckSnapAxis(int pos, int size, int otherPos, int otherSize)
    {
        const int SNAP_DISTANCE = 20;
        int dist;

        // 左边对齐到另一围栏左边
        dist = pos - otherPos;
        if (Math.Abs(dist) <= SNAP_DISTANCE) return -dist;

        // 左边对齐到另一围栏右边
        dist = pos - (otherPos + otherSize);
        if (Math.Abs(dist) <= SNAP_DISTANCE) return -dist;

        // 右边对齐到另一围栏左边
        dist = (pos + size) - otherPos;
        if (Math.Abs(dist) <= SNAP_DISTANCE) return -dist;

        // 右边对齐到另一围栏右边
        dist = (pos + size) - (otherPos + otherSize);
        if (Math.Abs(dist) <= SNAP_DISTANCE) return -dist;

        // 居中对齐
        dist = (pos + size / 2) - (otherPos + otherSize / 2);
        if (Math.Abs(dist) <= SNAP_DISTANCE) return -dist;

        return 0; // 不在吸附范围内
    }

    /// <summary>获取当前活跃围栏窗口数量。</summary>
    public int ActiveFenceCount => _fenceWindows.Count;

    /// <summary>
    /// 内部方法：创建围栏窗口并将其注册到追踪表。
    /// </summary>
    private void CreateFenceWindow(FenceInfo fence, System.Threading.ManualResetEventSlim? readySignal = null)
    {
        try
        {
            // WinForms 窗口必须在有 WinForms 消息泵的线程上创建才能正确显示
            // 使用独立 STA 线程运行 WinForms Application.Run
            var thread = new Thread(() =>
            {
                System.Windows.Forms.Application.EnableVisualStyles();
                System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);

                var window = new NoFences.FenceWindow(fence);
                window.FenceChanged += w => UpdateFenceFromWindow(fence, w);
                window.RequestNewFence += w => CreateFence(w.Name);
                window.RequestDeleteFence += w => RemoveFence(fence);
                window.OrganizeFenceEntriesRequested += (sourceFenceId, paths) =>
                {
                    OrganizeEntriesToFences(sourceFenceId, paths);
                };
                _fenceWindows.TryAdd(fence.Id, window);

                // FormClosed 事件中清理引用并退出线程消息泵（合并为一个处理器，避免重复注册）
                window.FormClosed += (_, _) =>
                {
                    _fenceWindows.TryRemove(fence.Id, out _);
                    System.Windows.Forms.Application.ExitThread();
                };

                App.Log($"Fence '{fence.Name}' showing on STA thread. Handle={window.Handle}");

                // 先设置位置再显示，避免 Show() 后位置被重置为 0,0
                // PosX/PosY 为 0 是合法位置（屏幕左上角），只有两者都为 0 且 X/Y 也为 0 时才视为未初始化
                int posX = fence.PosX != 0 ? fence.PosX : (int)fence.X;
                int posY = fence.PosY != 0 ? fence.PosY : (int)fence.Y;
                if (posX == 0 && posY == 0 && fence.Width == 0 && fence.Height == 0)
                {
                    // 仅在完全未初始化时才自动错开位置
                    var cfg = ConfigService.Instance.Config;
                    int idx = cfg.Boxes.Count;
                    posX = 100 + (idx % 5) * 320;
                    posY = 80 + (idx % 5) * 60;
                }

                // 临时禁用 FenceChanged 回调（通过设置 _isLoaded = false）
                window.SuppressFenceChanged = true;
                window.Location = new System.Drawing.Point(posX, posY);
                window.Size = new System.Drawing.Size((int)fence.Width, (int)fence.Height);

                window.Show();

                // 桌面底层模式：ToolWindow + HWND_BOTTOM（不遮挡普通窗口，在桌面图标之上可见）
                NoFences.Win32.WindowUtil.EnableToolWindow(window.Handle);
                NoFences.Win32.WindowUtil.SendToBack(window.Handle);
                App.Log($"Fence '{fence.Name}' display: Bottom mode, Visible={window.Visible}, Location=({posX},{posY})");

                // 显示后确保位置正确
                window.Location = new System.Drawing.Point(posX, posY);

                // 恢复 FenceChanged 回调，从窗口读取实际位置（Show/SendToBack 可能改变了位置）
                window.SuppressFenceChanged = false;
                var actualX = window.Location.X;
                var actualY = window.Location.Y;
                fence.X = actualX;
                fence.Y = actualY;
                fence.PosX = actualX;
                fence.PosY = actualY;

                // 窗口已就绪，通知等待方（CreateFence 可立即显示桌面，无需盲等 1 秒）
                readySignal?.Set();

                // 运行 WinForms 消息泵（阻塞直到 Application.Exit 被调用）
                System.Windows.Forms.Application.Run();

                window.Dispose();
            })
            {
                IsBackground = true,
                Name = $"FenceThread-{fence.Name}"
            };
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
        }
        catch (Exception ex)
        {
            App.Log($"[FenceManager] CreateFenceWindow failed for '{fence.Name}': {ex.Message}");
            readySignal?.Set(); // 失败也要释放信号，避免调用方死等
        }
    }

    /// <summary>从围栏窗口同步数据到 FenceInfo 并持久化（吸附由窗口 WM_WINDOWPOSCHANGING 实时处理）。</summary>
    private void UpdateFenceFromWindow(FenceInfo fence, NoFences.FenceWindow window)
    {
        var info = window.GetFenceInfo();
        fence.Name = info.Name;
        fence.X = info.PosX != 0 ? info.PosX : info.X;
        fence.Y = info.PosY != 0 ? info.PosY : info.Y;
        fence.PosX = (int)fence.X;
        fence.PosY = (int)fence.Y;
        fence.Width = info.Width;
        fence.Height = info.Height;
        fence.BackgroundColor = info.BackgroundColor;
        fence.Opacity = info.Opacity;
        fence.BlurEnabled = info.BlurEnabled;
        fence.TitleHeight = info.TitleHeight;
        fence.FilePaths = info.FilePaths;
        fence.ModifiedAt = DateTime.UtcNow;

        App.Log($"[FenceManager] UpdateFenceFromWindow '{fence.Name}': opacity={fence.Opacity}, blur={fence.BlurEnabled}, titleHeight={fence.TitleHeight}");

        // 确保缓存配置中也是同一个 fence 对象引用
        var cfg = ConfigService.Instance.Config;
        var idx = cfg.Boxes.FindIndex(b => b.Id == fence.Id);
        if (idx >= 0)
            cfg.Boxes[idx] = fence;
        try { ConfigService.Instance.Save(); App.Log($"[FenceManager] ConfigService.Save() OK, opacity={fence.Opacity}"); } catch (Exception ex) { App.Log($"[FenceManager] Save config failed: {ex.Message}"); }
        try { ConfigService.Instance.SaveFenceData(fence); } catch (Exception ex) { App.Log($"[FenceManager] SaveFenceData failed: {ex.Message}"); }
    }
}

