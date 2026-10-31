using System.Net;
using System.Net.Http.Headers;
using System.Threading.RateLimiting;

const string backendOrigin = "http://127.0.0.1:10001/";
const int apiRetryAfterSeconds = 30;
const int uploadRetryAfterSeconds = 300;
const int mutationRetryAfterSeconds = 60;
const int maxConcurrentApiRequests = 4;
const long maxDemoRequestBodyBytes = 11L * 1024 * 1024;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = maxDemoRequestBodyBytes);
builder.Services.AddSingleton(new HttpClient(new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    AutomaticDecompression = DecompressionMethods.None,
    UseCookies = false,
    MaxConnectionsPerServer = 8
})
{
    Timeout = Timeout.InfiniteTimeSpan
});
builder.Services.AddSingleton(new DemoRateLimiters(
    new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
    {
        TokenLimit = 30,
        TokensPerPeriod = 30,
        ReplenishmentPeriod = TimeSpan.FromSeconds(30),
        AutoReplenishment = true,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        QueueLimit = 0
    }),
        new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
    {
        PermitLimit = 2,
        Window = TimeSpan.FromMinutes(5),
        AutoReplenishment = true,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        QueueLimit = 0
    }),
    new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
    {
        PermitLimit = 20,
        Window = TimeSpan.FromMinutes(1),
        AutoReplenishment = true,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        QueueLimit = 0
    })));
builder.Services.AddSingleton(new SemaphoreSlim(maxConcurrentApiRequests, maxConcurrentApiRequests));

var app = builder.Build();
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    if (!path.StartsWithSegments("/api") || HttpMethods.IsOptions(context.Request.Method) ||
        MatchesPath(path, "/api/config"))
    {
        await next();
        return;
    }

    var concurrencyGate = context.RequestServices.GetRequiredService<SemaphoreSlim>();
    if (!concurrencyGate.Wait(0))
    {
        await RejectAsync(context, apiRetryAfterSeconds, "The demo is busy. Try again shortly.");
        return;
    }

    try
    {
        var limiters = context.RequestServices.GetRequiredService<DemoRateLimiters>();
        using var apiLease = limiters.Api.AttemptAcquire();
        if (!apiLease.IsAcquired)
        {
            await RejectAsync(context, apiRetryAfterSeconds, "The demo API is temporarily rate limited.");
            return;
        }

        if (HttpMethods.IsPost(context.Request.Method) && IsFileCreation(path))
        {
            using var creationLease = limiters.Creation.AttemptAcquire();
            if (!creationLease.IsAcquired)
            {
                await RejectAsync(context, uploadRetryAfterSeconds, "The demo accepts up to two uploads or copies every five minutes.");
                return;
            }
        }
        else if (HttpMethods.IsPost(context.Request.Method) && IsFileMutation(path))
        {
            using var mutationLease = limiters.Mutation.AttemptAcquire();
            if (!mutationLease.IsAcquired)
            {
                await RejectAsync(context, mutationRetryAfterSeconds, "The demo accepts up to twenty file changes per minute.");
                return;
            }
        }

        await next();
    }
    finally
    {
        concurrencyGate.Release();
    }
});

app.Run(ForwardAsync);
app.Run();

static bool IsFileMutation(PathString path) =>
    MatchesPath(path, "/api/files/delete") ||
    MatchesPath(path, "/api/files/move");

static bool IsFileCreation(PathString path) =>
    MatchesPath(path, "/api/files/upload") ||
    MatchesPath(path, "/api/files/copy");

static bool MatchesPath(PathString path, string expectedPath) =>
    string.Equals(path.Value?.TrimEnd('/'), expectedPath, StringComparison.OrdinalIgnoreCase);

static async Task RejectAsync(HttpContext context, int retryAfterSeconds, string detail)
{
    context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
    context.Response.Headers.RetryAfter = retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
    await context.Response.WriteAsJsonAsync(new
    {
        title = "Too many requests",
        status = StatusCodes.Status429TooManyRequests,
        detail
    }, context.RequestAborted);
}

static async Task ForwardAsync(HttpContext context)
{
    var client = context.RequestServices.GetRequiredService<HttpClient>();
    var targetUri = new Uri(new Uri(backendOrigin), $"{context.Request.Path}{context.Request.QueryString}");
    using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), targetUri);

    if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
    {
        var content = new StreamContent(context.Request.Body);
        if (context.Request.ContentLength is long contentLength)
            content.Headers.ContentLength = contentLength;
        if (MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var mediaType))
            content.Headers.ContentType = mediaType;
        request.Content = content;
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
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        context.Response.StatusCode = (int)response.StatusCode;

        foreach (var header in response.Headers)
            if (!IsHopByHopHeader(header.Key)) context.Response.Headers[header.Key] = header.Value.ToArray();

        foreach (var header in response.Content.Headers)
            if (!IsHopByHopHeader(header.Key)) context.Response.Headers[header.Key] = header.Value.ToArray();

        context.Response.Headers.Remove("transfer-encoding");
        await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }
    catch (HttpRequestException) when (!context.RequestAborted.IsCancellationRequested)
    {
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            await context.Response.WriteAsJsonAsync(new
            {
                title = "Demo backend unavailable",
                status = StatusCodes.Status502BadGateway,
                detail = "The demo backend is starting or temporarily unavailable. Try again shortly."
            }, context.RequestAborted);
        }
    }
}

static bool IsHopByHopHeader(string name) =>
    name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
    name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase) ||
    name.Equals("Proxy-Authenticate", StringComparison.OrdinalIgnoreCase) ||
    name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
    name.Equals("TE", StringComparison.OrdinalIgnoreCase) ||
    name.Equals("Trailer", StringComparison.OrdinalIgnoreCase) ||
    name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
    name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase);

sealed class DemoRateLimiters(
    TokenBucketRateLimiter api,
    FixedWindowRateLimiter creation,
    FixedWindowRateLimiter mutation) : IDisposable
{
    public TokenBucketRateLimiter Api { get; } = api;
    public FixedWindowRateLimiter Creation { get; } = creation;
    public FixedWindowRateLimiter Mutation { get; } = mutation;

    public void Dispose()
    {
        Api.Dispose();
        Creation.Dispose();
        Mutation.Dispose();
    }
}
