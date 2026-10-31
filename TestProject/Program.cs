using TestProject;
using TestProject.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddPolicy("ConfiguredFrontend", policy =>
    policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod()));

var configuredRoot = builder.Configuration["FileBrowser:RootPath"] ?? "SampleFiles";
var contentRoot = builder.Environment.ContentRootPath;
var rootPath = Path.GetFullPath(configuredRoot, contentRoot);
var webRoot = Path.GetFullPath(builder.Environment.WebRootPath ?? Path.Combine(contentRoot, "wwwroot"));
var relativeToWebRoot = Path.GetRelativePath(webRoot, rootPath);
if (!string.Equals(relativeToWebRoot, "..", StringComparison.Ordinal) &&
    !relativeToWebRoot.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
    !Path.IsPathRooted(relativeToWebRoot))
{
    throw new InvalidOperationException("FileBrowser:RootPath must be outside wwwroot.");
}

if (!Directory.Exists(rootPath))
{
    throw new InvalidOperationException("FileBrowser:RootPath must identify an existing directory.");
}

builder.Services.AddSingleton(provider => new FileSystemService(rootPath,
    provider.GetRequiredService<ILogger<FileSystemService>>(),
    builder.Configuration.GetValue("FileBrowser:UploadCopyBufferBytes", 64 * 1024),
    builder.Configuration.GetValue("FileBrowser:UploadStreamBufferBytes", 4096),
    builder.Configuration.GetValue("FileBrowser:MaxConcurrentUploads", 0),
    builder.Configuration.GetValue("FileBrowser:SnapshotCacheMaxViews", 16),
    builder.Configuration.GetValue("FileBrowser:SnapshotCacheMaxBytes", 64L * 1024 * 1024)));
builder.Services.AddSingleton(new DiagnosticsSettings(builder.Configuration.GetValue<bool>("Diagnostics:Enabled")));

var app = builder.Build();
app.Use(async (context, next) =>
{
    var correlationId = Guid.NewGuid().ToString("N");
    context.Items["CorrelationId"] = correlationId;
    context.Response.OnStarting(() =>
    {
        context.Response.Headers["X-Correlation-ID"] = correlationId;
        return Task.CompletedTask;
    });
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors("ConfiguredFrontend");
app.MapControllers();
app.Run();

public partial class Program;
