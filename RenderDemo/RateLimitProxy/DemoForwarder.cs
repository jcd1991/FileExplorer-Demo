namespace FileExplorerDemo;

public sealed class DemoForwarder(HttpClient client, Uri backendOrigin, TimeSpan requestTimeout)
{
    public async Task ForwardAsync(HttpContext context)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(requestTimeout);
        var targetUri = new Uri(backendOrigin, $"{context.Request.Path}{context.Request.QueryString}");
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), targetUri);

        // HTTP/2 bodies may have a content type without Content-Length or Transfer-Encoding.
        var hasRequestBody = context.Request.ContentLength is > 0 ||
            context.Request.Headers.ContainsKey("Transfer-Encoding") ||
            context.Request.ContentType is not null;
        if (hasRequestBody)
        {
            request.Content = new StreamContent(context.Request.Body);
        }

        foreach (var header in context.Request.Headers)
        {
            if (IsHopByHopHeader(header.Key) || header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }

        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            context.Response.StatusCode = (int)response.StatusCode;

            foreach (var header in response.Headers)
                if (!IsHopByHopHeader(header.Key) && !IsCorsHeader(header.Key)) context.Response.Headers[header.Key] = header.Value.ToArray();

            foreach (var header in response.Content.Headers)
                if (!IsHopByHopHeader(header.Key) && !IsCorsHeader(header.Key)) context.Response.Headers[header.Key] = header.Value.ToArray();

            context.Response.Headers.Remove("transfer-encoding");
            await response.Content.CopyToAsync(context.Response.Body, deadline.Token);
        }
        catch (Exception exception) when (deadline.IsCancellationRequested &&
            !context.RequestAborted.IsCancellationRequested &&
            exception is OperationCanceledException or IOException or HttpRequestException)
        {
            await FailAsync(context, StatusCodes.Status504GatewayTimeout, "Demo backend timed out",
                "The demo request exceeded its time limit. Refresh before retrying a file change; it may have completed.");
        }
        catch (HttpRequestException) when (!context.RequestAborted.IsCancellationRequested)
        {
            await FailAsync(context, StatusCodes.Status502BadGateway, "Demo backend unavailable",
                "The demo backend is starting or temporarily unavailable. Try again shortly.");
        }
    }

    private static async Task FailAsync(HttpContext context, int statusCode, string title, string detail)
    {
        if (context.Response.HasStarted)
        {
            context.Abort();
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        await context.Response.WriteAsJsonAsync(new { title, status = statusCode, detail }, context.RequestAborted);
    }

    private static bool IsHopByHopHeader(string name) =>
        name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Proxy-Authenticate", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("TE", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Trailer", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase);

    private static bool IsCorsHeader(string name) => name.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase);
}
