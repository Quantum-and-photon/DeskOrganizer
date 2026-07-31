using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeskOrganizer.NoFences.Model;

namespace DeskOrganizer.NoFences.Util;

/// <summary>
/// 异步缩略图生成器，带并发缓存和信号量控制。
/// </summary>
public class ThumbnailProvider : IDisposable
{
    private readonly ConcurrentDictionary<string, Bitmap> _cache = new();
    private readonly SemaphoreSlim _semaphore = new(Environment.ProcessorCount, Environment.ProcessorCount * 2);
    private readonly int _iconSize;
    private bool _disposed;

    /// <summary>
    /// 当缩略图加载完成时触发（参数：FilePath, Bitmap）。
    /// </summary>
    public event Action<string, Bitmap?>? ThumbnailLoaded;

    /// <summary>
    /// 创建缩略图提供器。
    /// </summary>
    /// <param name="iconSize">图标尺寸（像素）。</param>
    public ThumbnailProvider(int iconSize = 48)
    {
        _iconSize = iconSize;
    }

    /// <summary>
    /// 异步获取缩略图。如果缓存中有则直接返回，否则排队异步加载。
    /// </summary>
    public async Task<Bitmap?> GetThumbnailAsync(FenceEntry entry)
    {
        if (_disposed) return null;

        if (string.IsNullOrWhiteSpace(entry.FilePath))
            return null;

        // 检查缓存
        if (_cache.TryGetValue(entry.FilePath, out var cached))
            return cached;

        // 等待信号量，限制并发数
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return null;

            // 再次检查缓存（可能在等待期间已被其他线程加载）
            if (_cache.TryGetValue(entry.FilePath, out cached))
                return cached;

            // 在后台线程提取图标
            var bitmap = await Task.Run(() => ExtractThumbnail(entry)).ConfigureAwait(false);

            if (bitmap != null)
            {
                _cache.TryAdd(entry.FilePath, bitmap);
            }

            // 通知 UI 层刷新
            ThumbnailLoaded?.Invoke(entry.FilePath, bitmap);

            return bitmap;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// 批量异步加载缩略图。
    /// </summary>
    public async Task LoadAllAsync(IEnumerable<FenceEntry> entries)
    {
        var tasks = entries.Where(e => !string.IsNullOrWhiteSpace(e.FilePath) && !e.ThumbnailRequested)
            .Select(e =>
            {
                e.ThumbnailRequested = true;
                return GetThumbnailAsync(e);
            });

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>
    /// 从缓存中清除指定路径的缩略图。
    /// </summary>
    public void Invalidate(string filePath)
    {
        _cache.TryRemove(filePath, out var bmp);
        bmp?.Dispose();
    }

    /// <summary>
    /// 清除所有缓存。
    /// </summary>
    public void ClearCache()
    {
        foreach (var kvp in _cache)
        {
            kvp.Value.Dispose();
        }
        _cache.Clear();
    }

    private Bitmap? ExtractThumbnail(FenceEntry entry)
    {
        // SHGetFileInfo 和 ExtractAssociatedIcon 需要 STA 线程才能正确处理 .lnk 文件
        Bitmap? result = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = FenceEntry.ExtractIcon(entry.FilePath, _iconSize);
            }
            catch { }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(15000); // 最多等 15 秒
        return result;
    }

    public void Dispose()
    {
        _disposed = true;
        ClearCache();
        _semaphore.Dispose();
    }
}
