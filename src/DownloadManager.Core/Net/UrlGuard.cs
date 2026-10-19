using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using DownloadManager.Core.Resources;

namespace DownloadManager.Core.Net;

/// <summary>
/// Limits the requests the app makes on behalf of a web page's video (manifests, playlists, segments, subtitles):
/// only http and https, and never this computer, the local network or link-local addresses unless the page itself is
/// on such a network. A page cannot use the app to reach a router or a local service the browser would have protected.
/// The host is checked before each request (after DNS resolution) and again when the connection is made, so a redirect
/// or a DNS answer that changes in between is caught too (<see cref="ConnectAsync"/>).
/// </summary>
public sealed class UrlGuard
{
    /// <summary>Set on a request whose connections must not reach a private address.</summary>
    public static readonly HttpRequestOptionsKey<bool> PublicOnly = new("DownloadManager.PublicOnly");

    /// <summary>No limit: the page is itself on this computer or the local network.</summary>
    public static readonly UrlGuard Open = new(allowPrivate: true);

    private readonly ConcurrentDictionary<string, bool> _privateHosts = new(StringComparer.OrdinalIgnoreCase);

    private UrlGuard(bool allowPrivate) => AllowsPrivate = allowPrivate;

    public bool AllowsPrivate { get; }

    /// <summary>The guard for requests made for a page at <paramref name="origin"/>.</summary>
    public static async Task<UrlGuard> ForOriginAsync(string? origin, CancellationToken ct) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var page) && page.Scheme is "http" or "https" && await IsPrivateHostAsync(page.IdnHost, ct)
            ? Open
            : new UrlGuard(allowPrivate: false);

    /// <summary>Throws a <see cref="DownloadFailedException"/> when the app may not request <paramref name="target"/>.</summary>
    public async Task CheckAsync(Uri target, CancellationToken ct)
    {
        if (target.Scheme is not ("http" or "https"))
            throw new DownloadFailedException(string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.MediaErrorScheme, target.Scheme));
        if (AllowsPrivate)
            return;
        if (!_privateHosts.TryGetValue(target.IdnHost, out var isPrivate))
            _privateHosts[target.IdnHost] = isPrivate = await IsPrivateHostAsync(target.IdnHost, ct);
        if (isPrivate)
            throw new DownloadFailedException(Strings.MediaErrorPrivate);
    }

    /// <summary>
    /// Checks the address and marks the request so its connection is checked again. A connection already open to the same
    /// host is reused without a new check; it can only exist for a host that passed this check or for an ordinary download.
    /// </summary>
    public async Task PrepareAsync(HttpRequestMessage request, CancellationToken ct)
    {
        await CheckAsync(request.RequestUri!, ct);
        if (!AllowsPrivate)
            request.Options.Set(PublicOnly, true);
    }

    /// <summary>A host name that is, or resolves to, this computer, a private or a link-local address.</summary>
    public static async Task<bool> IsPrivateHostAsync(string host, CancellationToken ct)
    {
        var name = host.Trim('[', ']');
        if (IPAddress.TryParse(name, out var literal))
            return IsPrivate(literal);
        if (name.Equals("localhost", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        try
        {
            return (await Dns.GetHostAddressesAsync(name, ct)).Any(IsPrivate);
        }
        catch (SocketException)
        {
            return false; // not resolvable here: the request itself will fail (and the connection is checked again)
        }
    }

    /// <summary>
    /// Loopback, private (RFC 1918, IPv6 unique local), link-local, shared (CGNAT), benchmarking, IETF protocol,
    /// unspecified and multicast addresses; for IPv6 forms that carry an IPv4 address (mapped, NAT64, 6to4,
    /// IPv4-compatible) the IPv4 address inside decides.
    /// </summary>
    public static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None))
            return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || (b[0] & 0xFE) == 0xFC) // fc00::/7
                return true;
            // NAT64 (64:ff9b::/96 and the local-use 64:ff9b:1::/48) and IPv4-compatible (::a.b.c.d): the last four bytes.
            if (b[0] == 0 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B
                && (b.AsSpan(4, 8).IndexOfAnyExcept((byte)0) < 0 || b[4] == 0 && b[5] == 1)
                || b.AsSpan(0, 12).IndexOfAnyExcept((byte)0) < 0)
                return IsPrivate(new IPAddress(b.AsSpan(12, 4)));
            // 6to4 (2002::/16): bytes 2 to 5.
            if (b[0] == 0x20 && b[1] == 0x02)
                return IsPrivate(new IPAddress(b.AsSpan(2, 4)));
            return false;
        }
        var v4 = address.GetAddressBytes();
        return v4[0] switch
        {
            0 or 10 or 127 => true,
            169 => v4[1] == 254,
            172 => v4[1] is >= 16 and <= 31,
            192 => v4[1] == 168 || v4[1] == 0 && v4[2] == 0, // 192.0.0.0/24: IETF protocol assignments
            198 => v4[1] is 18 or 19,                         // benchmarking networks, often routed internally
            100 => v4[1] is >= 64 and <= 127,                 // shared address space (carrier-grade NAT)
            >= 224 => true,                                   // multicast and reserved
            _ => false,
        };
    }

    /// <summary>
    /// The connection step of the app's HTTP handlers: a request marked <see cref="PublicOnly"/> only connects to public
    /// addresses (a redirect or a changed DNS answer cannot lead it to a private one). Through a proxy the address is the
    /// proxy's, which the user configured, and is not checked.
    /// </summary>
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, bool viaProxy, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
        if (!viaProxy && context.InitialRequestMessage.Options.TryGetValue(PublicOnly, out var publicOnly) && publicOnly)
        {
            addresses = addresses.Where(a => !IsPrivate(a)).ToArray();
            if (addresses.Length == 0)
                throw new HttpRequestException(Strings.MediaErrorPrivate);
        }
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
