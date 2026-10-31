using System.Text;
using Microsoft.AspNetCore.Http;
using TestProject.Services;
using Xunit;

namespace TestProject.Tests;

public sealed class UploadTuningTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"upload-tuning-{Guid.NewGuid():N}");
    public UploadTuningTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    [Fact]
    public async Task UploadGateBoundsActiveRequestsAndReleasesOnCancellation()
    {
        var service = new FileSystemService(_root, uploadCopyBufferBytes: 128 * 1024,
            uploadStreamBufferBytes: 1, maxConcurrentUploads: 1);
        var blocking = new GateStream(Body("first.txt", "first"));
        var first = service.UploadAsync("", Request(blocking), CancellationToken.None);
        await blocking.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        using var cancelled = new CancellationTokenSource();
        var second = service.UploadAsync("", Request(new MemoryStream(Body("second.txt", "second"))), cancelled.Token);
        Assert.False(second.IsCompleted);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);

        blocking.Release.TrySetResult();
        var uploaded = await first.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(File.Exists(Path.Combine(_root, uploaded.Entry.Name)));
        var next = await service.UploadAsync("", Request(new MemoryStream(Body("next.txt", "next"))), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("next.txt", next.Entry.Name);
    }

    private static HttpRequest Request(Stream body)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = body;
        context.Request.ContentType = "multipart/form-data; boundary=upload-tuning";
        return context.Request;
    }

    private static byte[] Body(string name, string content) => Encoding.UTF8.GetBytes(
        $"--upload-tuning\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{name}\"\r\nContent-Type: application/octet-stream\r\n\r\n{content}\r\n--upload-tuning--\r\n");

    private sealed class GateStream(byte[] bytes) : MemoryStream(bytes)
    {
        private int _blocked;
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _blocked, 1) == 0)
            {
                ReadStarted.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}
