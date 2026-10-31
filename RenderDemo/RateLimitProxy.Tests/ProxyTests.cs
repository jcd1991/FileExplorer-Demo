using System.Net;
using System.Text;
using FileExplorerDemo;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

public sealed class ProxyTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Forwards_exact_body_and_one_content_type(bool multipart, bool knownLength)
    {
        var bytes = Encoding.UTF8.GetBytes(multipart
            ? "--fixture\r\nContent-Disposition: form-data; name=\"file\"; filename=\"sample.txt\"\r\nContent-Type: text/plain\r\n\r\nfixture bytes\r\n--fixture--\r\n"
            : "{\"name\":\"fixture\"}");
        var type = multipart ? "multipart/form-data; boundary=fixture" : "application/json";
        await using var harness = await Harness.StartAsync(async context =>
        {
            Assert.Equal(type, context.Request.ContentType);
            Assert.Single(context.Request.Headers.ContentType);
            using var received = new MemoryStream();
            await context.Request.Body.CopyToAsync(received);
            Assert.Equal(bytes, received.ToArray());
            context.Response.StatusCode = 204;
        });
        // TestServer's HttpClient buffers unknown-length content. Set the incoming HTTP/2
        // context directly so this regression genuinely exercises a missing length header.
        using var body = new MemoryStream(bytes);
        var response = await harness.SendBodyAsync(body, type, knownLength ? bytes.Length : null);
        Assert.Equal(204, response.Response.StatusCode);
        var incoming = await harness.Incoming.Task;
        Assert.Equal("HTTP/2", incoming.Protocol);
        Assert.Equal(knownLength ? bytes.Length : (long?)null, incoming.ContentLength);
        Assert.False(incoming.TransferEncoding);
    }

    [Fact]
    public async Task Header_deadline_releases_all_four_api_slots()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        await using var harness = await Harness.StartAsync(async context =>
        {
            if (context.Request.Path == "/api/stall")
            {
                if (Interlocked.Increment(ref count) == 4) started.TrySetResult();
                await Task.Delay(Timeout.Infinite, context.RequestAborted);
            }
            else context.Response.StatusCode = 204;
        }, TimeSpan.FromSeconds(2));
        var pending = Enumerable.Range(0, 4).Select(_ => harness.Client.GetAsync("/api/stall")).ToArray();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var busy = await harness.Client.GetAsync("/api/probe");
        Assert.Equal(HttpStatusCode.TooManyRequests, busy.StatusCode);
        foreach (var response in await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5)))
        {
            using (response) Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        }
        using var recovered = await harness.Client.GetAsync("/api/probe");
        Assert.Equal(HttpStatusCode.NoContent, recovered.StatusCode);
    }

    [Fact]
    public async Task Body_deadline_before_first_byte_returns_clean_504()
    {
        await using var harness = await Harness.StartAsync(async context =>
        {
            context.Response.ContentLength = 100;
            context.Response.ContentType = "application/octet-stream";
            await context.Response.StartAsync();
            await Task.Delay(Timeout.Infinite, context.RequestAborted);
        }, TimeSpan.FromMilliseconds(500));
        using var response = await harness.Client.GetAsync("/api/probe");
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("exceeded its time limit", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Body_deadline_after_first_byte_aborts_stream_and_releases_slot()
    {
        await using var harness = await Harness.StartAsync(async context =>
        {
            if (context.Request.Path == "/api/stall")
            {
                await context.Response.Body.WriteAsync("prefix"u8.ToArray());
                await context.Response.Body.FlushAsync();
                await Task.Delay(Timeout.Infinite, context.RequestAborted);
            }
            else context.Response.StatusCode = 204;
        }, TimeSpan.FromMilliseconds(500));
        using var response = await harness.Client.GetAsync("/api/stall", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var exception = await Record.ExceptionAsync(async () =>
            await response.Content.CopyToAsync(Stream.Null).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.NotNull(exception);
        Assert.IsNotType<TimeoutException>(exception);
        using var recovered = await harness.Client.GetAsync("/api/probe");
        Assert.Equal(HttpStatusCode.NoContent, recovered.StatusCode);
    }

    private sealed record Framing(string Protocol, long? ContentLength, bool TransferEncoding);

    private sealed class CaptureFraming(TaskCompletionSource<Framing> incoming) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                incoming.TrySetResult(new Framing(context.Request.Protocol, context.Request.ContentLength,
                    context.Request.Headers.ContainsKey("Transfer-Encoding")));
                await continuation();
            });
            next(app);
        };
    }

    private sealed class Harness(WebApplication upstream, WebApplicationFactory<Program> factory, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public Task<Microsoft.AspNetCore.Http.HttpContext> SendBodyAsync(Stream body, string contentType, long? length) =>
            factory.Server.SendAsync(context =>
            {
                context.Request.Method = "POST";
                context.Request.Path = "/api/probe";
                context.Request.Protocol = "HTTP/2";
                context.Request.ContentType = contentType;
                context.Request.ContentLength = length;
                context.Request.Body = body;
            });
        public TaskCompletionSource<Framing> Incoming { get; init; } = null!;

        public static async Task<Harness> StartAsync(Microsoft.AspNetCore.Http.RequestDelegate handler, TimeSpan? timeout = null)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, endpoint => endpoint.Protocols = HttpProtocols.Http1));
            var upstream = builder.Build();
            upstream.Run(handler);
            await upstream.StartAsync();
            var origin = new Uri(upstream.Urls.Single());
            var incoming = new TaskCompletionSource<Framing>(TaskCreationOptions.RunContinuationsAsynchronously);
            var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web => web.ConfigureServices(services =>
            {
                services.AddSingleton<IStartupFilter>(new CaptureFraming(incoming));
                services.RemoveAll<DemoForwarder>();
                services.AddSingleton(provider => new DemoForwarder(provider.GetRequiredService<HttpClient>(), origin, timeout ?? TimeSpan.FromSeconds(5)));
            }));
            return new Harness(upstream, factory, factory.CreateClient()) { Incoming = incoming };
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await factory.DisposeAsync();
            await upstream.DisposeAsync();
        }
    }
}
