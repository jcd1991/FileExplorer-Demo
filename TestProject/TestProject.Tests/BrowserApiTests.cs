using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TestProject.Models;
using TestProject.Services;
using Xunit;

namespace TestProject.Tests;

public sealed partial class BrowserApiTests : IDisposable
{
    private readonly BrowserApiFixture _fixture = new();
    private readonly HttpClient _client;
    private readonly string _root;
    private readonly bool _hasSymbolicLink;

    public BrowserApiTests()
    {
        var fixture = _fixture;
        _client = fixture.Client;
        _root = fixture.Root;
        _hasSymbolicLink = fixture.HasSymbolicLink;
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task BrowseReturnsEntriesAndAggregateCounts()
    {
        var response = await _client.GetFromJsonAsync<FileListResponse>("/api/files/browse");
        Assert.NotNull(response);
        Assert.Contains(response.Entries, entry => entry.Path == "top.txt" && entry.Kind == "file");
        Assert.Contains(response.Entries, entry => entry.Path == "Docs" && entry.Kind == "folder");
        Assert.Equal(2, response.Summary.FileCount);
        Assert.Equal(2, response.Summary.FolderCount);
        Assert.True(response.Summary.TotalFileBytes > 0);
    }

    [Fact]
    public async Task BrowseIncludesHiddenFilesAndUsesTheirEnumeratedMetadata()
    {
        var hiddenPath = Path.Combine(_root, ".hidden.txt");
        await File.WriteAllTextAsync(hiddenPath, "hidden");

        var response = await _client.GetFromJsonAsync<FileListResponse>("/api/files/browse");

        Assert.NotNull(response);
        var hidden = Assert.Single(response.Entries.Where(entry => entry.Name == ".hidden.txt"));
        Assert.Equal(6, hidden.SizeBytes);
        Assert.NotEqual(default, hidden.ModifiedUtc);
    }

    [Fact]
    public async Task BrowsePaginatesButCountsAllImmediateEntries()
    {
        var response = await _client.GetFromJsonAsync<FileListResponse>("/api/files/browse?path=Many&page=2&pageSize=100");
        Assert.NotNull(response);
        Assert.Equal(2, response.TotalPages);
        Assert.Equal(5, response.Entries.Count);
        Assert.Equal(105, response.Summary.FileCount);
    }

    [Fact]
    public async Task BrowseSortsBySizeAndPreservesUnicodeNames()
    {
        var response = await _client.GetFromJsonAsync<FileListResponse>("/api/files/browse?path=Many&sort=size&direction=asc&pageSize=20");
        Assert.NotNull(response);
        Assert.Equal(response.Entries.OrderBy(entry => entry.SizeBytes).Select(entry => entry.Path), response.Entries.Select(entry => entry.Path));

        var root = await _client.GetFromJsonAsync<FileListResponse>("/api/files/browse");
        Assert.NotNull(root);
        Assert.Contains(root.Entries, entry => entry.Name == "rêport-東京.txt" && entry.Path == "rêport-東京.txt");
    }

    [Fact]
    public async Task SearchFindsNamesRecursively()
    {
        var response = await _client.GetFromJsonAsync<FileListResponse>("/api/files/search?q=needle");
        Assert.NotNull(response);
        Assert.Contains(response.Entries, entry => entry.Path == "Docs/Nested/needle.txt");
    }

    [Fact]
    public async Task SearchSkipsGeneratedFoldersByDefaultAndCanIncludeThem()
    {
        var generated = Path.Combine(_root, "node_modules", "nested");
        Directory.CreateDirectory(generated);
        await File.WriteAllTextAsync(Path.Combine(generated, "generated-needle.txt"), "generated");

        var defaultResponse = await _client.GetFromJsonAsync<FileListResponse>("/api/files/search?q=generated-needle");
        Assert.NotNull(defaultResponse);
        Assert.DoesNotContain(defaultResponse.Entries, entry => entry.Path.Contains("node_modules", StringComparison.OrdinalIgnoreCase));

        var inclusiveResponse = await _client.GetFromJsonAsync<FileListResponse>("/api/files/search?q=generated-needle&skipGenerated=false");
        Assert.NotNull(inclusiveResponse);
        Assert.Contains(inclusiveResponse.Entries, entry => entry.Path == "node_modules/nested/generated-needle.txt");
    }

    [Fact]
    public async Task SearchReportsPartialResultsWhenSkippingSymbolicLinks()
    {
        if (!_hasSymbolicLink) return;
        var response = await _client.GetFromJsonAsync<FileListResponse>("/api/files/search?q=needle");
        Assert.NotNull(response);
        Assert.True(response.Partial);
        Assert.Contains(response.Warnings, warning => warning.Code == "symbolic-link-skipped");
    }

    [Fact]
    public void SearchHonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var service = new FileSystemService(_root);
        Assert.Throws<OperationCanceledException>(() => service.Search("", "needle", "name", "asc", 1, 100, cancellation.Token));
    }

