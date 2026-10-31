using System.Net;
using System.Net.Http.Json;
using System.Text;
using TestProject.Models;
using TestProject.Services;
using Xunit;

namespace TestProject.Tests;

public sealed partial class BrowserApiTests
{
    [Fact]
    public async Task SnapshotPagesRemainStableUntilExplicitRefresh()
    {
        var first = await _client.GetFromJsonAsync<FileListResponse>("/api/files/browse?path=Many&pageSize=100");
        Assert.NotNull(first);
        File.WriteAllText(Path.Combine(_root, "Many", "aaa-new.txt"), "new");
        var second = await _client.GetFromJsonAsync<FileListResponse>($"/api/files/browse?path=Many&page=2&pageSize=100&snapshot={first.SnapshotId}");
        Assert.NotNull(second);
        Assert.Equal(105, second.Summary.FileCount);
        Assert.Equal(5, second.Entries.Count);
        Assert.Equal(first.SnapshotId, second.SnapshotId);
        var refreshed = await _client.GetFromJsonAsync<FileListResponse>("/api/files/browse?path=Many");
        Assert.Equal(106, refreshed!.Summary.FileCount);
        using var wrongScope = await _client.GetAsync($"/api/files/browse?path=Docs&snapshot={first.SnapshotId}");
        Assert.Equal(HttpStatusCode.Conflict, wrongScope.StatusCode);
    }

    [Fact]
    public void SnapshotCacheEvictsOldestViewWhenEstimatedByteBudgetIsReached()
    {
        var service = new FileSystemService(_root, snapshotCacheMaxViews: 64, snapshotCacheMaxBytes: 25_000);
        var first = service.Browse("Many", "name", "asc", 1, 100, CancellationToken.None);
        var second = service.Browse("Many", "size", "asc", 1, 100, CancellationToken.None);

        var expired = Assert.Throws<FileBrowserException>(() =>
        {
            service.Browse("Many", "name", "asc", 2, 100, CancellationToken.None, first.SnapshotId);
        });
        Assert.Equal(409, expired.StatusCode);
        var retained = service.Browse("Many", "size", "asc", 2, 100, CancellationToken.None, second.SnapshotId);
        Assert.Equal(second.SnapshotId, retained.SnapshotId);
        Assert.Equal(105, retained.Summary.FileCount);
    }

    [Fact]
    public void DefaultSnapshotCacheRetainsMoreThanTwoViews()
    {
        var service = new FileSystemService(_root);
        var first = service.Browse("Many", "name", "asc", 1, 100, CancellationToken.None);
        service.Browse("Many", "size", "asc", 1, 100, CancellationToken.None);
        service.Browse("Many", "modified", "asc", 1, 100, CancellationToken.None);

        var retained = service.Browse("Many", "name", "asc", 2, 100, CancellationToken.None, first.SnapshotId);
        Assert.Equal(first.SnapshotId, retained.SnapshotId);
        Assert.Equal(105, retained.Summary.FileCount);
        Assert.Equal(5, retained.Entries.Count);
    }

    [Fact]
    public async Task ApplicationDefaultSnapshotCacheRetains16ViewsAndEvictsOldest()
    {
        var first = await _client.GetFromJsonAsync<FileListResponse>("/api/files/browse?path=Many&sort=name");
        Assert.NotNull(first);
        for (var index = 0; index < 15; index++)
        {
            var empty = await _client.GetFromJsonAsync<FileListResponse>(
                $"/api/files/search?path=Many&q=missing-{index}");
            Assert.NotNull(empty);
        }

        var retained = await _client.GetFromJsonAsync<FileListResponse>(
            $"/api/files/browse?path=Many&sort=name&page=2&snapshot={first.SnapshotId}");
        Assert.NotNull(retained);
        Assert.Equal(first.SnapshotId, retained.SnapshotId);
        Assert.Equal(105, retained.Summary.FileCount);
        Assert.Equal(5, retained.Entries.Count);

        await _client.GetFromJsonAsync<FileListResponse>("/api/files/search?path=Many&q=missing-final");
        using var evicted = await _client.GetAsync(
            $"/api/files/browse?path=Many&sort=name&page=2&snapshot={first.SnapshotId}");
        Assert.Equal(HttpStatusCode.Conflict, evicted.StatusCode);
    }

    [Fact]
    public async Task SearchContinuesAfterUnreadableDirectory()
    {
        if (OperatingSystem.IsWindows()) return;
        var folders = Directory.GetDirectories(_root);
        var denied = folders[0];
        var readable = folders[1];
        File.WriteAllText(Path.Combine(readable, "survivor-needle.txt"), "found");
        var mode = File.GetUnixFileMode(denied);
        try
        {
            File.SetUnixFileMode(denied, UnixFileMode.None);
            var result = await _client.GetFromJsonAsync<FileListResponse>("/api/files/search?q=survivor-needle");
            Assert.NotNull(result);
            Assert.True(result.Partial);
            Assert.Contains(result.Warnings, warning => warning.Code == "inaccessible-directory");
            Assert.Contains(result.Entries, entry => entry.Name == "survivor-needle.txt");
        }
        finally { File.SetUnixFileMode(denied, mode); }
    }

