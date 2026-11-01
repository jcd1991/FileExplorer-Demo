using System.Diagnostics;
using System.IO.Enumeration;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using TestProject.Models;

namespace TestProject.Services;

public sealed partial class FileSystemService : IDisposable
{
    public const long MaxUploadBytes = 100L * 1024 * 1024;
    public const int MaxPageSize = 500;
    private const int DefaultPageSize = 100;
    private const int MaxVisitedEntries = 100_000;
    private const int MaxPendingDirectories = 100_000;
    private const int MaxSearchSeconds = 5;
    private const string TemporaryPrefix = ".filebrowser-upload-";
    // Conservatively reject case aliases on platforms commonly using insensitive filesystems.
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private readonly string _root;
    private readonly string _rootPrefix;
    private readonly ILogger<FileSystemService> _logger;
    private readonly int _uploadCopyBufferBytes;
    private readonly int _uploadStreamBufferBytes;
    private readonly SemaphoreSlim? _uploadGate;
    private readonly int _snapshotCacheMaxViews;
    private readonly long _snapshotCacheMaxBytes;
    private static readonly Action<ILogger, object?, Exception?> LogCleanupFailure =
        LoggerMessage.Define<object?>(LogLevel.Warning, new EventId(4, "UploadCleanupFailed"),
            "Temporary upload cleanup failed; CorrelationId={CorrelationId}");

