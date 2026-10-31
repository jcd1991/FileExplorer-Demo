using System.Diagnostics;

namespace TestProject.Services;

public sealed partial class FileSystemService
{
    private const int MaxOperationEntries = 10_000;
    private const long MaxCopyBytes = 1024L * 1024 * 1024;
    private readonly SemaphoreSlim _mutationLock = new(1, 1);

    public async Task ChangeAsync(string operation, string? source, string? destination, CancellationToken token)
    {
        await _mutationLock.WaitAsync(token);
        try
        {
            var sourcePath = ResolvePath(source, requireDirectory: false);
            if (sourcePath.Equals(_root, PathComparison)) throw InvalidOperationPath("The configured root cannot be changed.");
            if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
                throw new FileBrowserException(404, "Entry not found", "The source no longer exists.");
            var target = operation == "delete" ? null : ResolveDestination(sourcePath, destination);
            var plan = PlanOperation(sourcePath, operation == "copy", token);
            if (operation == "delete") DeletePlan(plan, token);
            else if (operation == "move") MoveEntry(sourcePath, target!);
            else if (operation == "copy") await CopyPlanAsync(sourcePath, target!, plan, token);
            else throw InvalidOperationPath("Unknown file operation.");
        }
        finally { _mutationLock.Release(); }
    }

    private string ResolveDestination(string source, string? destination)
    {
        if (string.IsNullOrWhiteSpace(destination)) throw InvalidOperationPath("Enter a root-relative destination including the new name.");
        var target = ResolvePath(destination, requireDirectory: false);
        var comparison = PathComparison;
        if (target.Equals(source, comparison) || target.StartsWith(source + Path.DirectorySeparatorChar, comparison))
            throw InvalidOperationPath("The destination cannot be the source or a child of it.");
        ValidateFilename(Path.GetFileName(target));
        var parent = Path.GetRelativePath(_root, Path.GetDirectoryName(target)!).Replace(Path.DirectorySeparatorChar, '/');
        var parentPath = ResolvePath(parent == "." ? "" : parent, true);
        RejectSourceAncestor(source, parentPath);
        EnsureDestinationAvailable(target);
        return target;
    }

    private void RejectSourceAncestor(string source, string parent)
    {
        var origin = FileSystemMetadata.Read(source);
        if (!origin.IsDirectory) return;
        // Identity comparisons cover Unicode, case, and short-name aliases without
        // changing the caller's actual filesystem spelling. Each step removes a segment.
        var remaining = parent.Split(Path.DirectorySeparatorChar).Length;
        for (var current = parent; remaining > 0; remaining--)
        {
            if (FileSystemMetadata.Read(current).Identity == origin.Identity)
                throw InvalidOperationPath("The destination cannot be the source or a child of it.");
            if (current.Equals(_root, PathComparison)) return;
            current = Path.GetDirectoryName(current) ?? throw InvalidOperationPath("Invalid destination parent.");
        }
        throw InvalidOperationPath("Invalid destination ancestry.");
    }

    private List<(string Path, bool Directory)> PlanOperation(string source, bool copying, CancellationToken token)
    {
        var pending = new Queue<string>();
        var plan = new List<(string Path, bool Directory)>();
        var started = Stopwatch.GetTimestamp();
        long copyBytes = 0;
        pending.Enqueue(source);
        while (pending.TryDequeue(out var current))
        {
            token.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(started).TotalSeconds >= 5)
                throw new FileBrowserException(409, "Operation limit reached", "Use a smaller tree.");
            if (Path.GetFileName(current).StartsWith(TemporaryPrefix, StringComparison.OrdinalIgnoreCase))
                throw new FileBrowserException(409, "Transfer in progress", "The tree contains a temporary transfer; retry after it completes.");
            EnsureNoLinks(current);
            var metadata = FileSystemMetadata.Read(current);
            var directory = metadata.IsDirectory;
            if (!directory) FileSystemMetadata.RequireRegular(metadata);
            plan.Add((current, directory));
            if (!directory)
            {
                if (copying && (copyBytes += new FileInfo(current).Length) > MaxCopyBytes)
                    throw new FileBrowserException(413, "Copy limit reached", "Copy at most 1 GiB per operation.");
                continue;
            }
            foreach (var child in System.IO.Directory.EnumerateFileSystemEntries(current))
            {
                token.ThrowIfCancellationRequested();
                if (plan.Count + pending.Count >= MaxOperationEntries || Stopwatch.GetElapsedTime(started).TotalSeconds >= 5)
                    throw new FileBrowserException(409, "Operation limit reached", "Use a smaller tree (maximum 10,000 entries and five seconds of preflight work).");
                pending.Enqueue(child);
            }
        }
        return plan;
    }

    private static void DeletePlan(List<(string Path, bool Directory)> plan, CancellationToken token)
    {
        for (var index = plan.Count - 1; index >= 0; index--)
        {
            token.ThrowIfCancellationRequested();
            var entry = plan[index];
            if (entry.Directory) Directory.Delete(entry.Path);
            else File.Delete(entry.Path);
        }
    }

    private static void MoveEntry(string source, string target)
    {
        if (Directory.Exists(source)) Directory.Move(source, target);
        else File.Move(source, target);
    }

    private async Task CopyPlanAsync(string source, string target, List<(string Path, bool Directory)> plan, CancellationToken token)
    {
        var stage = Path.Combine(Path.GetDirectoryName(target)!, $"{TemporaryPrefix}{Guid.NewGuid():N}.tmp");
        var created = new List<(string Path, bool Directory)>();
        long total = 0;
        try
        {
            foreach (var entry in plan)
            {
                token.ThrowIfCancellationRequested();
                EnsureNoLinks(entry.Path);
                var output = entry.Path == source ? stage : Path.Combine(stage, Path.GetRelativePath(source, entry.Path));
                if (entry.Directory) Directory.CreateDirectory(output);
                else
                {
                    await using var input = FileSystemMetadata.OpenRegularRead(entry.Path);
                    await using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
                    created.Add((output, false));
                    total += await CopyWithinLimitAsync(input, file, MaxCopyBytes - total, token);
                    continue;
                }
                created.Add((output, true));
            }
            token.ThrowIfCancellationRequested();
            EnsureDestinationAvailable(target);
            MoveEntry(stage, target);
            created.Clear();
        }
        finally
        {
            for (var index = created.Count - 1; index >= 0; index--)
            {
                try
                {
                    if (created[index].Directory) Directory.Delete(created[index].Path);
                    else File.Delete(created[index].Path);
                }
                catch (IOException) { LogCleanupFailure(_logger, null, null); }
                catch (UnauthorizedAccessException) { LogCleanupFailure(_logger, null, null); }
            }
        }
    }

    private static async Task<long> CopyWithinLimitAsync(Stream input, Stream output, long remaining, CancellationToken token)
    {
        var buffer = new byte[65536];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, token);
            if (read == 0) return total;
            total += read;
            if (total > remaining) throw new FileBrowserException(413, "Copy limit reached", "Copy at most 1 GiB per operation.");
            await output.WriteAsync(buffer.AsMemory(0, read), token);
        }
    }

    private static FileBrowserException InvalidOperationPath(string detail) => new(400, "Invalid operation", detail);
}
