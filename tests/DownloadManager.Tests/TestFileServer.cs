using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Tests;

public sealed class ServedFile
{
    public required byte[] Content { get; set; }
    public bool SupportsRanges { get; set; } = true;
    public string? ETag { get; set; } = "\"v1\"";
    public bool SendContentLength { get; set; } = true;
    public string? ContentDisposition { get; set; }
    public string ContentType { get; set; } = "application/octet-stream";
    public DateTimeOffset? LastModified { get; set; }

    /// <summary>Bytes per second for a request starting at the given offset; 0 = unthrottled.</summary>
    public Func<long, int> Throttle { get; set; } = _ => 0;

    /// <summary>The first <see cref="FailRequests"/> responses are aborted after this many bytes.</summary>
    public int FailAfterBytes { get; set; }
    public int FailRequests { get; set; }
    internal int Failed;

    /// <summary>The first N requests are refused with 429 Too Many Requests.</summary>
    public int RejectRequests { get; set; }
    internal int Rejected;

    /// <summary>Requests beyond this many simultaneous ones get 429; 0 = no cap.</summary>
    public int MaxConcurrent { get; set; }
    internal int InFlight;

    /// <summary>Requests this returns false for are refused with 403, like a site that wants its session cookie.</summary>
    public Func<HttpRequest, bool>? Authorize { get; set; }

    public string Sha256 => Convert.ToHexString(SHA256.HashData(Content));
}

public sealed record LoggedRequest(string Path, long? From, long? To, bool HasIfRange);

/// <summary>Local HTTP server with single-range, If-Range, throttling and fault injection.</summary>
public sealed class TestFileServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, ServedFile> _files = new();

    private TestFileServer(WebApplication app) => _app = app;

    public string BaseUrl { get; private set; } = "";
    public ConcurrentQueue<LoggedRequest> Requests { get; } = new();

    public static async Task<TestFileServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var server = new TestFileServer(app);
        app.MapGet("/files/{name}", server.HandleAsync);
        await app.StartAsync();
        server.BaseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return server;
    }

    public string Add(string name, ServedFile file)
    {
        _files[name] = file;
        return $"{BaseUrl}/files/{name}";
    }

    public static byte[] RandomBytes(int length, int seed = 42)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private async Task HandleAsync(HttpContext ctx, string name)
    {
        if (!_files.TryGetValue(name, out var file))
        {
            ctx.Response.StatusCode = 404;
            return;
        }

        RangeItemHeaderValue? requested = null;
        if (RangeHeaderValue.TryParse(ctx.Request.Headers.Range.ToString(), out var range) && range.Ranges.Count == 1)
            requested = range.Ranges.First();
        Requests.Enqueue(new LoggedRequest(name, requested?.From, requested?.To, !string.IsNullOrEmpty(ctx.Request.Headers.IfRange.ToString())));

        if (file.Authorize is { } authorize && !authorize(ctx.Request))
        {
            ctx.Response.StatusCode = 403;
            return;
        }

        if (file.RejectRequests > 0 && Interlocked.Increment(ref file.Rejected) <= file.RejectRequests)
        {
            ctx.Response.StatusCode = 429;
            return;
        }

        try
        {
            if (file.MaxConcurrent > 0 && Interlocked.Increment(ref file.InFlight) > file.MaxConcurrent)
            {
                ctx.Response.StatusCode = 429;
                return;
            }
            await SendBodyAsync(ctx, file);
        }
        finally
        {
            if (file.MaxConcurrent > 0)
                Interlocked.Decrement(ref file.InFlight);
        }
    }

    private static async Task SendBodyAsync(HttpContext ctx, ServedFile file)
    {
        var content = file.Content;
        var length = content.LongLength;
        long from = 0, to = length - 1;
        var partial = false;
        var ifRange = ctx.Request.Headers.IfRange.ToString();
        var ifRangeOk = string.IsNullOrEmpty(ifRange) || ifRange == file.ETag
                        || (file.LastModified is { } lm && ifRange == lm.ToString("R"));
        RangeItemHeaderValue? requested = null;
        if (RangeHeaderValue.TryParse(ctx.Request.Headers.Range.ToString(), out var range) && range.Ranges.Count == 1)
            requested = range.Ranges.First();

        if (file.SupportsRanges && requested is not null && ifRangeOk)
        {
            from = requested.From ?? 0;
            to = Math.Min(requested.To ?? length - 1, length - 1);
            if (from >= length)
            {
                ctx.Response.StatusCode = 416;
                ctx.Response.Headers.ContentRange = $"bytes */{length}";
                return;
            }
            partial = true;
        }

        if (file.ETag is not null)
            ctx.Response.Headers.ETag = file.ETag;
        if (file.SupportsRanges)
            ctx.Response.Headers.AcceptRanges = "bytes";
        if (file.ContentDisposition is not null)
            ctx.Response.Headers.ContentDisposition = file.ContentDisposition;
        ctx.Response.ContentType = file.ContentType;
        if (file.LastModified is { } modified)
            ctx.Response.Headers.LastModified = modified.ToString("R");
        ctx.Response.StatusCode = partial ? 206 : 200;
        if (partial)
            ctx.Response.Headers.ContentRange = $"bytes {from}-{to}/{length}";
        if (file.SendContentLength)
            ctx.Response.ContentLength = to - from + 1;

        var fail = file.FailRequests > 0 && Interlocked.Increment(ref file.Failed) <= file.FailRequests;
        var bps = file.Throttle(from);
        const int chunk = 16 * 1024;
        long sent = 0;
        for (var pos = from; pos <= to; pos += chunk)
        {
            var count = (int)Math.Min(chunk, to - pos + 1);
            if (fail && sent + count > file.FailAfterBytes)
            {
                await ctx.Response.Body.FlushAsync();
                ctx.Abort();
                return;
            }
            try
            {
                await ctx.Response.Body.WriteAsync(content.AsMemory((int)pos, count), ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            }
            catch (Exception) when (ctx.RequestAborted.IsCancellationRequested)
            {
                return; // client closed the connection (e.g. range stolen or paused)
            }
            sent += count;
            if (bps > 0)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds((double)count / bps), ctx.RequestAborted);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
