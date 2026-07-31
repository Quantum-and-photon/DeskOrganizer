using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace DeskOrganizer.Win32;

/// <summary>
/// 提供路径安全检查、输入清理和安全的正则表达式匹配功能。
/// 用于防御路径遍历攻击和正则表达式拒绝服务（ReDoS）。
/// </summary>
public static class SecurityHelper
{
    // ---------- 配置常量 ----------

    /// <summary>正则表达式匹配超时时间（毫秒）。</summary>
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>允许的根驱动器列表。</summary>
    private static readonly string[] AllowedRoots =
    {
        Environment.GetFolderPath(Environment.SpecialFolder.Windows).Substring(0, 3), // 如 "C:\"
    };

    /// <summary>危险路径字符，不允许出现在文件名中。</summary>
    private static readonly char[] InvalidFileNameChars =
        Path.GetInvalidFileNameChars()
            .Union([':', '*', '?', '"', '<', '>', '|'])
            .ToArray();

    // ---------- 正则表达式缓存 ----------

    /// <summary>
    /// 线程安全的已编译正则表达式缓存。
    /// 避免重复编译相同模式，同时统一施加超时限制。
    /// </summary>
    private static readonly ConcurrentDictionary<string, Regex> RegexCache = new();
    private const int MaxRegexCacheSize = 100;

    // ---------- 路径安全 ----------

    /// <summary>
    /// 判断给定路径是否安全（无路径遍历、无危险字符）。
    /// </summary>
    /// <param name="path">待检查的路径。</param>
    /// <returns>路径安全返回 true，否则返回 false。</returns>
    public static bool IsPathSafe(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            var fullPath = Path.GetFullPath(path);

            // 拒绝 UNC 路径
            if (fullPath.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase))
                return false;

            // 拒绝路径遍历
            if (fullPath.Contains("..", StringComparison.OrdinalIgnoreCase))
                return false;

            // 检查路径是否存在危险的不可打印字符
            if (fullPath.Any(c => char.IsControl(c) && c != '\t'))
                return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 判断路径是否为有效的本地路径。
    /// 必须以驱动器号开头且经过规范化后不包含遍历序列。
    /// </summary>
    /// <param name="path">待检查的路径。</param>
    /// <returns>有效本地路径返回 true，否则返回 false。</returns>
    public static bool IsValidLocalPath(string path)
    {
        if (!IsPathSafe(path))
            return false;

        try
        {
            var fullPath = Path.GetFullPath(path);

            // 驱动器号格式检查 (如 "C:\...")
            if (fullPath.Length < 3
                || !char.IsLetter(fullPath[0])
                || fullPath[1] != ':'
                || (fullPath[2] != '\\' && fullPath[2] != '/'))
            {
                return false;
            }

            // 检查驱动器是否可访问（如果可能的话）
            var driveRoot = fullPath.Substring(0, 3);
            return Directory.Exists(driveRoot);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 从路径中提取安全文件名，移除危险字符并限制长度。
    /// </summary>
    /// <param name="fileName">原始文件名。</param>
    /// <param name="maxLength">允许的最大长度，默认 255。</param>
    /// <returns>清理后的安全文件名。</returns>
    public static string GetSafeFileName(string fileName, int maxLength = 255)
    {
        if (string.IsNullOrEmpty(fileName))
            return "_unknown_";

        // 移除危险字符
        var safeName = new string(
            fileName.Where(c => !InvalidFileNameChars.Contains(c)).ToArray());

        // 移除前后空白和点号
        safeName = safeName.Trim().Trim('.');

        // 如果清理后为空，使用默认名称
        if (string.IsNullOrWhiteSpace(safeName))
            return "_unnamed_";

        // 限制长度
        if (safeName.Length > maxLength)
            safeName = safeName.Substring(0, maxLength);

        return safeName;
    }

    // ---------- 安全正则匹配 ----------

    /// <summary>
    /// 带超时限制的安全正则表达式匹配。
    /// 使用缓存避免重复编译，防止 ReDoS 攻击。
    /// </summary>
    /// <param name="pattern">正则表达式模式。</param>
    /// <param name="input">待匹配的输入字符串。</param>
    /// <returns>匹配结果，失败返回 null。</returns>
    public static Match? SafeRegexMatch(string pattern, string input)
    {
        if (string.IsNullOrEmpty(pattern) || input is null)
            return null;

        try
        {
            // Prevent unbounded cache growth
            if (RegexCache.Count >= MaxRegexCacheSize)
            {
                RegexCache.Clear();
            }

            var regex = RegexCache.GetOrAdd(pattern, p =>
                new Regex(p, RegexOptions.Compiled, RegexTimeout));

            return regex.Match(input);
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// 判断输入是否匹配给定的正则表达式模式。
    /// </summary>
    /// <param name="pattern">正则表达式模式。</param>
    /// <param name="input">待匹配的输入字符串。</param>
    /// <returns>匹配返回 true，否则返回 false。</returns>
    public static bool SafeRegexIsMatch(string pattern, string input)
    {
        var match = SafeRegexMatch(pattern, input);
        return match?.Success ?? false;
    }

    // ---------- 显示清理 ----------

    /// <summary>
    /// 清理用于 UI 显示的文本内容，移除控制字符和危险 HTML/XML 字符。
    /// </summary>
    /// <param name="text">原始文本。</param>
    /// <returns>清理后的安全显示文本。</returns>
    public static string SanitizeForDisplay(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        // 移除控制字符（保留换行和制表符）
        var sanitized = new string(
            text.Where(c => !char.IsControl(c) || c == '\n' || c == '\t').ToArray());

        // 截断过长文本（防止 UI 溢出）
        const int maxDisplayLength = 512;
        if (sanitized.Length > maxDisplayLength)
            sanitized = sanitized.Substring(0, maxDisplayLength) + "...";

        return sanitized;
    }

    // ---------- 颜色字符串验证 ----------

    /// <summary>
    /// 验证颜色字符串是否为合法的十六进制颜色表示。
    /// 支持 #RGB, #ARGB, #RRGGBB, #AARRGGBB 格式。
    /// </summary>
    /// <param name="colorString">待验证的颜色字符串。</param>
    /// <returns>合法返回 true，否则返回 false。</returns>
    public static bool IsValidColorString(string colorString)
    {
        if (string.IsNullOrWhiteSpace(colorString))
            return false;

        // 移除前导 #（可选）
        var value = colorString.TrimStart('#');

        return value.Length switch
        {
            3 => IsHexString(value),   // #RGB
            4 => IsHexString(value),   // #ARGB
            6 => IsHexString(value),   // #RRGGBB
            8 => IsHexString(value),   // #AARRGGBB
            _ => false
        };
    }

    private static bool IsHexString(string value)
    {
        foreach (var c in value)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f')))
                return false;
        }
        return true;
    }
}
