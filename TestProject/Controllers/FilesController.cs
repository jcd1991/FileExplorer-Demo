using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using TestProject.Models;
using TestProject.Services;

namespace TestProject.Controllers;

[ApiController]
[Route("api/files")]
public sealed class FilesController(
    FileSystemService files,
    DiagnosticsSettings diagnostics,
    ILogger<FilesController> logger) : ControllerBase
{
    private static readonly Action<ILogger, string, object?, int, Exception?> LogIncompleteResults =
        LoggerMessage.Define<string, object?, int>(LogLevel.Warning, new EventId(1, "IncompleteResults"),
            "File browser {Operation} returned incomplete results; CorrelationId={CorrelationId}; WarningCount={WarningCount}");
    private static readonly Action<ILogger, string, object?, Exception?> LogFailedOperation =
        LoggerMessage.Define<string, object?>(LogLevel.Warning, new EventId(2, "OperationFailed"),
            "File browser {Operation} failed; CorrelationId={CorrelationId}");
    private static readonly Action<ILogger, string, string, object?, double, long, int, Exception?> LogCompletedOperation =
        LoggerMessage.Define<string, string, object?, double, long, int>(LogLevel.Information, new EventId(3, "OperationCompleted"),
            "File browser {Operation} {Outcome}; CorrelationId={CorrelationId}; DurationMs={DurationMs}; Bytes={Bytes}; Count={Count}");

    [HttpGet("browse")]
    public IActionResult Browse([FromQuery] string? path, [FromQuery] string? sort = "name", [FromQuery] string? direction = "asc", [FromQuery] int page = 1, [FromQuery] int pageSize = 100, [FromQuery] string? snapshot = null)
    {
        return Run("browse", () => files.Browse(path, sort, direction, page, pageSize, HttpContext.RequestAborted, snapshot));
    }

    [HttpGet("search")]
    public IActionResult Search([FromQuery] string? path, [FromQuery] string? q = "", [FromQuery] string? sort = "name", [FromQuery] string? direction = "asc", [FromQuery] int page = 1, [FromQuery] int pageSize = 100, [FromQuery] bool skipGenerated = true, [FromQuery] string? snapshot = null)
    {
        var query = q?.Trim() ?? string.Empty;
        return Run(query.Length == 0 ? "browse" : "search", () => query.Length == 0
            ? files.Browse(path, sort, direction, page, pageSize, HttpContext.RequestAborted, snapshot)
            : files.Search(path, query, sort, direction, page, pageSize, skipGenerated, snapshot, HttpContext.RequestAborted));
    }

    [HttpPost("upload")]
    [Consumes("multipart/form-data")]
    [DisableFormValueModelBinding]
    public async Task<IActionResult> Upload([FromQuery] string? path)
    {
        var started = diagnostics.Enabled ? Stopwatch.GetTimestamp() : 0;
        var outcome = "success";
        long bytes = 0;
        try
        {
            var result = await files.UploadAsync(path, Request, HttpContext.RequestAborted);
            bytes = result.Entry.SizeBytes ?? 0;
            return StatusCode(StatusCodes.Status201Created, result.Entry);
        }
        catch (FileBrowserException exception)
        {
            outcome = exception.StatusCode is StatusCodes.Status400BadRequest or StatusCodes.Status403Forbidden ? "path-rejected" : "rejected";
            return Problem(statusCode: exception.StatusCode, title: exception.Title, detail: exception.Message);
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            outcome = "failed";
            return Problem(statusCode: StatusCodes.Status403Forbidden, title: "Directory is not writable", detail: "The file cannot be saved in this directory.");
        }
        catch (IOException)
        {
            outcome = "failed";
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Upload failed", detail: "The file could not be saved.");
        }
        finally
        {
            if (outcome == "failed") LogFailure("upload");
            LogOperation("upload", outcome, started, bytes, bytes > 0 ? 1 : 0);
        }
    }

    [HttpGet("download")]
    public IActionResult Download([FromQuery] string? path)
    {
        var started = diagnostics.Enabled ? Stopwatch.GetTimestamp() : 0;
        var outcome = "prepared";
        try
        {
            var filePath = files.ResolveDownload(path);
            var provider = new FileExtensionContentTypeProvider();
            if (!provider.TryGetContentType(filePath, out var contentType)) contentType = "application/octet-stream";
            return PhysicalFile(filePath, contentType, Path.GetFileName(filePath), enableRangeProcessing: true);
        }
        catch (FileBrowserException exception)
        {
            outcome = exception.StatusCode is StatusCodes.Status400BadRequest or StatusCodes.Status403Forbidden ? "path-rejected" : "rejected";
            return Problem(statusCode: exception.StatusCode, title: exception.Title, detail: exception.Message);
        }
        catch (UnauthorizedAccessException)
        {
            outcome = "failed";
            return Problem(statusCode: StatusCodes.Status403Forbidden, title: "File is not accessible", detail: "The requested file cannot be read.");
        }
        catch (IOException)
        {
            outcome = "failed";
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "File is unavailable", detail: "The requested file changed or became unavailable.");
        }
        finally
        {
            if (outcome == "failed") LogFailure("download");
            LogOperation("download-prepare", outcome, started);
        }
    }

    [HttpPost("{operation:regex(^(delete|move|copy)$)}")]
    public async Task<IActionResult> Change(string operation, [FromBody] FileChangeRequest request)
    {
        try
        {
            await files.ChangeAsync(operation, request.Source, request.Destination, HttpContext.RequestAborted);
            return NoContent();
        }
        catch (FileBrowserException exception)
        {
            return Problem(statusCode: exception.StatusCode, title: exception.Title, detail: exception.Message);
        }
        catch (UnauthorizedAccessException)
        {
            return Problem(statusCode: 403, title: "Operation denied", detail: "Access was denied. A delete may have partially completed; refresh before retrying.");
        }
        catch (IOException)
        {
            return Problem(statusCode: 409, title: "Operation failed", detail: "The filesystem changed or is unavailable. A delete may have partially completed; refresh before retrying.");
        }
    }

    private IActionResult Run(string operation, Func<FileListResponse> action)
    {
        var started = diagnostics.Enabled ? Stopwatch.GetTimestamp() : 0;
        var outcome = "success";
        try
        {
            var result = action();
            if (result.Warnings.Count > 0)
            {
                LogIncompleteResults(logger,
                    operation,
                    HttpContext.Items["CorrelationId"],
                    result.Warnings.Sum(warning => warning.Count), null);
            }
            LogOperation(operation, outcome, started, result.Summary.TotalFileBytes, result.Summary.FileCount + result.Summary.FolderCount);
            return Ok(result);
        }
        catch (FileBrowserException exception)
        {
            outcome = exception.StatusCode is StatusCodes.Status400BadRequest or StatusCodes.Status403Forbidden ? "path-rejected" : "rejected";
            return Problem(statusCode: exception.StatusCode, title: exception.Title, detail: exception.Message);
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            outcome = "failed";
            return Problem(statusCode: StatusCodes.Status403Forbidden, title: "Directory is not accessible", detail: "The requested directory cannot be read.");
        }
        catch (IOException)
        {
            outcome = "failed";
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Filesystem operation failed", detail: "The requested operation could not be completed.");
        }
        finally
        {
            if (outcome == "failed") LogFailure(operation);
            if (outcome != "success") LogOperation(operation, outcome, started);
        }
    }

    private void LogFailure(string operation)
    {
        LogFailedOperation(logger,
            operation,
            HttpContext.Items["CorrelationId"], null);
    }

    private void LogOperation(string operation, string outcome, long started, long bytes = 0, int count = 0)
    {
        if (!diagnostics.Enabled) return;
        LogCompletedOperation(logger,
            operation,
            outcome,
            HttpContext.Items["CorrelationId"],
            Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            bytes,
            count, null);
    }
}

[ApiController]
[Route("api/config")]
public sealed class ConfigurationController(DiagnosticsSettings diagnostics) : ControllerBase
{
    [HttpGet]
    public ActionResult<DiagnosticsResponse> Get() => Ok(new DiagnosticsResponse(diagnostics.Enabled));
}
