using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DeskOrganizer;

public class SearchService
{
    public static SearchService Instance { get; } = new();

    private readonly ConcurrentDictionary<string, FileIndexItem> _index = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _indexCts;

    public const int DefaultMaxIndexedFiles = 200000;
    public const int MaxSearchResults = 50;
    private const int MaxDepth = 8;

    public int MaxIndexedFiles { get; set; } = DefaultMaxIndexedFiles;

    public int IndexedCount => _index.Count;

    public event EventHandler<IndexProgressEventArgs>? IndexProgressChanged;

    private SearchService() { }

    // ---- Build Index ----

    public Task BuildIndexAsync(string[] paths, CancellationToken ct = default)
    {
        return Task.Run(() => BuildIndexInternal(paths, ct), ct);
    }

    private void BuildIndexInternal(string[] paths, CancellationToken ct)
    {
        StopIndexing();

        _indexCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            // Clear existing index
            _index.Clear();

            var totalCount = 0;
            var filesEnumerated = 0;

            // First pass: count total files (with limit)
            foreach (var path in paths)
            {
                if (!Directory.Exists(path)) continue;
                filesEnumerated += CountFiles(path, MaxIndexedFiles - totalCount);
            }

            RaiseProgress(0, filesEnumerated, IndexStatus.Indexing);

            // Second pass: index files
            var indexedCount = 0;
            foreach (var path in paths)
            {
                if (!Directory.Exists(path)) continue;
                indexedCount += IndexDirectory(path, _indexCts.Token, ref totalCount, MaxIndexedFiles);
            }

            RaiseProgress(indexedCount, indexedCount, IndexStatus.Complete);
        }
        catch (OperationCanceledException)
        {
            RaiseProgress(_index.Count, _index.Count, IndexStatus.Stopped);
        }
        catch (Exception)
        {
            RaiseProgress(_index.Count, _index.Count, IndexStatus.Error);
        }
    }

    private int IndexDirectory(string path, CancellationToken ct, ref int currentTotal, int maxFiles)
    {
        ct.ThrowIfCancellationRequested();
        if (currentTotal >= maxFiles)
            return 0;

        var count = 0;

        try
        {
            // Index files in this directory
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly))
            {
                ct.ThrowIfCancellationRequested();
                if (currentTotal >= maxFiles)
                    break;

                try
                {
                    IndexFile(file);
                    currentTotal++;
                    count++;

                    if (currentTotal % 500 == 0)
                    {
                        RaiseProgress(currentTotal, MaxIndexedFiles, IndexStatus.Indexing);
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    // Skip files we cannot access
                }
            }

            // Recurse into subdirectories
            foreach (var dir in Directory.EnumerateDirectories(path))
            {
                ct.ThrowIfCancellationRequested();
                if (currentTotal >= maxFiles)
                    break;

                try
                {
                    count += IndexDirectory(dir, ct, ref currentTotal, maxFiles);
                }
                catch (UnauthorizedAccessException)
                {
                    // Skip directories we cannot access
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Skip directories we cannot access
        }

        return count;
    }

    private void IndexFile(string filePath)
    {
        var info = new FileInfo(filePath);
        var item = new FileIndexItem
        {
            FilePath = filePath,
            FileName = info.Name,
            Extension = info.Extension.ToLowerInvariant(),
            Size = info.Length,
            ModifiedDate = info.LastWriteTime
        };
        _index[item.FilePath] = item;
    }

    private static int CountFiles(string path, int maxCount)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories)
                .Take(maxCount + 1)
                .Count(e => File.Exists(e));
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    // ---- Search ----

    public List<SearchResult> Search(string keyword, int maxResults = 50)
    {
        if (string.IsNullOrWhiteSpace(keyword) || _index.IsEmpty)
        {
            return [];
        }

        keyword = keyword.Trim();
        var results = new List<SearchResult>();

        foreach (var item in _index.Values)
        {
            if (results.Count >= maxResults) break;

            var result = Match(item, keyword);
            if (result != null)
            {
                results.Add(result);
            }
        }

        // Sort by score descending, then by modified date descending
        results.Sort((a, b) =>
        {
            var cmp = b.Score.CompareTo(a.Score);
            if (cmp != 0) return cmp;
            return b.ModifiedDate.CompareTo(a.ModifiedDate);
        });

        return results.Take(maxResults).ToList();
    }

    private static SearchResult? Match(FileIndexItem item, string keyword)
    {
        var fileName = item.FileName;
        var filePath = item.FilePath;
        var extension = item.Extension;

        // Exact match (highest score)
        if (string.Equals(fileName, keyword, StringComparison.OrdinalIgnoreCase))
        {
            return CreateResult(item, MatchType.Exact, 100);
        }

        // Extension match
        var kw = keyword.TrimStart('.');
        if (keyword.StartsWith('.') && string.Equals(extension.TrimStart('.'), kw, StringComparison.OrdinalIgnoreCase))
        {
            return CreateResult(item, MatchType.Extension, 80);
        }

        // Prefix match
        if (fileName.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
        {
            var score = 70 + (30.0 * keyword.Length / fileName.Length);
            return CreateResult(item, MatchType.Prefix, score);
        }

        // Contains match
        if (fileName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
        {
            var idx = fileName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
            var score = 40 + (30.0 * keyword.Length / fileName.Length);
            // Bonus for match near start of filename
            if (idx < 5) score += 10;
            return CreateResult(item, MatchType.Contains, score);
        }

        // Path contains match (lower priority)
        if (filePath.Contains(keyword, StringComparison.OrdinalIgnoreCase))
        {
            return CreateResult(item, MatchType.Contains, 25);
        }

        return null;
    }

    private static SearchResult CreateResult(FileIndexItem item, MatchType matchType, double score)
    {
        return new SearchResult
        {
            FilePath = item.FilePath,
            FileName = item.FileName,
            Extension = item.Extension,
            Size = item.Size,
            ModifiedDate = item.ModifiedDate,
            Score = score,
            MatchType = matchType
        };
    }

    // ---- Control ----

    public void StopIndexing()
    {
        _indexCts?.Cancel();
        _indexCts?.Dispose();
        _indexCts = null;
    }

    public void ClearIndex()
    {
        _index.Clear();
        RaiseProgress(0, 0, IndexStatus.Idle);
    }

    private void RaiseProgress(int current, int total, IndexStatus status)
    {
        try
        {
            IndexProgressChanged?.Invoke(this, new IndexProgressEventArgs(current, total, status));
        }
        catch
        {
            // Prevent handler exceptions from crashing the indexer
        }
    }
}
