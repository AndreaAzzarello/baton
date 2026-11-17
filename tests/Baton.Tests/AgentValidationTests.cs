using System.Net;
using Baton.Core.Integration;
using Baton.Core;
using Baton.Core.Net;

namespace Baton.Tests;

/// <summary>A26: what the validator and the address guard refuse for agents, and for odd text.</summary>
public sealed class AgentValidationTests
{
    private static ChannelMessage Add(string origin, BrowserDownload download) => new(Messages.Add, download, Messages.ProtocolVersion, Origin: origin);

    private static readonly List<BrowserCookie> Cookie = [new BrowserCookie("sid", "x", "example.com", "/", true, true)];

    [Theory]
    [InlineData(Origins.Api)]
    [InlineData(Origins.Mcp)]
    public void Agents_CannotCarryCookiesOrSendForms(string origin)
    {
        Assert.Null(MessageValidator.Validate(Add(origin, new BrowserDownload("https://example.com/a.bin"))));
        Assert.NotNull(MessageValidator.Validate(Add(origin, new BrowserDownload("https://example.com/a.bin", Cookies: Cookie))));
        Assert.NotNull(MessageValidator.Validate(Add(origin, new BrowserDownload("https://example.com/a.bin", Method: "POST", Body: "a=1", BodyType: "application/x-www-form-urlencoded"))));
        Assert.NotNull(MessageValidator.Validate(Add(origin, new BrowserDownload("https://example.com/a.bin", Method: "POST"))));
    }

    [Fact]
    public void TheBrowserAndTheCommandLine_KeepTheirCookiesAndForms()
    {
        Assert.Null(MessageValidator.Validate(Add(Origins.Browser, new BrowserDownload("https://example.com/a.bin", Cookies: Cookie))));
        Assert.Null(MessageValidator.Validate(Add(Origins.Cli, new BrowserDownload("https://example.com/a.bin", Cookies: Cookie))));
    }

    [Fact]
    public void TheClipboardOrigin_IsNeverAcceptedOnThePipe()
    {
        Assert.Equal("Unknown origin.", MessageValidator.Validate(new ChannelMessage(Messages.List, Origin: Origins.Clipboard)));
        Assert.Null(MessageValidator.Validate(new ChannelMessage(Messages.List, Origin: Origins.Cli)));
    }

    [Fact]
    public void C1ControlCharacters_AreRefusedInUserAgentAndReferrer()
    {
        var c1 = "Mozilla\u0085 injected";
        Assert.NotNull(MessageValidator.Validate(Add(Origins.Cli, new BrowserDownload("https://example.com/a.bin", UserAgent: c1))));
        Assert.NotNull(MessageValidator.Validate(Add(Origins.Cli, new BrowserDownload("https://example.com/a.bin", Referrer: "https://example.com/\u009b"))));
        Assert.Null(MessageValidator.Validate(Add(Origins.Cli, new BrowserDownload("https://example.com/a.bin", UserAgent: "Mozilla/5.0 (àèì)"))));
    }

    [Theory]
    [InlineData("2001:0:4136:e378:8000:63bf:3fff:fdd2")]   // Teredo: hides a client's IPv4 address
    [InlineData("2001:0:0:0:0:0:0:1")]
    public void TeredoAddresses_AreNotPublic(string address) => Assert.True(UrlGuard.IsPrivate(IPAddress.Parse(address)));

    [Fact]
    public void OrdinaryPublicIPv6_StaysPublic()
    {
        Assert.False(UrlGuard.IsPrivate(IPAddress.Parse("2606:4700:4700::1111")));
        Assert.False(UrlGuard.IsPrivate(IPAddress.Parse("2001:4860:4860::8888")));   // 2001::/16 but not Teredo (2001:0000::/32)
    }
}
