using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using TestProject.Models;
using TestProject.Services;
using Xunit;

namespace TestProject.Tests;

public sealed partial class BrowserApiTests
{
    [Theory]
    [InlineData("copy")]
    [InlineData("move")]
    public async Task UnicodeAliasCannotPlaceFolderInsideItself(string operation)
    {
        var original = "Caf\u00e9";
        var alias = "Cafe\u0301";
        Directory.CreateDirectory(Path.Combine(_root, original));
        File.WriteAllText(Path.Combine(_root, original, "keep.txt"), "unchanged");
        // Normalization-sensitive filesystems have two genuinely different paths.
        if (!Directory.Exists(Path.Combine(_root, alias))) return;
        using var response = await Change(operation, original, alias + "/child");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(_root, original, "child")));
        Assert.Equal("unchanged", File.ReadAllText(Path.Combine(_root, original, "keep.txt")));
        using var valid = await Change("copy", original, "UnicodeCopy");
        Assert.Equal(HttpStatusCode.NoContent, valid.StatusCode);
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("move")]
    [InlineData("copy")]
    public async Task FifoIsRejectedBeforeMutationAndGateIsReleased(string operation)
    {
        if (OperatingSystem.IsWindows()) return;
        var pipe = Path.Combine(_root, "pipe");
        Assert.Equal(0, MakeFifo(pipe, 0x180));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var rejected = await _client.PostAsJsonAsync($"/api/files/{operation}",
            new FileChangeRequest("pipe", "pipe-copy"), timeout.Token);
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        using var next = await _client.PostAsJsonAsync("/api/files/move",
            new FileChangeRequest("top.txt", "moved.txt"), timeout.Token);
        Assert.Equal(HttpStatusCode.NoContent, next.StatusCode);
        Assert.False(File.Exists(Path.Combine(_root, "pipe-copy")));
        Assert.Empty(Directory.GetFileSystemEntries(_root, ".filebrowser-upload-*"));
    }

    [Fact]
    public async Task CheckedOpenRejectsFifoWithoutWaitingForWriter()
    {
        if (OperatingSystem.IsWindows()) return;
        var pipe = Path.Combine(_root, "pipe");
        Assert.Equal(0, MakeFifo(pipe, 0x180));
        var error = await Task.Run(() => Assert.Throws<FileBrowserException>(() =>
        {
            using var stream = FileSystemMetadata.OpenRegularRead(pipe);
        })).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(403, error.StatusCode);
    }

    [Fact]
    public async Task FifoIsSkippedInListingsAndRejectedForDownload()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal(0, MakeFifo(Path.Combine(_root, "pipe"), 0x180));
        var view = await _client.GetFromJsonAsync<FileListResponse>("/api/files/browse");
        Assert.NotNull(view);
        Assert.DoesNotContain(view.Entries, entry => entry.Name == "pipe");
        Assert.Contains(view.Warnings, warning => warning.Code == "unsupported-entry-skipped");
        Assert.True(view.Partial);
        using var response = await _client.GetAsync("/api/files/download?path=pipe");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CancelledMutationDoesNotHoldGateOrChangeSource()
    {
        var service = new FileSystemService(_root);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ChangeAsync("copy", "Docs", "CancelledCopy", cancelled.Token));
        Assert.False(Directory.Exists(Path.Combine(_root, "CancelledCopy")));
        await service.ChangeAsync("move", "top.txt", "after-cancel.txt", CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(_root, "after-cancel.txt")));
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("move")]
    [InlineData("copy")]
    public async Task SpecialEntryInTreeRejectsEntirePlanBeforeChangingFiles(string operation)
    {
        if (OperatingSystem.IsWindows()) return;
        var tree = Directory.CreateDirectory(Path.Combine(_root, "SpecialTree")).FullName;
        File.WriteAllText(Path.Combine(tree, "keep.txt"), "keep");
        Assert.Equal(0, MakeFifo(Path.Combine(tree, "pipe"), 0x180));
        using var result = await Change(operation, "SpecialTree", "RejectedTree");
        Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(tree, "keep.txt")));
        Assert.False(Directory.Exists(Path.Combine(_root, "RejectedTree")));
        Assert.Empty(Directory.GetFileSystemEntries(_root, ".filebrowser-upload-*"));
    }

    [Fact]
    public async Task CancellationDuringCopyRemovesStageAndReleasesGate()
    {
        await using (var file = File.Create(Path.Combine(_root, "cancel-source.bin")))
            file.SetLength(256L * 1024 * 1024);
        using var cancelled = new CancellationTokenSource();
        using var watcher = new FileSystemWatcher(_root, ".filebrowser-upload-*");
        watcher.Created += (_, _) => cancelled.Cancel();
        watcher.EnableRaisingEvents = true;
        var service = new FileSystemService(_root);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Run(() =>
            service.ChangeAsync("copy", "cancel-source.bin", "CancelledCopy.bin", cancelled.Token))
            .WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(File.Exists(Path.Combine(_root, "CancelledCopy.bin")));
        Assert.Empty(Directory.GetFileSystemEntries(_root, ".filebrowser-upload-*"));
        await service.ChangeAsync("move", "top.txt", "after-cancel.txt", CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(_root, "after-cancel.txt")));
    }

    [Fact]
    public async Task RootWithTrailingSeparatorSupportsNestedDestination()
    {
        var service = new FileSystemService(_root + Path.DirectorySeparatorChar);
        await service.ChangeAsync("copy", "Docs", "Many/Copy", CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(_root, "Many", "Copy", "Nested", "needle.txt")));
    }

    [DllImport("System.Native", EntryPoint = "SystemNative_MkFifo", SetLastError = true)]
    private static extern int MakeFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode);
}
