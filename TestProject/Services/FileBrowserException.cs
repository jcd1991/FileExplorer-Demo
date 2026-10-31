namespace TestProject.Services;

public sealed class FileBrowserException(int statusCode, string title, string detail) : Exception(detail)
{
    public int StatusCode { get; } = statusCode;
    public string Title { get; } = title;
}