    [Theory]
    [InlineData("..%2Fsecret.txt")]
    [InlineData("%2Fetc%2Fpasswd")]
    [InlineData("%00")]
    public async Task BrowseRejectsPathsOutsideRoot(string path)
    {
        using var response = await _client.GetAsync($"/api/files/browse?path={path}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task UploadAndDownloadPreserveBytesAndRejectConflicts()
    {
        var bytes = Encoding.UTF8.GetBytes("new upload bytes");
        using var upload = MakeUpload("new.bin", bytes);
        using var response = await _client.PostAsync("/api/files/upload", upload);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var entry = await response.Content.ReadFromJsonAsync<FileEntry>();
        Assert.NotNull(entry);
        Assert.Equal(bytes.Length, entry.SizeBytes);

        using var downloaded = await _client.GetAsync($"/api/files/download?path={Uri.EscapeDataString(entry.Path)}");
        Assert.Equal(HttpStatusCode.OK, downloaded.StatusCode);
        Assert.Equal(bytes, await downloaded.Content.ReadAsByteArrayAsync());
        File.Delete(Path.Combine(_root, "new.bin"));

        using var duplicate = MakeUpload("top.txt", bytes);
        using var conflict = await _client.PostAsync("/api/files/upload", duplicate);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task DownloadSupportsByteRanges()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/files/download?path=top.txt");
        request.Headers.Range = new RangeHeaderValue(1, 3);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(3, (await response.Content.ReadAsByteArrayAsync()).Length);
    }

    [Fact]
    public async Task UploadRejectsDeclaredOversizeBeforeReadingBody()
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "multipart/form-data; boundary=browser-test";
        context.Request.ContentLength = FileSystemService.MaxUploadBytes + 1_048_577;
        using var service = new FileSystemService(_root);
        var exception = await Assert.ThrowsAsync<FileBrowserException>(() => service.UploadAsync("", context.Request, CancellationToken.None));
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, exception.StatusCode);
    }

