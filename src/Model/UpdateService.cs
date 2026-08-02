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

            // 查找自包含版本 zip 资产
            var asset = release.Assets?.FirstOrDefault(a =>
                a.Name.Contains("DeskOrganizer", StringComparison.OrdinalIgnoreCase) &&
                (a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                 a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)));

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
    /// 启动独立更新程序，然后退出当前程序。
    /// 更新程序会：等待主程序退出 -> 替换 exe -> 重启程序。
    /// </summary>
    public static void ApplyUpdate(string downloadedFilePath, string targetDir)
    {
        var exeName = string.IsNullOrEmpty(Environment.ProcessPath)
            ? "DeskOrganizer_v2.exe"
            : Path.GetFileName(Environment.ProcessPath);
        var exePath = Path.Combine(targetDir, exeName);

        // 更新程序路径（与主程序同目录）
        var updaterPath = Path.Combine(targetDir, "DeskOrganizerUpdater.exe");

        // 如果更新程序不存在，尝试从临时目录或下载目录查找
        if (!File.Exists(updaterPath))
        {
            // 从下载的文件中提取（如果是 zip）
            // 或者使用内嵌的更新程序
            var tempUpdater = Path.Combine(Path.GetTempPath(), "DeskOrganizerUpdater.exe");

            // 尝试从当前程序目录复制更新程序
            var currentDir = Path.GetDirectoryName(Environment.ProcessPath) ?? targetDir;
            var sourceUpdater = Path.Combine(currentDir, "DeskOrganizerUpdater.exe");
            if (File.Exists(sourceUpdater))
            {
                File.Copy(sourceUpdater, tempUpdater, true);
                updaterPath = tempUpdater;
            }
            else
            {
                // 回退到 BAT 脚本方案
                ApplyUpdateWithBat(downloadedFilePath, targetDir, exeName, exePath);
                return;
            }
        }

        App.Log($"[UpdateService] Starting updater: {updaterPath}");
        App.Log($"[UpdateService] Args: \"{downloadedFilePath}\" \"{exePath}\" \"{exeName}\"");

        // 启动更新程序（UseShellExecute=true 让它成为独立进程）
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = updaterPath,
            Arguments = $"\"{downloadedFilePath}\" \"{exePath}\" \"{exeName}\"",
            UseShellExecute = true,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            CreateNoWindow = true
        };
        var proc = System.Diagnostics.Process.Start(psi);
        App.Log($"[UpdateService] Updater started, PID={proc?.Id}");
    }

    /// <summary>回退方案：使用 BAT 脚本更新（当 updater.exe 不存在时）。</summary>
    private static void ApplyUpdateWithBat(string downloadedFilePath, string targetDir, string exeName, string exePath)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), "DeskOrganizerUpdate.bat");
        // BAT 脚本中使用 ping 替代 timeout（timeout 在非交互式会话中无法工作）
        string script;

        if (downloadedFilePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            script = $@"@echo off
echo Updating DeskOrganizer...
ping -n 3 127.0.0.1 >nul

taskkill /im ""{exeName}"" /f >nul 2>&1
ping -n 2 127.0.0.1 >nul

powershell -Command ""Expand-Archive -Path '{downloadedFilePath}' -DestinationPath '{targetDir}' -Force""

start """" ""{exePath}""
del ""{downloadedFilePath}"" >nul 2>&1
del ""%~f0"" >nul 2>&1
";
        }
        else
        {
            script = $@"@echo off
echo Updating DeskOrganizer...
ping -n 3 127.0.0.1 >nul

taskkill /im ""{exeName}"" /f >nul 2>&1
ping -n 3 127.0.0.1 >nul

set ""del_retries=0""
:del_retry
del ""{exePath}"" >nul 2>&1
if exist ""{exePath}"" (
    set /a ""del_retries+=1""
    if %del_retries% lss 15 (
        ping -n 3 127.0.0.1 >nul
        goto del_retry
    )
    copy /y ""{downloadedFilePath}"" ""{exePath}"" >nul 2>&1
    if not errorlevel 1 goto restart
    start """" ""{exePath}""
    del ""{downloadedFilePath}"" >nul 2>&1
    del ""%~f0"" >nul 2>&1
    exit /b 1
)

move /y ""{downloadedFilePath}"" ""{exePath}"" >nul 2>&1
if errorlevel 1 (
    copy /y ""{downloadedFilePath}"" ""{exePath}"" >nul 2>&1
)

:restart
start """" ""{exePath}""
del ""{downloadedFilePath}"" >nul 2>&1
del ""%~f0"" >nul 2>&1
";
        }

        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var encoding = System.Text.Encoding.GetEncoding(0);
        File.WriteAllText(scriptPath, script, encoding);

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c start \"\" /b cmd /c \"{scriptPath}\"",
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
            UseShellExecute = true
        };
        System.Diagnostics.Process.Start(psi);
        App.Log($"[UpdateService] BAT fallback started");
    }
}
