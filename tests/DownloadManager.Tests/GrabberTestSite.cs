using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Tests;

/// <summary>A resource of the test site, for one host and one path (with its query).</summary>
public sealed class SiteResource
{
    public byte[] Body { get; set; } = [];
    public string ContentType { get; set; } = "text/html; charset=utf-8";
    public int Status { get; set; } = 200;
    public string? ETag { get; set; }
    public string? LastModified { get; set; }
    public string? Location { get; set; }
    public string? ContentDisposition { get; set; }

    /// <summary>Sends the headers and part of the body, then nothing more (the connection stays open).</summary>
    public bool Stall { get; set; }

    /// <summary>The first N requests get 429 with this Retry-After (seconds).</summary>
    public int BusyRequests { get; set; }
    public string RetryAfter { get; set; } = "1";
    internal int Busy;

    public static SiteResource Html(string html) => new() { Body = Encoding.UTF8.GetBytes(html) };

    public static SiteResource File(int size, string type = "application/octet-stream", string? etag = null, int seed = 1)
    {
        var body = new byte[size];
        new Random(seed).NextBytes(body);
        return new SiteResource { Body = body, ContentType = type, ETag = etag };
    }
}

public sealed record SiteRequest(string Host, string Path, string Method, TimeSpan At, string UserAgent, string Cookie, string IfNoneMatch, int Status);

/// <summary>
/// A small local website for the grabber tests: several made-up hosts (site.test, cdn.test, other.test...) served by one
/// local server, chosen by the Host header. The test client connects every host name to it.
/// </summary>
public sealed class GrabberTestSite : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, SiteResource> _resources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private GrabberTestSite(WebApplication app) => _app = app;

    public int Port { get; private set; }
    public ConcurrentQueue<SiteRequest> Requests { get; } = new();

    public static async Task<GrabberTestSite> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var site = new GrabberTestSite(app);
        app.Run(site.HandleAsync);
        await app.StartAsync();
        site.Port = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;
        return site;
    }

    /// <summary>Serves <paramref name="resource"/> at "http://host/pathAndQuery".</summary>
    public SiteResource Set(string host, string pathAndQuery, SiteResource resource)
    {
        _resources[$"{host}{pathAndQuery}"] = resource;
        return resource;
    }

    public SiteResource Page(string pathAndQuery, string html, string host = "site.test") => Set(host, pathAndQuery, SiteResource.Html(html));

    public void Remove(string host, string pathAndQuery) => _resources.TryRemove($"{host}{pathAndQuery}", out _);

    public IEnumerable<SiteRequest> To(string path, string host = "site.test") => Requests.Where(r => r.Host == host && r.Path == path);

    private async Task HandleAsync(HttpContext ctx)
    {
        var host = ctx.Request.Host.Host;
        var path = ctx.Request.Path.Value + ctx.Request.QueryString.Value;
        var status = 404;
        try
        {
            if (!_resources.TryGetValue($"{host}{path}", out var resource))
            {
                ctx.Response.StatusCode = 404;
                return;
            }
            if (resource.BusyRequests > 0 && Interlocked.Increment(ref resource.Busy) <= resource.BusyRequests)
            {
                status = ctx.Response.StatusCode = 429;
                ctx.Response.Headers.RetryAfter = resource.RetryAfter;
                return;
            }
            if (resource.Location is { } location)
            {
                status = ctx.Response.StatusCode = resource.Status is 301 or 302 or 307 or 308 ? resource.Status : 302;
                ctx.Response.Headers.Location = location;
                return;
            }
            if (resource.ETag is { } etag && ctx.Request.Headers.IfNoneMatch.ToString() == etag)
            {
                status = ctx.Response.StatusCode = 304;
                ctx.Response.Headers.ETag = etag;
                return;
            }
            status = ctx.Response.StatusCode = resource.Status;
            ctx.Response.ContentType = resource.ContentType;
            if (resource.ETag is not null)
                ctx.Response.Headers.ETag = resource.ETag;
            if (resource.LastModified is not null)
                ctx.Response.Headers.LastModified = resource.LastModified;
            if (resource.ContentDisposition is not null)
                ctx.Response.Headers.ContentDisposition = resource.ContentDisposition;
            ctx.Response.ContentLength = resource.Body.Length;
            if (resource.Stall && !HttpMethods.IsHead(ctx.Request.Method))
            {
                await ctx.Response.Body.WriteAsync(resource.Body.AsMemory(0, Math.Min(10, resource.Body.Length)));
                await ctx.Response.Body.FlushAsync();
                await Task.Delay(Timeout.Infinite, ctx.RequestAborted).ContinueWith(_ => { });
                return;
            }
            if (!HttpMethods.IsHead(ctx.Request.Method))
                await ctx.Response.Body.WriteAsync(resource.Body);
        }
        finally
        {
            Requests.Enqueue(new SiteRequest(host, path, ctx.Request.Method, _clock.Elapsed, ctx.Request.Headers.UserAgent.ToString(),
                ctx.Request.Headers.Cookie.ToString(), ctx.Request.Headers.IfNoneMatch.ToString(), status));
        }
    }

    /// <summary>A client that reaches every host name at this server and, like the grabber's own, does not follow redirects.</summary>
    public HttpClient CreateClient(CookieContainer cookies)
    {
        var port = Port;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            CookieContainer = cookies,
            ConnectCallback = async (context, ct) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                await socket.ConnectAsync(IPAddress.Loopback, port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            },
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