    [Fact]
    public async Task UploadRejectsPathsInTheFilename()
    {
        using var upload = MakeUpload("../escape.txt", Encoding.UTF8.GetBytes("no"));
        using var response = await _client.PostAsync("/api/files/upload", upload);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ApiReturnsCorrelationIdAndConfigAndStaticDialog()
    {
        using var config = await _client.GetAsync("/api/config");
        Assert.True(config.Headers.Contains("X-Correlation-ID"));
        var configBody = await config.Content.ReadFromJsonAsync<DiagnosticsResponse>();
        Assert.NotNull(configBody);
        Assert.False(configBody.DiagnosticsEnabled);

        var html = await _client.GetStringAsync("/");
        Assert.Contains("<dialog", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("type=\"module\"", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BrowseSkipsSymbolicLinks()
    {
        if (!_hasSymbolicLink) return;
        var response = await _client.GetFromJsonAsync<FileListResponse>("/api/files/browse");
        Assert.NotNull(response);
        Assert.DoesNotContain(response.Entries, entry => entry.Name == "outside-link");
        Assert.Contains(response.Warnings, warning => warning.Code == "symbolic-link-skipped");
        Assert.True(response.Partial);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UploadRejectsExtraFieldsWithoutPublishingOrLeavingTemporaryFiles(bool extraFirst)
    {
        using var upload = new MultipartFormDataContent();
        if (extraFirst) upload.Add(new StringContent("unexpected"), "extra");
        upload.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("payload")), "file", "rejected.bin");
        if (!extraFirst) upload.Add(new StringContent("unexpected"), "extra");
        using var response = await _client.PostAsync("/api/files/upload", upload);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(File.Exists(Path.Combine(_root, "rejected.bin")));
        Assert.Empty(Directory.EnumerateFiles(_root, ".filebrowser-upload-*"));
    }

    [Fact]
    public async Task UploadReadLimitCountsAcrossReadsWithoutOwningSource()
    {
        using var source = new MemoryStream(new byte[12]);
        using (var bounded = new UploadReadStream(source, 10))
        {
            var buffer = new byte[6];
            await bounded.ReadExactlyAsync(buffer.AsMemory());
            var exception = await Assert.ThrowsAsync<FileBrowserException>(async () =>
                await bounded.ReadExactlyAsync(buffer.AsMemory()));
            Assert.Equal(StatusCodes.Status413PayloadTooLarge, exception.StatusCode);
            Assert.Equal(11, source.Position);
        }
        Assert.True(source.CanRead);
    }

    [Fact]
    public void CleanupFailureWarnsWithoutExposingFilesystemDetails()
    {
        var logger = new CleanupLogger();
        using var service = new FileSystemService(_root, logger);
        service.DeleteTemporaryFile(Path.Combine(_root, "Docs"), "test-correlation");
        Assert.Equal(LogLevel.Warning, logger.Level);
        Assert.Contains("test-correlation", logger.Message);
        Assert.DoesNotContain(_root, logger.Message);
        Assert.DoesNotContain("Docs", logger.Message);
        Assert.True(Directory.Exists(Path.Combine(_root, "Docs")));
    }

    private static MultipartFormDataContent MakeUpload(string filename, byte[] bytes)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "file", filename);
        return content;
    }
}

internal sealed class CleanupLogger : ILogger<FileSystemService>
{
    public string Message { get; private set; } = "";
    public LogLevel Level { get; private set; }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Level = logLevel;
        Message = formatter(state, exception);
    }
}

public sealed class BrowserApiFixture : IDisposable
{
    private readonly BrowserApiFactory _factory;
    public string Root { get; }
    public HttpClient Client { get; }
    public bool HasSymbolicLink { get; }

    public BrowserApiFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), $"file-browser-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(Root, "Docs", "Nested"));
        Directory.CreateDirectory(Path.Combine(Root, "Many"));
        File.WriteAllText(Path.Combine(Root, "top.txt"), "top-file-content");
        File.WriteAllText(Path.Combine(Root, "rêport-東京.txt"), "unicode name");
        File.WriteAllText(Path.Combine(Root, "Docs", "Nested", "needle.txt"), "search content");
        for (var index = 0; index < 105; index++)
        {
            File.WriteAllText(Path.Combine(Root, "Many", $"item-{index:D3}.txt"), index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(Root)!, $"outside-{Path.GetFileName(Root)}.txt"), "outside");
        var linkCreated = false;
        try
        {
            File.CreateSymbolicLink(Path.Combine(Root, "outside-link"), Path.Combine(Path.GetDirectoryName(Root)!, $"outside-{Path.GetFileName(Root)}.txt"));
            linkCreated = true;
        }
        catch (PlatformNotSupportedException) { }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
        HasSymbolicLink = linkCreated;

        _factory = new BrowserApiFactory(Root);
        Client = _factory.CreateClient();
    }

    public void Dispose()
    {
        Client.Dispose();
        _factory.Dispose();
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        var outside = Path.Combine(Path.GetDirectoryName(Root)!, $"outside-{Path.GetFileName(Root)}.txt");
        if (File.Exists(outside)) File.Delete(outside);
    }
}

internal sealed class BrowserApiFactory(string root) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.UseSetting("FileBrowser:RootPath", root);
        builder.UseSetting("Diagnostics:Enabled", "false");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FileBrowser:RootPath"] = root,
            ["Diagnostics:Enabled"] = "false"
        }));
    }
}
