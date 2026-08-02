using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace DeskOrganizer.Model;

/// <summary>
/// GitHub Release 信息（仅包含所需字段）。
/// </summary>
public class GitHubRelease
{
    [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("body")] public string Body { get; set; } = "";
    [JsonPropertyName("published_at")] public string PublishedAt { get; set; } = "";
    [JsonPropertyName("html_url")] public string HtmlUrl { get; set; } = "";
    [JsonPropertyName("assets")] public List<GitHubAsset> Assets { get; set; } = new();
}

/// <summary>
/// GitHub Release 资产（附件文件）。
/// </summary>
public class GitHubAsset
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
}

/// <summary>
/// 更新检查结果。
/// </summary>
public class UpdateCheckResult
{
    public bool HasUpdate { get; set; }
    public string LatestVersion { get; set; } = "";
    public string CurrentVersion { get; set; } = "";
    public string ReleaseNotes { get; set; } = "";
    public string PublishedDate { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public string HtmlUrl { get; set; } = "";
    public long DownloadSize { get; set; }
    public string Error { get; set; } = "";
}

/// <summary>
/// 自动更新服务：检查 GitHub Releases，静默下载到暂存目录，重启时应用更新。
/// 更新逻辑完全内嵌于主程序，不依赖独立的 Updater.exe。
/// </summary>
public class UpdateService
{
    private const string RepoOwner = "Quantum-and-photon";
    private const string RepoName = "DeskOrganizer";
    private const string ApiBase = "https://api.github.com/repos";

    private static readonly HttpClient _http;

    // ---- Job Object 脱离：P/Invoke ----
    // 当父进程运行在 Job Object 中时（如 TRAE SOLO CN 沙箱），
    // 进程退出会杀死所有子进程。需要用 CREATE_BREAKAWAY_FROM_JOB
    // 标志启动 cmd.exe，使其脱离 Job Object 成为独立进程。

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string lpApplicationName,
        IntPtr lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public uint dwX;
        public uint dwY;
        public uint dwXSize;
        public uint dwYSize;
        public uint dwXCountChars;
        public uint dwYCountChars;
        public uint dwFillAttribute;
        public uint dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    // CREATE_BREAKAWAY_FROM_JOB=0x01000000
    // 注意：不用 CREATE_NO_WINDOW（0x08000000），因为它会导致 cmd.exe 没有控制台 handle，
    // 批处理脚本完全无法执行。改用 STARTUPINFO 的 SW_HIDE 隐藏窗口。
    private const uint CREATE_FLAGS = 0x01000000;
    private const uint STARTF_USESHOWWINDOW = 0x00000001;
    private const short SW_HIDE = 0;

    /// <summary>
    /// 启动独立 cmd.exe 进程执行批处理脚本，使用 CREATE_BREAKAWAY_FROM_JOB
    /// 脱离 Job Object，确保父进程退出时不会被终止。
    /// </summary>
    private static bool StartDetachedCmd(string scriptPath)
    {
        // 构造命令行：cmd.exe /c "scriptPath"
        // CreateProcessW 的 lpCommandLine 需要可写缓冲区
        var cmdLine = $"cmd.exe /c \"{scriptPath}\"";
        var cmdLineBytes = System.Text.Encoding.Unicode.GetBytes(cmdLine + "\0");
        var cmdLinePtr = Marshal.AllocHGlobal(cmdLineBytes.Length);
        try
        {
            Marshal.Copy(cmdLineBytes, 0, cmdLinePtr, cmdLineBytes.Length);

            var si = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                dwFlags = STARTF_USESHOWWINDOW,
                wShowWindow = SW_HIDE
            };

            if (CreateProcessW(
                null,
                cmdLinePtr,  // 可写命令行缓冲区
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                CREATE_FLAGS,
                IntPtr.Zero,
                null,
                ref si,
                out var pi))
            {
                CloseHandle(pi.hProcess);
                CloseHandle(pi.hThread);
                App.Log($"[UpdateService] Detached cmd.exe started (PID={pi.dwProcessId}) with CREATE_BREAKAWAY_FROM_JOB");
                return true;
            }

            var err = Marshal.GetLastWin32Error();
            App.Log($"[UpdateService] CreateProcessW failed (error={err}), falling back to Process.Start");
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(cmdLinePtr);
        }
    }

