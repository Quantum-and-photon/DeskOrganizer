using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
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
/// 自动更新服务：检查 GitHub Releases，下载并替换程序文件。
/// </summary>
public class UpdateService
{
    private const string RepoOwner = "Quantum-and-photon";
    private const string RepoName = "DeskOrganizer";
    private const string ApiBase = "https://api.github.com/repos";

    /// <summary>Updater.exe 的下载地址（检查更新时获取，当前未使用，保留兼容）。</summary>
    public static string? UpdaterDownloadUrl { get; private set; }

    private static readonly HttpClient _http;

    static UpdateService()
    {
        // 确保 TLS 1.2+ 可用（GitHub API 要求）
        System.Net.ServicePointManager.SecurityProtocol =
            System.Net.SecurityProtocolType.Tls12 | System.Net.SecurityProtocolType.Tls13;

        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
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

            // 查找主程序 asset（排除 updater）
            var asset = release.Assets?.FirstOrDefault(a =>
                a.Name.Equals("DeskOrganizer_v2.exe", StringComparison.OrdinalIgnoreCase));

            if (asset != null)
            {
                result.DownloadUrl = asset.BrowserDownloadUrl;
                result.DownloadSize = asset.Size;
            }

            // 查找 updater asset
            var updaterAsset = release.Assets?.FirstOrDefault(a =>
                a.Name.Equals("DeskOrganizerUpdater.exe", StringComparison.OrdinalIgnoreCase));
            if (updaterAsset != null)
            {
                UpdaterDownloadUrl = updaterAsset.BrowserDownloadUrl;
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

    /// <summary>
    /// 下载更新包到临时目录，返回下载的文件路径。
    /// </summary>
    public static async Task<string> DownloadUpdateAsync(string downloadUrl, IProgress<(long received, long total)>? progress = null)
    {
        if (string.IsNullOrEmpty(downloadUrl))
            throw new InvalidOperationException("下载地址为空");

        var tempDir = Path.Combine(Path.GetTempPath(), "DeskOrganizerUpdate");
        Directory.CreateDirectory(tempDir);

        var fileName = Path.GetFileName(new Uri(downloadUrl).LocalPath);
        var filePath = Path.Combine(tempDir, fileName);

        // 如果文件已存在，先删除
        if (File.Exists(filePath))
            File.Delete(filePath);

        using var response = await _http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? 0;

        await using var contentStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        await using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true);

        var buffer = new byte[8192];
        long received = 0;
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead)).ConfigureAwait(false);
            received += bytesRead;
            progress?.Report((received, totalBytes));
        }

        return filePath;
    }

    /// <summary>
    /// 分层更新架构 - 第1层：主程序启动 Updater.exe
    /// Updater.exe 作为独立进程负责：等待主程序退出 -> 替换文件 -> 重启主程序
    /// 通过 schtasks 计划任务拉起 Updater，确保进程树完全孤立。
    /// </summary>
    public static void ApplyUpdate(string downloadedFilePath, string targetDir)
    {
        var exeName = string.IsNullOrEmpty(Environment.ProcessPath)
            ? "DeskOrganizer_v2.exe"
            : Path.GetFileName(Environment.ProcessPath);
        var exePath = Path.Combine(targetDir, exeName);

        // Updater.exe 路径：先找安装目录，再找临时目录
        var updaterPath = Path.Combine(targetDir, "DeskOrganizerUpdater.exe");
        if (!File.Exists(updaterPath))
        {
            // 尝试从当前运行目录复制
            var currentDir = Path.GetDirectoryName(Environment.ProcessPath) ?? targetDir;
            var sourceUpdater = Path.Combine(currentDir, "DeskOrganizerUpdater.exe");
            var tempUpdater = Path.Combine(Path.GetTempPath(), "DeskOrganizerUpdater.exe");

            if (File.Exists(sourceUpdater) && sourceUpdater != updaterPath)
            {
                try { File.Copy(sourceUpdater, tempUpdater, true); updaterPath = tempUpdater; }
                catch { }
            }

            // 尝试从 GitHub 下载
            if (!File.Exists(updaterPath) && !string.IsNullOrEmpty(UpdaterDownloadUrl))
            {
                try
                {
                    App.Log($"[UpdateService] Downloading updater from GitHub");
                    using var resp = _http.GetAsync(UpdaterDownloadUrl, HttpCompletionOption.ResponseHeadersRead).Result;
                    resp.EnsureSuccessStatusCode();
                    using var fs = new FileStream(tempUpdater, FileMode.Create, FileAccess.Write, FileShare.None);
                    resp.Content.ReadAsStream().CopyTo(fs);
                    updaterPath = tempUpdater;
                    App.Log($"[UpdateService] Updater downloaded to {updaterPath}");
                }
                catch (Exception ex)
                {
                    App.Log($"[UpdateService] Failed to download updater: {ex.Message}");
                }
            }

            // 回退到 PowerShell 脚本
            if (!File.Exists(updaterPath))
            {
                App.Log("[UpdateService] Updater.exe not found, falling back to PowerShell");
                ApplyUpdateWithPowerShell(downloadedFilePath, exePath, exeName);
                return;
            }
        }

        App.Log($"[UpdateService] Starting updater: {updaterPath}");
        App.Log($"[UpdateService] Args: \"{downloadedFilePath}\" \"{exePath}\" \"{exeName}\"");

        // 用 schtasks 创建一次性计划任务拉起 Updater，确保进程树完全孤立
        var taskName = "DeskOrganizerUpdate_" + DateTime.Now.Ticks;
        var taskCmd = $"\"{updaterPath}\" \"{downloadedFilePath}\" \"{exePath}\" \"{exeName}\"";

        // 创建计划任务
        var createPsi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = $"/create /tn \"{taskName}\" /tr \"{taskCmd}\" /sc once /st 23:59 /rl highest /f",
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
            UseShellExecute = false
        };
        var createProc = System.Diagnostics.Process.Start(createPsi);
        createProc?.WaitForExit(5000);
        App.Log($"[UpdateService] Scheduled task created: {taskName}");

        // 立即运行计划任务
        var runPsi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = $"/run /tn \"{taskName}\"",
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
            UseShellExecute = false
        };
        var runProc = System.Diagnostics.Process.Start(runPsi);
        runProc?.WaitForExit(5000);
        App.Log($"[UpdateService] Scheduled task started: {taskName}");

        // 延迟删除计划任务
        var delPsi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = $"/delete /tn \"{taskName}\" /f",
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
            UseShellExecute = false
        };
        System.Diagnostics.Process.Start(delPsi);
    }

    /// <summary>回退方案：使用 PowerShell 脚本更新（当 updater.exe 不存在时）。</summary>
    private static void ApplyUpdateWithPowerShell(string downloadedFilePath, string exePath, string exeName)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), "DeskOrganizerUpdate.ps1");
        var logPath = Path.Combine(Path.GetTempPath(), "DeskOrganizerUpdate.log");
        var procName = Path.GetFileNameWithoutExtension(exeName);

        var script = $@"
