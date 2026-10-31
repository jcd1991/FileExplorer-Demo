namespace TestProject.Models;

public sealed record FileEntry(
    string Path,
    string Name,
    string Kind,
    long? SizeBytes,
    DateTimeOffset ModifiedUtc);

public sealed record FileSummary(int FileCount, int FolderCount, long TotalFileBytes);

public sealed record FileWarning(string Code, int Count);

public sealed record FileListResponse(
    IReadOnlyList<FileEntry> Entries,
    int Page,
    int PageSize,
    int TotalPages,
    FileSummary Summary,
    bool Partial,
    IReadOnlyList<FileWarning> Warnings,
    string SnapshotId = "");

public sealed record UploadResult(FileEntry Entry);

public sealed record DiagnosticsResponse(bool DiagnosticsEnabled);

public sealed record FileChangeRequest(string Source, string? Destination);