    /// <summary>更新包暂存目录（%APPDATA%\DeskOrganizer\update\）。</summary>
    public static string UpdateStagingDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DeskOrganizer", "update");

    /// <summary>暂存的主程序 exe 路径。</summary>
    public static string StagedExePath => Path.Combine(UpdateStagingDir, "DeskOrganizer_v2.exe");

    static UpdateService()
    {
        // 确保 TLS 1.2+ 可用（GitHub API 要求）
        System.Net.ServicePointManager.SecurityProtocol =
            System.Net.SecurityProtocolType.Tls12 | System.Net.SecurityProtocolType.Tls13;

        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _http.DefaultRequestHeaders.Add("User-Agent", "DeskOrganizer-Updater");
        _http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
    }

    /// <summary>获取当前程序版本（从 exe 文件版本信息获取，兼容单文件发布）。</summary>
    public static string GetCurrentVersion()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                return $"{vi.FileMajorPart}.{vi.FileMinorPart}.{vi.FileBuildPart}";
            }
        }
        catch { }
        // 回退：从程序集信息版本获取
        try
        {
            var attr = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            if (attr != null)
            {
                // 取 "+" 前的部分（去掉 commit hash 后缀）
                var ver = attr.InformationalVersion.Split('+')[0];
                return ver;
            }
        }
        catch { }
        return "2.0.0.0";
    }

    /// <summary>
    /// 检查 GitHub 上是否有新版本。
    /// </summary>
    public static async Task<UpdateCheckResult> CheckForUpdateAsync()
    {
        var result = new UpdateCheckResult
        {
            CurrentVersion = GetCurrentVersion()
        };

        try
        {
            var url = $"{ApiBase}/{RepoOwner}/{RepoName}/releases/latest";

            HttpResponseMessage? resp = null;
            try
            {
                resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                result.Error = "连接 GitHub 超时，请检查网络连接";
                return result;
            }
            catch (HttpRequestException hex)
            {
                result.Error = $"网络错误: {hex.Message}";
                return result;
            }

            if (!resp.IsSuccessStatusCode)
            {
                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                    result.Error = "暂无发布版本";
                else
                    result.Error = $"GitHub API 返回错误: {(int)resp.StatusCode} {resp.StatusCode}";
                return result;
            }

            var response = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var release = JsonSerializer.Deserialize<GitHubRelease>(response);

            if (release == null || string.IsNullOrEmpty(release.TagName))
            {
                result.Error = "无法解析 Release 信息";
                return result;
            }

            // 解析版本号：去掉 v 前缀
            var latestVer = release.TagName.TrimStart('v', 'V');
            result.LatestVersion = latestVer;
            result.ReleaseNotes = release.Body ?? "";
            result.PublishedDate = release.PublishedAt ?? "";
            result.HtmlUrl = release.HtmlUrl ?? "";

            // 查找主程序 asset
            var asset = release.Assets?.FirstOrDefault(a =>
                a.Name.Equals("DeskOrganizer_v2.exe", StringComparison.OrdinalIgnoreCase));

            if (asset != null)
            {
                result.DownloadUrl = asset.BrowserDownloadUrl;
                result.DownloadSize = asset.Size;
            }

            // 比较版本号
            result.HasUpdate = IsNewerVersion(latestVer, result.CurrentVersion);
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// 比较版本号，判断 latest 是否比 current 更新。
    /// </summary>
    private static bool IsNewerVersion(string latest, string current)
    {
        if (Version.TryParse(latest, out var latestVer) &&
            Version.TryParse(current, out var currentVer))
        {
            return latestVer > currentVer;
        }
        return string.Compare(latest, current, StringComparison.OrdinalIgnoreCase) > 0;
    }

    // ---- 静默下载 ----

    /// <summary>
    /// 静默下载更新包到暂存目录。下载完成后设置 config.PendingUpdate* 字段。
    /// 返回下载完成的暂存文件路径，失败返回 null。
    /// </summary>
    public static async Task<string?> SilentDownloadAsync(
        string downloadUrl,
        string version,
        long expectedSize = 0,
        IProgress<(long received, long total)>? progress = null)
    {
        if (string.IsNullOrEmpty(downloadUrl))
        {
            App.Log("[UpdateService] SilentDownload: downloadUrl is empty");
            return null;
        }

        try
        {
            Directory.CreateDirectory(UpdateStagingDir);

            // 暂存文件先写入 .part 后缀，下载完成后重命名，避免半成品被误用
            var partPath = StagedExePath + ".part";
            var finalPath = StagedExePath;

            // 清理可能残留的 .part 文件（带重试，防止杀毒软件短暂占用）
            DeleteFileWithRetry(partPath);

            App.Log($"[UpdateService] SilentDownload: downloading v{version} from {downloadUrl}");

            // 下载阶段：所有文件句柄限制在独立 using 块内，确保 Move 前已全部释放
            using (var response = await _http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? expectedSize;

                await using (var contentStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                await using (var fileStream = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true))
                {
                    var buffer = new byte[8192];
                    long received = 0;
                    int bytesRead;

                    while ((bytesRead = await contentStream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead)).ConfigureAwait(false);
                        received += bytesRead;
                        progress?.Report((received, totalBytes));
                    }
                } // fileStream 和 contentStream 在此处释放
            } // response 在此处释放

            // 下载完成，重命名 .part -> 最终文件名（此时所有句柄已释放）
            MoveFileWithRetry(partPath, finalPath);

            // 校验文件大小（如果已知预期大小）
            if (expectedSize > 0)
            {
                var actualSize = new FileInfo(finalPath).Length;
                if (actualSize != expectedSize)
                {
                    App.Log($"[UpdateService] SilentDownload: size mismatch (expected={expectedSize}, actual={actualSize})");
                    try { File.Delete(finalPath); } catch { }
                    return null;
                }
            }

            // 设置待更新状态
            var config = ConfigService.Instance.Config;
            config.PendingUpdateVersion = version;
            config.PendingUpdatePath = finalPath;
            config.PendingUpdateUrl = downloadUrl;
            ConfigService.Instance.Save();

            App.Log($"[UpdateService] SilentDownload: completed, staged at {finalPath} (v{version})");
            return finalPath;
        }
        catch (Exception ex)
        {
            App.Log($"[UpdateService] SilentDownload failed: {ex.Message}");
            // 清理半成品
            try { if (File.Exists(StagedExePath + ".part")) File.Delete(StagedExePath + ".part"); } catch { }
            return null;
        }
    }

    /// <summary>
    /// 下载更新包到暂存目录（用于手动更新流程，带进度回调）。
    /// 返回暂存文件路径。
    /// </summary>
    public static async Task<string> DownloadToUpdateAsync(string downloadUrl, string version, IProgress<(long received, long total)>? progress = null)
    {
        if (string.IsNullOrEmpty(downloadUrl))
            throw new InvalidOperationException("下载地址为空");

        Directory.CreateDirectory(UpdateStagingDir);

        var partPath = StagedExePath + ".part";

        DeleteFileWithRetry(partPath);

        // 下载阶段：所有文件句柄限制在独立 using 块内，确保 Move 前已全部释放
        using (var response = await _http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? 0;

            await using (var contentStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            await using (var fileStream = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true))
            {
                var buffer = new byte[8192];
                long received = 0;
                int bytesRead;

                while ((bytesRead = await contentStream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead)).ConfigureAwait(false);
                    received += bytesRead;
                    progress?.Report((received, totalBytes));
                }
            } // fileStream 和 contentStream 在此处释放
        } // response 在此处释放

        // 重命名 .part -> 最终文件名（此时所有句柄已释放）
        var finalPath = StagedExePath;
        MoveFileWithRetry(partPath, finalPath);

        // 设置待更新状态
        var config = ConfigService.Instance.Config;
        config.PendingUpdateVersion = version;
        config.PendingUpdatePath = finalPath;
        config.PendingUpdateUrl = downloadUrl;
        ConfigService.Instance.Save();

        return finalPath;
    }

    // ---- 待更新状态管理 ----

    /// <summary>删除文件并重试（处理杀毒软件等短暂占用）。</summary>
    private static void DeleteFileWithRetry(string path, int maxRetries = 5, int delayMs = 500)
    {
        if (!File.Exists(path)) return;
        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (IOException) when (i < maxRetries - 1)
            {
                System.Threading.Thread.Sleep(delayMs);
            }
            catch (UnauthorizedAccessException) when (i < maxRetries - 1)
            {
                System.Threading.Thread.Sleep(delayMs);
            }
        }
        // 最后一次尝试，失败则抛出
        File.Delete(path);
    }

    /// <summary>移动文件并重试（处理杀毒软件等短暂占用目标文件）。</summary>
    private static void MoveFileWithRetry(string sourcePath, string destPath, int maxRetries = 5, int delayMs = 500)
    {
        // 先尝试删除目标文件（如果存在）
        if (File.Exists(destPath))
            DeleteFileWithRetry(destPath, maxRetries, delayMs);

        // 移动源文件到目标
        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                File.Move(sourcePath, destPath);
                return;
            }
            catch (IOException) when (i < maxRetries - 1)
            {
                System.Threading.Thread.Sleep(delayMs);
            }
            catch (UnauthorizedAccessException) when (i < maxRetries - 1)
            {
                System.Threading.Thread.Sleep(delayMs);
            }
        }
        // 最后一次尝试，失败则抛出
        File.Move(sourcePath, destPath);
    }

    /// <summary>是否存在待应用的更新（暂存文件存在且配置中有记录）。</summary>
    public static bool HasPendingUpdate()
    {
        var config = ConfigService.Instance.Config;
        if (string.IsNullOrEmpty(config.PendingUpdatePath) || !File.Exists(config.PendingUpdatePath))
            return false;

        // 校验暂存版本仍然比当前版本新
        var currentVer = GetCurrentVersion();
        if (!string.IsNullOrEmpty(config.PendingUpdateVersion) &&
            !IsNewerVersion(config.PendingUpdateVersion, currentVer))
        {
            App.Log($"[UpdateService] Pending update v{config.PendingUpdateVersion} is not newer than current v{currentVer}, clearing");
            ClearPendingUpdate();
            return false;
        }

        return true;
    }

    /// <summary>清除待更新状态（删除暂存文件 + 清空配置字段）。</summary>
    public static void ClearPendingUpdate()
    {
        var config = ConfigService.Instance.Config;

        // 删除暂存文件
        if (!string.IsNullOrEmpty(config.PendingUpdatePath) && File.Exists(config.PendingUpdatePath))
        {
            try { File.Delete(config.PendingUpdatePath); } catch { }
        }

        // 清理 .part 残留
        var partPath = StagedExePath + ".part";
        if (File.Exists(partPath))
        {
            try { File.Delete(partPath); } catch { }
        }

        config.PendingUpdateVersion = "";
        config.PendingUpdatePath = "";
        config.PendingUpdateUrl = "";
        ConfigService.Instance.Save();

        App.Log("[UpdateService] Pending update cleared");
    }

    // ---- 重启即更新 ----

    /// <summary>
    /// 在程序退出时应用待更新（生成批处理脚本，等待进程退出后替换 exe）。
    /// restart=true 时替换后自动重启程序；restart=false 时仅替换不重启。
    /// 返回 true 表示已启动更新脚本，调用方应随即退出进程。
    /// </summary>
    public static bool TryApplyPendingUpdateOnExit(bool restart)
    {
        if (!HasPendingUpdate())
        {
            App.Log("[UpdateService] TryApplyPendingUpdateOnExit: no pending update");
            return false;
        }

        var config = ConfigService.Instance.Config;
        var stagedPath = config.PendingUpdatePath;
        var currentExe = Environment.ProcessPath;

        if (string.IsNullOrEmpty(currentExe) || !File.Exists(currentExe))
        {
            App.Log("[UpdateService] TryApplyPendingUpdateOnExit: cannot determine current exe path");
            return false;
        }

        if (!File.Exists(stagedPath))
        {
            App.Log("[UpdateService] TryApplyPendingUpdateOnExit: staged file not found");
            ClearPendingUpdate();
            return false;
        }

        var pid = System.Diagnostics.Process.GetCurrentProcess().Id;
        var logPath = Path.Combine(UpdateStagingDir, "update.log");
        var scriptPath = Path.Combine(UpdateStagingDir, "apply_update.cmd");

        // 生成批处理脚本：等待进程退出 -> 替换 exe -> (可选)重启 -> 自删除
        // 注意：不使用 chcp 65001，避免与脚本文件编码冲突导致中文路径乱码
        // 脚本用 UTF-8 with BOM 编码，Windows 10+ cmd.exe 可自动识别
        var script = $@"@echo off
echo [{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Update script started (PID={pid}, restart={restart}) >> ""{logPath}""
echo [%date% %time%] Script path: %~f0 >> ""{logPath}""
echo [%date% %time%] Working dir: %CD% >> ""{logPath}""
echo [%date% %time%] Source: {stagedPath} >> ""{logPath}""
echo [%date% %time%] Target: {currentExe} >> ""{logPath}""

:wait_exit
timeout /t 1 /nobreak >nul 2>&1
tasklist /fi ""PID eq {pid}"" 2>nul | find ""{pid}"" >nul 2>&1
if not errorlevel 1 goto wait_exit

echo [%date% %time%] Process exited, waiting for file handle release >> ""{logPath}""
timeout /t 2 /nobreak >nul 2>&1

:replace
timeout /t 1 /nobreak >nul 2>&1
copy /y ""{stagedPath}"" ""{currentExe}"" >nul 2>&1
if errorlevel 1 (
    echo [%date% %time%] Copy failed (errorlevel=%errorlevel%), retrying... >> ""{logPath}""
    goto replace
)

echo [%date% %time%] File replaced successfully >> ""{logPath}""

del ""{stagedPath}"" >nul 2>&1
";

        if (restart)
        {
            script += $@"
echo [{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Restarting application >> ""{logPath}""
start """" ""{currentExe}""
";
        }

        script += $@"
echo [{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Update complete >> ""{logPath}""
del ""%~f0"" >nul 2>&1
";

        try
        {
            Directory.CreateDirectory(UpdateStagingDir);
            // UTF-8 with BOM：Windows 10+ cmd.exe 自动识别，无需 chcp
            File.WriteAllText(scriptPath, script, new System.Text.UTF8Encoding(true));

            App.Log($"[UpdateService] Spawning update script: {scriptPath} (PID={pid}, restart={restart})");

            // 首选 UseShellExecute=true：通过 Shell 启动独立进程，cmd.exe 有完整控制台 handle
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"{scriptPath}\"",
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(psi);
                App.Log("[UpdateService] Update script started via Process.Start (UseShellExecute=true)");
            }
            catch (Exception ex)
            {
                // 回退：用 CreateProcessW + CREATE_BREAKAWAY_FROM_JOB（用于 Job Object 沙箱环境）
                App.Log($"[UpdateService] Process.Start failed: {ex.Message}, trying StartDetachedCmd");
                if (!StartDetachedCmd(scriptPath))
                {
                    App.Log("[UpdateService] All launch methods failed");
                    throw;
                }
            }

            // 清除配置中的待更新标记（文件由脚本删除）
            config.PendingUpdateVersion = "";
            config.PendingUpdatePath = "";
            config.PendingUpdateUrl = "";
            ConfigService.Instance.Save();

            return true;
        }
        catch (Exception ex)
        {
            App.Log($"[UpdateService] TryApplyPendingUpdateOnExit failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 立即应用更新（用户手动点击"立即更新"时调用）。
    /// 生成批处理脚本，等待进程退出后替换 exe 并重启。
    /// 调用方应在调用此方法后立即退出进程。
    /// </summary>
    public static bool ApplyUpdateNow(string stagedExePath)
    {
        var currentExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(currentExe) || !File.Exists(currentExe))
        {
            App.Log("[UpdateService] ApplyUpdateNow: cannot determine current exe path");
            return false;
        }

        if (!File.Exists(stagedExePath))
        {
            App.Log($"[UpdateService] ApplyUpdateNow: staged file not found: {stagedExePath}");
            return false;
        }

        var pid = System.Diagnostics.Process.GetCurrentProcess().Id;
        var logPath = Path.Combine(UpdateStagingDir, "update.log");
        var scriptPath = Path.Combine(UpdateStagingDir, "apply_update.cmd");

        var script = $@"@echo off
echo [{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Immediate update started (PID={pid}) >> ""{logPath}""
echo [%date% %time%] Script path: %~f0 >> ""{logPath}""
echo [%date% %time%] Working dir: %CD% >> ""{logPath}""
echo [%date% %time%] Source: {stagedExePath} >> ""{logPath}""
echo [%date% %time%] Target: {currentExe} >> ""{logPath}""

:wait_exit
timeout /t 1 /nobreak >nul 2>&1
tasklist /fi ""PID eq {pid}"" 2>nul | find ""{pid}"" >nul 2>&1
if not errorlevel 1 goto wait_exit

echo [%date% %time%] Process exited >> ""{logPath}""
timeout /t 2 /nobreak >nul 2>&1

:replace
timeout /t 1 /nobreak >nul 2>&1
copy /y ""{stagedExePath}"" ""{currentExe}"" >nul 2>&1
if errorlevel 1 (
    echo [%date% %time%] Copy failed (errorlevel=%errorlevel%), retrying... >> ""{logPath}""
    goto replace
)

echo [%date% %time%] File replaced, restarting >> ""{logPath}""
del ""{stagedExePath}"" >nul 2>&1
start """" ""{currentExe}""
echo [%date% %time%] Update complete >> ""{logPath}""
del ""%~f0"" >nul 2>&1
";

        try
        {
            Directory.CreateDirectory(UpdateStagingDir);
            // UTF-8 with BOM：Windows 10+ cmd.exe 自动识别，无需 chcp
            File.WriteAllText(scriptPath, script, new System.Text.UTF8Encoding(true));

            App.Log($"[UpdateService] ApplyUpdateNow: spawning update script (PID={pid})");

            // 首选 UseShellExecute=true：通过 Shell 启动独立进程，cmd.exe 有完整控制台 handle
            // 这是 v2.6.3/v2.6.4 验证过能正常工作的方式
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"{scriptPath}\"",
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(psi);
                App.Log("[UpdateService] Update script started via Process.Start (UseShellExecute=true)");
            }
            catch (Exception ex)
            {
                // 回退：用 CreateProcessW + CREATE_BREAKAWAY_FROM_JOB（用于 Job Object 沙箱环境）
                App.Log($"[UpdateService] Process.Start failed: {ex.Message}, trying StartDetachedCmd");
                if (!StartDetachedCmd(scriptPath))
                {
                    App.Log("[UpdateService] All launch methods failed");
                    throw;
                }
            }

            // 清除待更新标记
            var config = ConfigService.Instance.Config;
            config.PendingUpdateVersion = "";
            config.PendingUpdatePath = "";
            config.PendingUpdateUrl = "";
            ConfigService.Instance.Save();

            return true;
        }
        catch (Exception ex)
        {
            App.Log($"[UpdateService] ApplyUpdateNow failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 清理上次更新遗留的 .old 备份文件（在启动时调用）。
    /// </summary>
    public static void CleanupOldBackup()
    {
        try
        {
            var currentExe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(currentExe)) return;

            var backupPath = currentExe + ".old";
            if (File.Exists(backupPath))
            {
                // 尝试删除，失败则忽略（可能仍被占用）
                for (int i = 0; i < 5; i++)
                {
                    try
                    {
                        File.Delete(backupPath);
                        App.Log($"[UpdateService] Cleaned up old backup: {backupPath}");
                        break;
                    }
                    catch
                    {
                        System.Threading.Thread.Sleep(500);
                    }
                }
            }
        }
        catch { }
    }
}
