namespace TestProject.Services;

// Leaves the HTTP request stream owned by ASP.NET Core. Counts all parser reads,
// including multipart headers and fields, when Content-Length is unavailable.
internal sealed class UploadReadStream(Stream inner, long limit) : Stream
{
    private long _bytesRead;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _bytesRead; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, AllowedCount(count));
        Record(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer[..AllowedCount(buffer.Length)], cancellationToken);
        Record(read);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private int AllowedCount(int requested) => (int)Math.Min(requested, limit - _bytesRead + 1);

    private void Record(int read)
    {
        _bytesRead += read;
        if (_bytesRead > limit)
            throw new FileBrowserException(StatusCodes.Status413PayloadTooLarge, "Upload too large", "The multipart request exceeds the upload request limit.");
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
