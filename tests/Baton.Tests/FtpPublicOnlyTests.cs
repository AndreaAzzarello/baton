using System.Net;
using Baton.Core;
using Baton.Core.Net;
using Baton.Core.Security;

namespace Baton.Tests;

/// <summary>
/// FTP downloads asked for by an agent (<c>RequestContext.PublicOnly</c>): the private-network check is repeated when the
/// connection is made (DNS rebinding) and the data connection never follows an address a server names in PASV.
/// </summary>
public sealed class FtpPublicOnlyTests : IAsyncLifetime
{
    private readonly List<IAsyncDisposable> _servers = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var server in _servers)
            await server.DisposeAsync();
    }

    private FtpTestServer Server()
    {
        var server = new FtpTestServer();
        server.Files["pub/data.bin"] = new FtpTestFile { Content = TestFileServer.RandomBytes(50_000, 7) };
        _servers.Add(server);
        return server;
    }

    private static FtpTransport Transport(string url, bool publicOnly, Func<IPAddress, bool>? isBlocked = null,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null) =>
        new(new Uri(url), new NetworkCredential("anonymous", "anonymous@"), new AppSettings { TimeoutSeconds = 10, ProxyMode = ProxyMode.None },
            new CertificateTrust(null), null, publicOnly, isBlocked, resolve);

    private static readonly DownloadRecord Record = new() { Url = "ftp://x/", Directory = "." };

    [Fact]
    public async Task LocalAddress_IsRefused_BeforeAnyConnection()
    {
        var server = Server();
        using var transport = Transport(server.Url("pub/data.bin"), publicOnly: true);

        await Assert.ThrowsAsync<DownloadFailedException>(() => transport.ProbeAsync(CancellationToken.None));

        Assert.Equal(0, server.LoginCount);
    }

    [Fact]
    public async Task NameThatResolvesToPrivateAddress_IsRefused()
    {
        var server = Server();
        using var transport = Transport(server.Url("pub/data.bin", host: "files.example.test"), publicOnly: true,
            resolve: (_, _) => Task.FromResult(new[] { IPAddress.Parse("10.1.2.3") }));

        await Assert.ThrowsAsync<DownloadFailedException>(() => transport.ProbeAsync(CancellationToken.None));

        Assert.Equal(0, server.LoginCount);
    }

    [Fact]
    public async Task NameIsResolvedOnce_AndTheConnectionGoesToTheCheckedAddress()
    {
        var server = Server();
        var lookups = 0;
        // A rebinding name: public (here: allowed) for the first lookup, a private address for any later one.
        using var transport = Transport(server.Url("pub/data.bin", host: "files.example.test"), publicOnly: true,
            isBlocked: a => a.ToString() == "10.1.2.3",
            resolve: (_, _) => Task.FromResult(Interlocked.Increment(ref lookups) == 1
                ? new[] { IPAddress.Loopback }
                : new[] { IPAddress.Parse("10.1.2.3") }));

        var probe = await transport.ProbeAsync(CancellationToken.None);

        Assert.Equal(50_000, probe.TotalSize);
        Assert.Equal(1, lookups);
        Assert.Equal(1, server.LoginCount);
    }

    [Theory]
    [InlineData("10.9.8.7")]    // a private host named by a hostile server
    [InlineData("203.0.113.9")] // any other host: only the control connection's address is used
    public async Task PasvAddress_OfAServer_IsIgnored_AndTheDataComesFromTheControlAddress(string announced)
    {
        var server = Server();
        server.EpsvSupported = false; // only PASV is left
        server.PasvHost = announced;
        using var transport = Transport(server.Url("pub/data.bin"), publicOnly: true, isBlocked: _ => false);

        using var stream = await transport.OpenAsync(Record, 0, 49_999, CancellationToken.None);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);

        Assert.Equal(50_000, buffer.Length);
    }
}
