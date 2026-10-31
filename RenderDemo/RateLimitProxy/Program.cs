using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;

const string backendOrigin = "http://127.0.0.1:10001/";
const int apiRetryAfterSeconds = 30;
const int uploadRetryAfterSeconds = 300;
const int mutationRetryAfterSeconds = 60;
const int maxConcurrentApiRequests = 4;
const long defaultDemoUploadBytes = 100L * 1024 * 1024;
const long multipartOverheadBytes = 1024L * 1024;
const long bytesPerMiB = 1024L * 1024;

var builder = WebApplication.CreateBuilder(args);
var configuredUploadBytes = builder.Configuration.GetValue("FileBrowser:MaxUploadBytes", defaultDemoUploadBytes);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = configuredUploadBytes + multipartOverheadBytes);
builder.Services.AddCors();
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
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
var turnstileSiteKey = builder.Configuration["Turnstile:SiteKey"]?.Trim() ?? string.Empty;
var turnstileSecretKey = builder.Configuration["Turnstile:SecretKey"] ?? string.Empty;
var turnstileReady = !string.IsNullOrWhiteSpace(turnstileSiteKey) && !string.IsNullOrWhiteSpace(turnstileSecretKey);
var turnstileVerifier = new TurnstileVerifier(turnstileSecretKey);
var uploadLimitMiB = (int)(configuredUploadBytes / bytesPerMiB);

var app = builder.Build();
app.UseCors(policy => policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod());
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    if (HttpMethods.IsGet(context.Request.Method) && MatchesPath(path, "/api/demo-config"))
    {
        await context.Response.WriteAsJsonAsync(new
        {
            uploadLimitMiB,
            requestBurst = 30,
            requestRefillCount = 30,
            requestRefillSeconds = 30,
            maxConcurrentRequests = maxConcurrentApiRequests,
            creationsPerWindow = 2,
            creationWindowMinutes = 5,
            mutationsPerWindow = 20,
            idleSleepMinutes = 15,
            turnstileReady,
            turnstileSiteKey = turnstileReady ? turnstileSiteKey : string.Empty
        }, context.RequestAborted);
        return;
    }

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

        if (HttpMethods.IsPost(context.Request.Method) && IsProtectedMutation(path))
        {
            if (!turnstileReady)
            {
                await RejectAsync(context, 0, "The demo security check is not configured. File changes are temporarily disabled.", StatusCodes.Status503ServiceUnavailable);
                return;
            }

            var token = context.Request.Headers["X-Turnstile-Token"].ToString();
            var verification = await turnstileVerifier.ValidateAsync(token, context.RequestAborted);
            if (!verification.IsValid)
            {
                app.Logger.LogWarning(
                    "Turnstile rejected a file change. Reason={Reason}; ProviderErrorCodes={ErrorCodes}",
                    verification.Reason,
                    string.Join(",", verification.ErrorCodes));
                await RejectAsync(context, 0, "Complete the security check before changing files.", StatusCodes.Status403Forbidden);
                return;
            }
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

static bool IsProtectedMutation(PathString path) =>
    IsFileCreation(path) || IsFileMutation(path);

static bool IsFileCreation(PathString path) =>
    MatchesPath(path, "/api/files/upload") ||
    MatchesPath(path, "/api/files/copy");

static bool MatchesPath(PathString path, string expectedPath) =>
    string.Equals(path.Value?.TrimEnd('/'), expectedPath, StringComparison.OrdinalIgnoreCase);

static async Task RejectAsync(HttpContext context, int retryAfterSeconds, string detail, int statusCode = StatusCodes.Status429TooManyRequests)
{
    context.Response.StatusCode = statusCode;
    if (statusCode == StatusCodes.Status429TooManyRequests)
        context.Response.Headers.RetryAfter = retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
    await context.Response.WriteAsJsonAsync(new
    {
        title = statusCode switch
        {
            StatusCodes.Status403Forbidden => "Security check failed",
            StatusCodes.Status503ServiceUnavailable => "Security check unavailable",
            _ => "Too many requests"
        },
        status = statusCode,
        detail
    }, context.RequestAborted);
}

static async Task ForwardAsync(HttpContext context)
{
    var client = context.RequestServices.GetRequiredService<HttpClient>();
    var targetUri = new Uri(new Uri(backendOrigin), $"{context.Request.Path}{context.Request.QueryString}");
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
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        context.Response.StatusCode = (int)response.StatusCode;

        foreach (var header in response.Headers)
            if (!IsHopByHopHeader(header.Key) && !IsCorsHeader(header.Key)) context.Response.Headers[header.Key] = header.Value.ToArray();

        foreach (var header in response.Content.Headers)
            if (!IsHopByHopHeader(header.Key) && !IsCorsHeader(header.Key)) context.Response.Headers[header.Key] = header.Value.ToArray();

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

static bool IsCorsHeader(string name) => name.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase);

sealed class TurnstileVerifier(string secretKey)
{
    private const string SiteverifyUrl = "https://challenges.cloudflare.com/turnstile/v0/siteverify";
    private const string ExpectedAction = "file_mutation";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(6) };
    private static readonly HashSet<string> AllowedHostnames = new(StringComparer.OrdinalIgnoreCase)
    {
        "jcd1991.github.io",
        "file-explorer-demo-api.onrender.com"
    };

    public async Task<TurnstileVerificationResult> ValidateAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(secretKey)) return Failure("secret-not-configured");
        if (string.IsNullOrWhiteSpace(token)) return Failure("token-missing");
        if (token.Length > 2048) return Failure("token-too-large");

        using var content = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("secret", secretKey),
            new KeyValuePair<string, string>("response", token)
        ]);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));

        try
        {
            using var response = await Client.PostAsync(SiteverifyUrl, content, timeout.Token);
            if (!response.IsSuccessStatusCode) return Failure($"siteverify-http-{(int)response.StatusCode}");
            var validation = await response.Content.ReadFromJsonAsync<TurnstileValidation>(cancellationToken: timeout.Token);
            if (validation is null) return Failure("siteverify-empty-response");
            if (!validation.Success) return Failure("siteverify-rejected", validation.ErrorCodes);
            if (!string.Equals(validation.Action, ExpectedAction, StringComparison.Ordinal))
                return Failure("action-mismatch", validation.ErrorCodes);
            if (validation.Hostname is null || !AllowedHostnames.Contains(validation.Hostname))
                return Failure("hostname-not-allowed", validation.ErrorCodes);
            return new TurnstileVerificationResult(true, "verified", []);
        }
        catch (HttpRequestException)
        {
            return Failure("siteverify-network-error");
        }
        catch (JsonException)
        {
            return Failure("siteverify-invalid-json");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure("siteverify-timeout");
        }
    }

    private static TurnstileVerificationResult Failure(string reason, string[]? errorCodes = null) =>
        new(false, reason, errorCodes ?? []);

    private sealed record TurnstileValidation(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("hostname")] string? Hostname,
        [property: JsonPropertyName("action")] string? Action,
        [property: JsonPropertyName("error-codes")] string[]? ErrorCodes);
}

sealed record TurnstileVerificationResult(bool IsValid, string Reason, string[] ErrorCodes);

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
