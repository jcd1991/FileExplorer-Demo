namespace TestProject.Services;

public sealed partial class FileSystemService
{
    public void Dispose()
    {
        _uploadGate?.Dispose();
        _mutationLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