$ErrorActionPreference = 'Stop'
$log = '{logPath}'
function Log($msg) {{ Add-Content -Path $log -Value ""[$(Get-Date -Format 'HH:mm:ss.fff')] $msg"" }}
Log '=== PowerShell update started ==='
Log 'Waiting for process exit...'
for ($i = 0; $i -lt 30; $i++) {{
    $procs = Get-Process -Name '{procName}' -ErrorAction SilentlyContinue
    if ($procs.Count -eq 0) {{ Log ""Process exited after $i seconds""; break }}
    $procs | ForEach-Object {{ $_.Dispose() }}
    Start-Sleep -Seconds 1
}}
try {{ Get-Process -Name '{procName}' -ErrorAction SilentlyContinue | Stop-Process -Force }} catch {{}}
Start-Sleep -Seconds 2
$replaced = $false
for ($i = 0; $i -lt 20; $i++) {{
    try {{
        if (Test-Path '{exePath}') {{ Remove-Item '{exePath}' -Force }}
        Move-Item '{downloadedFilePath}' '{exePath}' -Force
        $replaced = $true; Log ""Replaced on attempt $($i + 1)""; break
    }} catch {{ Log ""Attempt $($i + 1) failed: $_""; Start-Sleep -Seconds 2 }}
}}
if (-not $replaced) {{ try {{ Copy-Item '{downloadedFilePath}' '{exePath}' -Force; Log 'Copied as fallback' }} catch {{}} }}
if (Test-Path '{exePath}') {{ Start-Process '{exePath}'; Log 'Program restarted' }}
try {{ if (Test-Path '{downloadedFilePath}') {{ Remove-Item '{downloadedFilePath}' -Force }} }} catch {{}}
try {{ Remove-Item $MyInvocation.MyCommand.Path -Force }} catch {{}}
Log '=== Update finished ==='
";
        File.WriteAllText(scriptPath, script, System.Text.Encoding.UTF8);

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
            UseShellExecute = true
        };
        System.Diagnostics.Process.Start(psi);
        App.Log("[UpdateService] PowerShell fallback started");
    }
}