    public FileSystemService(string root, ILogger<FileSystemService>? logger = null,
        int uploadCopyBufferBytes = 64 * 1024, int uploadStreamBufferBytes = 4096, int maxConcurrentUploads = 0,
        int snapshotCacheMaxViews = 16, long snapshotCacheMaxBytes = 64L * 1024 * 1024)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<FileSystemService>.Instance;
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _rootPrefix = Path.EndsInDirectorySeparator(_root) ? _root : _root + Path.DirectorySeparatorChar;
        if (uploadCopyBufferBytes is < 4096 or > 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(uploadCopyBufferBytes));
        if (uploadStreamBufferBytes != 1 && uploadStreamBufferBytes is < 4096 or > 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(uploadStreamBufferBytes));
        if (maxConcurrentUploads is < 0 or > 64)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentUploads));
        if (snapshotCacheMaxViews is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(snapshotCacheMaxViews));
        if (snapshotCacheMaxBytes is < 1024 or > 1024L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(snapshotCacheMaxBytes));
        _uploadCopyBufferBytes = uploadCopyBufferBytes;
        _uploadStreamBufferBytes = uploadStreamBufferBytes;
        _uploadGate = maxConcurrentUploads == 0 ? null : new SemaphoreSlim(maxConcurrentUploads, maxConcurrentUploads);
        _snapshotCacheMaxViews = snapshotCacheMaxViews;
        _snapshotCacheMaxBytes = snapshotCacheMaxBytes;
        if (!Directory.Exists(_root))
        {
            throw new InvalidOperationException("The configured file root does not exist.");
        }
        if (new DirectoryInfo(_root).LinkTarget is not null)
        {
            throw new InvalidOperationException("The configured file root cannot be a symbolic link.");
        }
    }

    public FileListResponse Browse(string? relativePath, string? sort, string? direction, int page, int pageSize, CancellationToken cancellationToken, string? snapshot = null)
    {
        var directory = ResolvePath(relativePath, requireDirectory: true);
        var key = SnapshotKey(directory, "", sort, direction);
        if (snapshot is not null) return ReadSnapshot(snapshot, key, page, pageSize);
        var (entries, warningCounts, partial) = ReadDirectory(directory, cancellationToken);
        return CreateResponse(entries, warningCounts, partial, sort, direction, page, pageSize, key);
    }

    public FileListResponse Search(string? relativePath, string query, string? sort, string? direction, int page, int pageSize, CancellationToken cancellationToken, string? snapshot = null)
    {
        var start = Stopwatch.GetTimestamp();
        var directory = ResolvePath(relativePath, requireDirectory: true);
        var key = SnapshotKey(directory, query, sort, direction);
        if (snapshot is not null) return ReadSnapshot(snapshot, key, page, pageSize);
        var state = new SearchState(directory);
        while (state.Pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SearchLimitReached(state, start)) break;
            VisitSearchDirectory(state.Pending.Dequeue(), query, state, start, cancellationToken);
            if (state.LimitReached) break;
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            var durationMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            SearchScanCompleted(_logger, durationMs, state.Inspected, state.Entries.Count, state.Partial);
        }

        return CreateResponse(state.Entries, state.Warnings, state.Partial, sort, direction, page, pageSize, key);
    }

    private void VisitSearchDirectory(string current, string query, SearchState state, long started, CancellationToken cancellationToken)
    {
        try
        {
            EnsureNoLinks(current);
            foreach (var child in EnumerateEntries(current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (SearchLimitReached(state, started)) break;
                state.Inspected++;
                if (!TryCreateEntry(child, state.Warnings, out var entry, out var isDirectory)) continue;
                if (entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) state.Entries.Add(entry);
                if (!isDirectory) continue;
                if (state.Pending.Count >= MaxPendingDirectories)
                {
                    MarkSearchLimit(state);
                    break;
                }
                state.Pending.Enqueue(child.FullPath);
            }
        }
        catch (UnauthorizedAccessException)
        {
            AddWarning(state.Warnings, "inaccessible-directory");
            state.Partial = true;
        }
        catch (IOException)
        {
            AddWarning(state.Warnings, "unavailable-directory");
            state.Partial = true;
        }
    }

    private static bool SearchLimitReached(SearchState state, long started)
    {
        if (state.Inspected < MaxVisitedEntries && Stopwatch.GetElapsedTime(started).TotalSeconds < MaxSearchSeconds) return false;
        MarkSearchLimit(state);
        return true;
    }

    private static void MarkSearchLimit(SearchState state)
    {
        state.Partial = true;
        state.LimitReached = true;
        if (!state.Warnings.ContainsKey("search-limit")) AddWarning(state.Warnings, "search-limit");
    }

    public string ResolveDownload(string? relativePath)
    {
        var fullPath = ResolvePath(relativePath, requireDirectory: false);
        if (!File.Exists(fullPath))
        {
            throw new FileBrowserException(StatusCodes.Status404NotFound, "File not found", "The requested file does not exist.");
        }
        if ((File.GetAttributes(fullPath) & FileAttributes.Directory) != 0)
        {
            throw new FileBrowserException(StatusCodes.Status400BadRequest, "Invalid file", "A directory cannot be downloaded as a file.");
        }
        FileSystemMetadata.RequireRegular(FileSystemMetadata.Read(fullPath));
        return fullPath;
    }

    public async Task<UploadResult> UploadAsync(string? relativePath, HttpRequest request, CancellationToken cancellationToken)
    {
        if (_uploadGate is null) return await UploadCoreAsync(relativePath, request, cancellationToken);
        await _uploadGate.WaitAsync(cancellationToken);
        try { return await UploadCoreAsync(relativePath, request, cancellationToken); }
        finally { _uploadGate.Release(); }
    }

    private async Task<UploadResult> UploadCoreAsync(string? relativePath, HttpRequest request, CancellationToken cancellationToken)
    {
        var directory = ResolvePath(relativePath, requireDirectory: true);
        var reader = CreateUploadReader(request);
        string? tempPath = null;
        try
        {
            return await ReadUploadAsync(directory, reader, path => tempPath = path, cancellationToken);
        }
        catch (InvalidDataException)
        {
            if (request.ContentLength is > MaxUploadBytes + 1_048_576)
            {
                throw UploadTooLarge();
            }
            throw new FileBrowserException(StatusCodes.Status400BadRequest, "Invalid multipart request", "The upload request could not be read.");
        }
        catch (IOException) when (tempPath is not null && new FileInfo(tempPath).Length > MaxUploadBytes)
        {
            throw UploadTooLarge();
        }
        finally
        {
            DeleteTemporaryFile(tempPath, request.HttpContext.Items["CorrelationId"]);
        }
    }

    private static MultipartReader CreateUploadReader(HttpRequest request)
    {
        if (request.ContentLength is > MaxUploadBytes + 1_048_576)
        {
            throw UploadTooLarge();
        }

        var maxBodySize = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (maxBodySize is { IsReadOnly: false })
        {
            maxBodySize.MaxRequestBodySize = MaxUploadBytes + 1_048_576;
        }

        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType) ||
            !string.Equals(mediaType.MediaType.Value, "multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            throw new FileBrowserException(StatusCodes.Status415UnsupportedMediaType, "Unsupported media type", "Upload must use multipart/form-data.");
        }

        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary) || boundary.Length > 128)
        {
            throw new FileBrowserException(StatusCodes.Status400BadRequest, "Invalid multipart request", "A valid multipart boundary is required.");
        }
        return new MultipartReader(boundary, new UploadReadStream(request.Body, MaxUploadBytes + 1_048_576))
        {
            BodyLengthLimit = MaxUploadBytes + 1_048_576
        };
    }

    private async Task<UploadResult> ReadUploadAsync(string directory, MultipartReader reader, Action<string?> registerTempPath, CancellationToken cancellationToken)
    {
        var section = await reader.ReadNextSectionAsync(cancellationToken);
        if (section is not null)
        {
            if (!TryGetUploadFilename(section, out var filename))
            {
                throw new FileBrowserException(StatusCodes.Status400BadRequest, "Invalid upload", "Include exactly one multipart field named 'file'.");
            }

            ValidateFilename(filename);
            var destination = Path.Combine(directory, filename);
            EnsureDestinationAvailable(destination);
            var tempPath = Path.Combine(directory, $"{TemporaryPrefix}{Guid.NewGuid():N}.tmp");
            registerTempPath(tempPath);
            await CopyUploadSectionAsync(section.Body, tempPath, cancellationToken);
            await RejectAdditionalFilesAsync(reader, cancellationToken);
            MoveUploadedFile(tempPath, destination);
            registerTempPath(null);
            return new UploadResult(CreateFileEntry(destination));
        }
        throw new FileBrowserException(StatusCodes.Status400BadRequest, "File is required", "Include one multipart field named 'file'.");
    }

    private static async Task RejectAdditionalFilesAsync(MultipartReader reader, CancellationToken cancellationToken)
    {
        var section = await reader.ReadNextSectionAsync(cancellationToken);
        if (section is not null)
        {
            throw new FileBrowserException(StatusCodes.Status400BadRequest, "Invalid upload", "Upload exactly one multipart field per request.");
        }
    }

    private void EnsureDestinationAvailable(string destination)
    {
        EnsureNoLinks(destination);
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            throw FileAlreadyExists();
        }
    }

    private static void MoveUploadedFile(string tempPath, string destination)
    {
        try { File.Move(tempPath, destination); }
        catch (IOException) when (File.Exists(destination) || Directory.Exists(destination))
        {
            throw FileAlreadyExists();
        }
    }

    internal void DeleteTemporaryFile(string? tempPath, object? correlationId)
    {
        if (tempPath is null) return;
        try { File.Delete(tempPath); }
        catch (IOException) { LogCleanupFailure(_logger, correlationId, null); }
        catch (UnauthorizedAccessException) { LogCleanupFailure(_logger, correlationId, null); }
    }

    private static FileBrowserException UploadTooLarge() =>
        new(StatusCodes.Status413PayloadTooLarge, "Upload too large", "The upload exceeds the 100 MiB limit.");

    private static FileBrowserException FileAlreadyExists() =>
        new(StatusCodes.Status409Conflict, "File already exists", "Choose a different filename; existing files are never overwritten.");

    private string ResolvePath(string? relativePath, bool requireDirectory)
    {
        var value = relativePath ?? string.Empty;
        if (value.Contains('\\') || Path.IsPathRooted(value) || value.StartsWith('/'))
        {
            throw new FileBrowserException(StatusCodes.Status400BadRequest, "Invalid path", "Use a root-relative path with forward slashes.");
        }

        var segments = value.Split('/', StringSplitOptions.None);
        if (segments.Any(segment => segment is "." or ".." || segment.Contains(':') ||
            segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || segment.StartsWith(TemporaryPrefix, StringComparison.OrdinalIgnoreCase)))
        {
            throw new FileBrowserException(StatusCodes.Status400BadRequest, "Invalid path", "The path contains a forbidden segment.");
        }

        var fullPath = segments.Length == 1 && segments[0].Length == 0
            ? _root
            : Path.GetFullPath(Path.Combine(_root, Path.Combine(segments)));
        var comparison = PathComparison;
        if (!fullPath.Equals(_root, comparison) && !fullPath.StartsWith(_rootPrefix, comparison))
        {
            throw new FileBrowserException(StatusCodes.Status400BadRequest, "Invalid path", "The path must remain inside the configured root.");
        }

        EnsureNoLinks(fullPath);
        if (requireDirectory && !Directory.Exists(fullPath))
        {
            throw new FileBrowserException(StatusCodes.Status404NotFound, "Directory not found", "The requested directory does not exist.");
        }
        return fullPath;
    }

    private void EnsureNoLinks(string fullPath)
    {
        var relative = Path.GetRelativePath(_root, fullPath);
        if (relative == ".")
        {
            return;
        }

        var current = _root;
        foreach (var segment in relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            var fileInfo = new FileInfo(current);
            var directoryInfo = new DirectoryInfo(current);
            if (fileInfo.LinkTarget is not null || directoryInfo.LinkTarget is not null)
            {
                throw new FileBrowserException(StatusCodes.Status403Forbidden, "Symbolic link is not allowed", "Paths containing symbolic links cannot be accessed.");
            }
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                continue;
            }
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new FileBrowserException(StatusCodes.Status403Forbidden, "Symbolic link is not allowed", "Paths containing symbolic links cannot be accessed.");
            }
        }
    }

    private (List<FileEntry> Entries, Dictionary<string, int> Warnings, bool Partial) ReadDirectory(string directory, CancellationToken cancellationToken)
    {
        var started = _logger.IsEnabled(LogLevel.Debug) ? Stopwatch.GetTimestamp() : 0;
        var entries = new List<FileEntry>();
        var warnings = new Dictionary<string, int>(StringComparer.Ordinal);
        var partial = false;
        var inspected = 0;
        try
        {
            foreach (var child in EnumerateEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (inspected >= MaxVisitedEntries)
                {
                    partial = true;
                    AddWarning(warnings, "entry-limit");
                    break;
                }
                inspected++;
                if (TryCreateEntry(child, warnings, out var entry, out _))
                {
                    entries.Add(entry);
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            throw new FileBrowserException(StatusCodes.Status403Forbidden, "Directory is not accessible", "The requested directory cannot be read.");
        }
        catch (IOException)
        {
            throw new FileBrowserException(StatusCodes.Status409Conflict, "Directory is unavailable", "The directory changed or became unavailable while reading it.");
        }
        if (started != 0 && _logger.IsEnabled(LogLevel.Debug))
        {
            var durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            DirectoryScanCompleted(_logger, durationMs, inspected, entries.Count, partial);
        }
        return (entries, warnings, partial);
    }

    // Reuse enumeration metadata for names, attributes, size, and timestamp instead of
    // constructing FileInfo objects and asking the filesystem for each field again.
    private static FileSystemEnumerable<EnumeratedEntry> EnumerateEntries(string directory)
    {
        var options = new EnumerationOptions
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = false
        };
        return new FileSystemEnumerable<EnumeratedEntry>(
            directory,
            ReadEnumeratedEntry,
            options);
    }

    private static EnumeratedEntry ReadEnumeratedEntry(ref FileSystemEntry entry)
    {
        var fullPath = entry.ToFullPath();
        var name = entry.FileName.ToString();
        try
        {
            return new EnumeratedEntry(fullPath, name, entry.Attributes, entry.Length, entry.LastWriteTimeUtc, MetadataFailure.None);
        }
        catch (UnauthorizedAccessException)
        {
            return new EnumeratedEntry(fullPath, name, default, 0, default, MetadataFailure.Inaccessible);
        }
        catch (IOException)
        {
            return new EnumeratedEntry(fullPath, name, default, 0, default, MetadataFailure.Unavailable);
        }
    }

    private bool TryCreateEntry(EnumeratedEntry candidate, Dictionary<string, int> warnings, out FileEntry entry, out bool isDirectory)
    {
        entry = default!;
        isDirectory = false;
        var name = candidate.Name;
        if (name.StartsWith(TemporaryPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            if (candidate.Failure == MetadataFailure.Inaccessible)
            {
                AddWarning(warnings, "inaccessible-entry");
                return false;
            }
            if (candidate.Failure == MetadataFailure.Unavailable)
            {
                AddWarning(warnings, "unavailable-entry");
                return false;
            }
            if ((candidate.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                AddWarning(warnings, "symbolic-link-skipped");
                return false;
            }

            isDirectory = (candidate.Attributes & FileAttributes.Directory) != 0;
            if (!isDirectory && !FileSystemMetadata.Read(candidate.FullPath).IsRegularFile)
            {
                AddWarning(warnings, "unsupported-entry-skipped");
                return false;
            }
            var relative = Path.GetRelativePath(_root, candidate.FullPath).Replace(Path.DirectorySeparatorChar, '/');
            long? size = isDirectory ? null : candidate.Length;
            entry = new FileEntry(relative, name, isDirectory ? "folder" : "file", size, candidate.ModifiedUtc);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            AddWarning(warnings, "inaccessible-entry");
            return false;
        }
        catch (IOException)
        {
            AddWarning(warnings, "unavailable-entry");
            return false;
        }
    }

    private FileEntry CreateFileEntry(string fullPath)
    {
        var info = new FileInfo(fullPath);
        return new FileEntry(Path.GetRelativePath(_root, fullPath).Replace(Path.DirectorySeparatorChar, '/'), info.Name, "file", info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
    }

    private FileListResponse CreateResponse(List<FileEntry> entries, Dictionary<string, int> warningCounts, bool partial, string? sort, string? direction, int page, int pageSize, string key)
    {
        var started = _logger.IsEnabled(LogLevel.Debug) ? Stopwatch.GetTimestamp() : 0;
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize is <= 0 ? DefaultPageSize : pageSize, 1, MaxPageSize);
        var descending = string.Equals(direction, "desc", StringComparison.OrdinalIgnoreCase);
        var normalizedSort = sort?.ToLowerInvariant();
        var ordered = entries.OrderBy(entry => entry.Kind == "folder" ? 0 : 1)
            .ThenBy(entry => entry, Comparer<FileEntry>.Create((left, right) => CompareBySort(left, right, normalizedSort, descending)))
            .ToList();
        var summary = new FileSummary(
            ordered.Count(entry => entry.Kind == "file"),
            ordered.Count(entry => entry.Kind == "folder"),
            ordered.Where(entry => entry.SizeBytes.HasValue).Sum(entry => entry.SizeBytes!.Value));
        var totalPages = Math.Max(1, (int)Math.Ceiling(ordered.Count / (double)pageSize));
        page = Math.Min(page, totalPages);

        var warnings = warningCounts.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new FileWarning(pair.Key, pair.Value)).ToArray();
        partial |= warningCounts.Keys.Any(code => code is "inaccessible-entry" or "unavailable-entry" or "inaccessible-directory" or "unavailable-directory" or "symbolic-link-skipped" or "unsupported-entry-skipped");
        var complete = new FileListResponse(ordered, 1, pageSize, totalPages, summary, partial, warnings, Guid.NewGuid().ToString("N"));
        var preparationMs = started == 0 ? 0 : Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        StoreSnapshot(key, complete);
        if (started != 0 && _logger.IsEnabled(LogLevel.Debug))
        {
            ResponsePrepared(_logger, preparationMs, ordered.Count, summary.FileCount, summary.FolderCount);
        }
        return PageSnapshot(complete, page, pageSize);
    }

    private static int CompareBySort(FileEntry left, FileEntry right, string? sort, bool descending)
    {
        var comparison = sort switch
        {
            "size" => Nullable.Compare(left.SizeBytes, right.SizeBytes),
            "modified" => left.ModifiedUtc.CompareTo(right.ModifiedUtc),
            _ => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name)
        };
        if (descending) comparison = -comparison;
        return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left.Path, right.Path);
    }

    private static bool TryGetUploadFilename(MultipartSection section, out string filename)
    {
        filename = string.Empty;
        if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) ||
            !string.Equals(disposition.DispositionType.Value, "form-data", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(HeaderUtilities.RemoveQuotes(disposition.Name).Value, "file", StringComparison.Ordinal))
        {
            return false;
        }
        var rawName = disposition.FileNameStar.HasValue ? disposition.FileNameStar : disposition.FileName;
        filename = HeaderUtilities.RemoveQuotes(rawName).Value ?? string.Empty;
        return filename.Length > 0;
    }

    private static void ValidateFilename(string filename)
    {
        if (filename is "." or ".." || filename.Contains('/') || filename.Contains('\\') || filename.Contains(':') ||
            filename.StartsWith(TemporaryPrefix, StringComparison.OrdinalIgnoreCase) ||
            filename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Encoding.UTF8.GetByteCount(filename) > 255)
        {
            throw new FileBrowserException(StatusCodes.Status400BadRequest, "Invalid filename", "The uploaded filename is not allowed.");
        }
    }

    private async Task<long> CopyUploadSectionAsync(Stream source, string tempPath, CancellationToken cancellationToken)
    {
        var buffer = new byte[_uploadCopyBufferBytes];
        long total = 0;
        await using var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            _uploadStreamBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;
            total += read;
            if (total > MaxUploadBytes)
            {
                throw new FileBrowserException(StatusCodes.Status413PayloadTooLarge, "Upload too large", "The upload exceeds the 100 MiB limit.");
            }
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        await output.FlushAsync(cancellationToken);
        return total;
    }

    private static void AddWarning(Dictionary<string, int> warnings, string code)
    {
        warnings[code] = warnings.GetValueOrDefault(code) + 1;
    }

    private sealed class SearchState(string root)
    {
        public Queue<string> Pending { get; } = new([root]);
        public List<FileEntry> Entries { get; } = [];
        public Dictionary<string, int> Warnings { get; } = new(StringComparer.Ordinal);
        public int Inspected { get; set; }
        public bool Partial { get; set; }
        public bool LimitReached { get; set; }
    }

    private enum MetadataFailure { None, Inaccessible, Unavailable }

    private readonly record struct EnumeratedEntry(
        string FullPath, string Name, FileAttributes Attributes, long Length, DateTimeOffset ModifiedUtc, MetadataFailure Failure);
}
