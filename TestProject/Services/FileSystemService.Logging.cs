using Microsoft.Extensions.Logging;

namespace TestProject.Services;

public sealed partial class FileSystemService
{
    [LoggerMessage(EventId = 5, Level = LogLevel.Debug,
        Message = "File browser search scan completed; DurationMs={DurationMs}; Inspected={Inspected}; Matches={Matches}; Partial={Partial}")]
    private static partial void SearchScanCompleted(ILogger logger, double durationMs, int inspected, int matches, bool partial);

    [LoggerMessage(EventId = 6, Level = LogLevel.Debug,
        Message = "File browser directory scan completed; DurationMs={DurationMs}; Inspected={Inspected}; Returned={Returned}; Partial={Partial}")]
    private static partial void DirectoryScanCompleted(ILogger logger, double durationMs, int inspected, int returned, bool partial);

    [LoggerMessage(EventId = 7, Level = LogLevel.Debug,
        Message = "File browser response prepared; DurationMs={DurationMs}; Entries={Entries}; FileCount={FileCount}; FolderCount={FolderCount}")]
    private static partial void ResponsePrepared(ILogger logger, double durationMs, int entries, int fileCount, int folderCount);

    [LoggerMessage(EventId = 8, Level = LogLevel.Debug,
        Message = "File browser snapshot not cached; EstimatedBytes={EstimatedBytes}; CacheMaxBytes={CacheMaxBytes}")]
    private static partial void SnapshotNotCached(ILogger logger, long estimatedBytes, long cacheMaxBytes);

    [LoggerMessage(EventId = 9, Level = LogLevel.Debug,
        Message = "File browser snapshot cached; EstimatedBytes={EstimatedBytes}; CacheBytes={CacheBytes}; CacheViews={CacheViews}; EvictedViews={EvictedViews}")]
    private static partial void SnapshotCached(ILogger logger, long estimatedBytes, long cacheBytes, int cacheViews, int evictedViews);
}