    [Fact]
    public async Task UploadToReadOnlyFolderReturnsProblemWithoutTempFiles()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(_root, "Docs");
        var mode = File.GetUnixFileMode(directory);
        try
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            using var upload = MakeUpload("denied.txt", Encoding.UTF8.GetBytes("no"));
            using var result = await _client.PostAsync("/api/files/upload?path=Docs", upload);
            Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
            Assert.Equal("application/problem+json", result.Content.Headers.ContentType!.MediaType);
            Assert.Empty(Directory.GetFiles(directory, ".filebrowser-upload-*"));
        }
        finally { File.SetUnixFileMode(directory, mode); }
    }

    [Theory]
    [InlineData("top.txt", "copied.txt")]
    [InlineData("Docs", "CopiedDocs")]
    public async Task CopyMoveAndDeletePreserveTreeAndBytes(string source, string destination)
    {
        using var copied = await Change("copy", source, destination);
        Assert.Equal(HttpStatusCode.NoContent, copied.StatusCode);
        var suffix = source == "Docs" ? "/Nested/needle.txt" : "";
        Assert.Equal(File.ReadAllBytes(Path.Combine(_root, source + suffix)), File.ReadAllBytes(Path.Combine(_root, destination + suffix)));
        using var moved = await Change("move", destination, "moved");
        Assert.Equal(HttpStatusCode.NoContent, moved.StatusCode);
        Assert.False(Path.Exists(Path.Combine(_root, destination)));
        Assert.True(File.Exists(Path.Combine(_root, "moved" + suffix)));
        using var deleted = await Change("delete", "moved");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.False(Path.Exists(Path.Combine(_root, "moved")));
        Assert.True(Path.Exists(Path.Combine(_root, source)));
        Assert.Empty(Directory.GetFileSystemEntries(_root, ".filebrowser-upload-*"));
    }

    [Theory]
    [InlineData("delete", "", null, 400)]
    [InlineData("delete", "../escape", null, 400)]
    [InlineData("move", "Docs", "Docs/Nested/child", 400)]
    [InlineData("copy", "top.txt", "top.txt", 400)]
    [InlineData("copy", "top.txt", "Docs", 409)]
    [InlineData("move", "top.txt", "../escape", 400)]
    [InlineData("copy", "top.txt", "missing/target", 404)]
    [InlineData("delete", "missing", null, 404)]
    public async Task InvalidOperationsLeaveSourceIntact(string operation, string source, string? destination, int status)
    {
        using var result = await Change(operation, source, destination);
        Assert.Equal(status, (int)result.StatusCode);
        Assert.True(File.Exists(Path.Combine(_root, "top.txt")));
        Assert.True(Directory.Exists(Path.Combine(_root, "Docs")));
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("copy")]
    [InlineData("move")]
    public async Task OperationsRejectTreesContainingLinksBeforeMutation(string operation)
    {
        if (!_hasSymbolicLink) return;
        Directory.CreateDirectory(Path.Combine(_root, "LinkTree"));
        File.CreateSymbolicLink(Path.Combine(_root, "LinkTree", "link"), Path.Combine(_root, "top.txt"));
        using var result = await Change(operation, "LinkTree", "LinkCopy");
        Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        Assert.True(Directory.Exists(Path.Combine(_root, "LinkTree")));
        Assert.False(Path.Exists(Path.Combine(_root, "LinkCopy")));
    }

    [Fact]
    public async Task OversizeCopyIsRejectedBeforePublishingOrStaging()
    {
        await using (var file = File.Create(Path.Combine(_root, "large.bin")))
            file.SetLength(1024L * 1024 * 1024 + 1);
        using var result = await Change("copy", "large.bin", "large-copy.bin");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, result.StatusCode);
        Assert.False(File.Exists(Path.Combine(_root, "large-copy.bin")));
        Assert.Empty(Directory.GetFileSystemEntries(_root, ".filebrowser-upload-*"));
    }

    [Fact]
    public async Task OversizedDeleteTreeIsRejectedBeforeRemovingAnything()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, "LargeTree")).FullName;
        for (var i = 0; i < 10000; i++) File.WriteAllText(Path.Combine(directory, $"{i}.txt"), "");
        using var result = await Change("delete", "LargeTree");
        Assert.Equal(HttpStatusCode.Conflict, result.StatusCode);
        Assert.Equal(10000, Directory.GetFiles(directory).Length);
    }

    [Fact]
    public async Task FailedCopyCleansStagingAndLeavesSourceUntouched()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Directory.CreateDirectory(Path.Combine(_root, "CopyFailure")).FullName;
        File.WriteAllText(Path.Combine(directory, "first.txt"), "first");
        File.WriteAllText(Path.Combine(directory, "second.txt"), "second");
        var denied = Directory.GetFiles(directory)[1];
        var mode = File.GetUnixFileMode(denied);
        try
        {
            File.SetUnixFileMode(denied, UnixFileMode.None);
            using var result = await Change("copy", "CopyFailure", "RejectedCopy");
            Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
            Assert.False(Directory.Exists(Path.Combine(_root, "RejectedCopy")));
            Assert.Empty(Directory.GetFileSystemEntries(_root, ".filebrowser-upload-*"));
            Assert.Equal(2, Directory.GetFiles(directory).Length);
        }
        finally { File.SetUnixFileMode(denied, mode); }
    }

    [Fact]
    public async Task CaseAliasCannotCopyFolderIntoItself()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS()) return;
        using var result = await Change("copy", "Docs", "docs/Nested/child");
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(_root, "Docs", "Nested", "child")));
    }

    private Task<HttpResponseMessage> Change(string operation, string source, string? destination = null) =>
        _client.PostAsJsonAsync($"/api/files/{operation}", new FileChangeRequest(source, destination));
}
