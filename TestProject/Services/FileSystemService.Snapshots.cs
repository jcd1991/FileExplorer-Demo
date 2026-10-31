using TestProject.Models;

namespace TestProject.Services;

public sealed partial class FileSystemService
{
    // A small, byte-accounted set of short-lived views avoids rescans without unbounded retention.
    private readonly object _snapshotLock = new();
    private readonly Dictionary<string, (string Key, DateTimeOffset Expires, long EstimatedBytes, FileListResponse Result)> _snapshots = new();
    private long _snapshotCacheBytes;

    private static string SnapshotKey(string directory, string query, string? sort, string? direction) =>
        System.Text.Json.JsonSerializer.Serialize(new[] { directory, query, sort, direction });

    private void StoreSnapshot(string key, FileListResponse result)
    {
        var estimatedBytes = EstimateSnapshotBytes(result);
        lock (_snapshotLock)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var id in _snapshots.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToArray())
                RemoveSnapshot(id);
            if (estimatedBytes > _snapshotCacheMaxBytes)
            {
                _logger.LogDebug("File browser snapshot not cached; EstimatedBytes={EstimatedBytes}; CacheMaxBytes={CacheMaxBytes}",
                    estimatedBytes, _snapshotCacheMaxBytes);
                return;
            }
            var evicted = 0;
            while (_snapshots.Count >= _snapshotCacheMaxViews || _snapshotCacheBytes > _snapshotCacheMaxBytes - estimatedBytes)
            {
                RemoveSnapshot(_snapshots.MinBy(pair => pair.Value.Expires).Key);
                evicted++;
            }
            _snapshots[result.SnapshotId] = (key, now.AddMinutes(2), estimatedBytes, result);
            _snapshotCacheBytes += estimatedBytes;
            _logger.LogDebug("File browser snapshot cached; EstimatedBytes={EstimatedBytes}; CacheBytes={CacheBytes}; CacheViews={CacheViews}; EvictedViews={EvictedViews}",
                estimatedBytes, _snapshotCacheBytes, _snapshots.Count, evicted);
        }
    }

    private static long EstimateSnapshotBytes(FileListResponse result)
    {
        // Approximate retained managed size: row/list references, FileEntry objects,
        // and UTF-16 path/name storage. This is a cache accounting estimate, not RSS.
        long bytes = 256;
        foreach (var entry in result.Entries)
            bytes += 128 + 2L * (entry.Path.Length + entry.Name.Length);
        return bytes;
    }

    private void RemoveSnapshot(string id)
    {
        if (_snapshots.Remove(id, out var removed)) _snapshotCacheBytes -= removed.EstimatedBytes;
    }

    private FileListResponse ReadSnapshot(string id, string key, int page, int pageSize)
    {
        lock (_snapshotLock)
        {
            if (!_snapshots.TryGetValue(id, out var view) || view.Key != key || view.Expires <= DateTimeOffset.UtcNow)
                throw new FileBrowserException(409, "View expired", "Refresh the directory to create a new view.");
            return PageSnapshot(view.Result, page, pageSize);
        }
    }

    private static FileListResponse PageSnapshot(FileListResponse result, int page, int pageSize)
    {
        pageSize = Math.Clamp(pageSize <= 0 ? DefaultPageSize : pageSize, 1, MaxPageSize);
        var totalPages = Math.Max(1, (int)Math.Ceiling(result.Entries.Count / (double)pageSize));
        page = Math.Clamp(page, 1, totalPages);
        return result with
        {
            Entries = result.Entries.Skip((page - 1) * pageSize).Take(pageSize).ToArray(),
            Page = page,
            PageSize = pageSize,
            TotalPages = totalPages
        };
    }
}
