using Baton.Core;
using Baton.Core.Net;
using Baton.Core.Security;

namespace Baton.Tests;

/// <summary>A6: a download an agent asked for (PublicOnly) never uses the logins the user saved.</summary>
public sealed class AgentLoginsTests : IAsyncLifetime
{
    private TestFileServer _server = null!;

    public async Task InitializeAsync() => _server = await TestFileServer.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private NetworkContext Network(Uri url)
    {
        var credentials = new CredentialStore(null);
        credentials.Save(new SiteCredential { Scheme = "http", Host = url.Host, Port = url.Port, Username = "dario", Password = "pw-digest-6" }, remember: false);
        return new NetworkContext(new AppSettings { ProxyMode = ProxyMode.None }, credentials, new CertificateTrust(null));
    }

    [Fact]
    public async Task SavedLogin_IsUsedForTheUser_ButNotForAnAgentClient()
    {
        var url = _server.Add("secret.bin", new ServedFile { Content = TestFileServer.RandomBytes(500, 6), DigestLogin = ("dario", "pw-digest-6") });
        var network = Network(new Uri(url));

        using (var own = network.CreateHttpClient())
            Assert.Equal(System.Net.HttpStatusCode.OK, (await own.GetAsync(url)).StatusCode);

        _server.Requests.Clear();
        using var agent = network.CreateHttpClient(useSavedLogins: false);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await agent.GetAsync(url)).StatusCode);
        Assert.All(_server.Requests, r => Assert.Equal("", r.Authorization));
    }

    [Fact]
    public void AnAgentTransport_DoesNotShareTheClientThatHoldsSavedLogins()
    {
        var url = "http://files.example.test/a.bin";
        var network = Network(new Uri(url));
        var shared = network.SharedHttp;

        using var user = network.CreateTransport(new DownloadRecord { Url = url }, shared);
        using var agent = network.CreateTransport(new DownloadRecord { Url = url, Context = new RequestContext(null, null, []) { PublicOnly = true } }, shared);

        Assert.False(((HttpTransport)user).OwnsClient);   // the shared client, which holds the saved logins
        Assert.True(((HttpTransport)agent).OwnsClient);   // a client of its own, without them
    }
}
