using FileExplorerDemo;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;

const int apiRetryAfterSeconds = 30;
const int uploadRetryAfterSeconds = 300;
const int mutationRetryAfterSeconds = 60;
const int maxConcurrentApiRequests = 4;
const long maxDemoRequestBodyBytes = 11L * 1024 * 1024;
const long defaultDemoUploadBytes = 10L * 1024 * 1024;
const long bytesPerMiB = 1024L * 1024;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = maxDemoRequestBodyBytes);
builder.Services.AddCors();
builder.Services.AddSingleton(new HttpClient(new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    AutomaticDecompression = DecompressionMethods.None,
    UseCookies = false,
    MaxConnectionsPerServer = 8
})
{
    // DemoForwarder applies one deadline to request sending, headers, and body streaming.
    Timeout = Timeout.InfiniteTimeSpan
});
builder.Services.AddSingleton(provider => new DemoForwarder(
    provider.GetRequiredService<HttpClient>(), new Uri("http://127.0.0.1:10001/"), TimeSpan.FromSeconds(60)));
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
var uploadLimitMiB = (int)(builder.Configuration.GetValue("FileBrowser:MaxUploadBytes", defaultDemoUploadBytes) / bytesPerMiB);

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
            if (!await turnstileVerifier.IsValidAsync(token, context.RequestAborted))
            {
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

app.Run(context => context.RequestServices.GetRequiredService<DemoForwarder>().ForwardAsync(context));
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

    public async Task<bool> IsValidAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(secretKey) || string.IsNullOrWhiteSpace(token) || token.Length > 2048)
            return false;

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
            if (!response.IsSuccessStatusCode) return false;
            var validation = await response.Content.ReadFromJsonAsync<TurnstileValidation>(cancellationToken: timeout.Token);
            return validation is { Success: true, Action: ExpectedAction } &&
                validation.Hostname is not null && AllowedHostnames.Contains(validation.Hostname);
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private sealed record TurnstileValidation(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("hostname")] string? Hostname,
        [property: JsonPropertyName("action")] string? Action);
}

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

public partial class Program;
