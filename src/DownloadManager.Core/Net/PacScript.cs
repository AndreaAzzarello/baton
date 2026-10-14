using System.Net;
using System.Net.Sockets;
using Jint;

namespace DownloadManager.Core.Net;

/// <summary>
/// A proxy auto-configuration (PAC) file: JavaScript with a FindProxyForURL(url, host) function. It runs in Jint,
/// a sandboxed interpreter with no access to files or .NET, a time limit and a memory limit, because a PAC file
/// comes from the network. The standard helper functions are provided; DNS answers come from .NET.
/// </summary>
public sealed class PacScript
{
    // The helper functions every PAC file may call, written for this app. Network lookups are bridged to .NET.
    private const string Helpers = """
        function isPlainHostName(host) { return host.indexOf('.') < 0; }
        function dnsDomainIs(host, domain) {
            host = host.toLowerCase(); domain = domain.toLowerCase();
            return host.length >= domain.length && host.substring(host.length - domain.length) === domain;
        }
        function localHostOrDomainIs(host, hostdom) {
            host = host.toLowerCase(); hostdom = hostdom.toLowerCase();
            return host === hostdom || (host.indexOf('.') < 0 && hostdom.indexOf(host + '.') === 0);
        }
        function isResolvable(host) { return __dnsResolve(host) !== null; }
        function dnsResolve(host) { return __dnsResolve(host); }
        function myIpAddress() { return __myIpAddress(); }
        function isInNet(host, pattern, mask) { return __isInNet(host, pattern, mask); }
        function dnsDomainLevels(host) { return host.split('.').length - 1; }
        function shExpMatch(str, shexp) {
            var re = '^' + shexp.replace(/[.+^${}()|[\]\\]/g, '\\$&').replace(/\*/g, '.*').replace(/\?/g, '.') + '$';
            return new RegExp(re, 'i').test(str);
        }
        function weekdayRange(wd1, wd2, gmt) {
            var days = ['SUN','MON','TUE','WED','THU','FRI','SAT'];
            var now = new Date();
            var today = (gmt === 'GMT' || wd2 === 'GMT') ? now.getUTCDay() : now.getDay();
            var from = days.indexOf(wd1), to = (wd2 && wd2 !== 'GMT') ? days.indexOf(wd2) : from;
            return from <= to ? (today >= from && today <= to) : (today >= from || today <= to);
        }
        function timeRange() {
            var args = Array.prototype.slice.call(arguments);
            var gmt = args[args.length - 1] === 'GMT'; if (gmt) args.pop();
            var now = new Date(); var h = gmt ? now.getUTCHours() : now.getHours();
            if (args.length === 1) return h === args[0];
            return args[0] <= args[1] ? (h >= args[0] && h < args[1]) : (h >= args[0] || h < args[1]);
        }
        function dateRange() { return true; }
        function alert() { }
        """;

    private readonly Engine _engine;
    private readonly object _lock = new();

    public PacScript(string source, Func<string, IPAddress?>? resolve = null)
    {
        var dns = resolve ?? Resolve;
        _engine = new Engine(options => options
            .TimeoutInterval(TimeSpan.FromSeconds(2))
            .LimitMemory(16 * 1024 * 1024)
            .LimitRecursion(256));
        _engine.SetValue("__dnsResolve", new Func<string, string?>(host => dns(host)?.ToString()));
        _engine.SetValue("__myIpAddress", new Func<string>(MyIpAddress));
        _engine.SetValue("__isInNet", new Func<string, string, string, bool>((host, pattern, mask) => IsInNet(host, pattern, mask, dns)));
        _engine.Execute(Helpers);
        _engine.Execute(source);
        if (_engine.GetValue("FindProxyForURL").IsUndefined())
            throw new FormatException("The PAC file has no FindProxyForURL function.");
    }

    /// <summary>The script's answer, such as "PROXY host:8080; SOCKS5 host:1080; DIRECT".</summary>
    public string FindProxyForUrl(Uri url)
    {
        lock (_lock)
            return _engine.Invoke("FindProxyForURL", url.AbsoluteUri, url.Host).ToString();
    }

    /// <summary>The first usable choice of a PAC answer: a proxy address, or null for a direct connection.</summary>
    public static Uri? FirstChoice(string answer)
    {
        foreach (var part in answer.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pieces = part.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var kind = pieces[0].ToUpperInvariant();
            if (kind == "DIRECT")
                return null;
            if (pieces.Length < 2)
                continue;
            var scheme = kind switch
            {
                "PROXY" or "HTTP" => "http",
                "HTTPS" => "https",
                "SOCKS" or "SOCKS5" => "socks5",
                "SOCKS4" => "socks4",
                _ => null,
            };
            if (scheme is not null && Uri.TryCreate($"{scheme}://{pieces[1]}", UriKind.Absolute, out var proxy))
                return proxy;
        }
        return null;
    }

    private static IPAddress? Resolve(string host)
    {
        try
        {
            return IPAddress.TryParse(host, out var ip) ? ip
                : Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private static string MyIpAddress()
    {
        try
        {
            // The address of the interface that would carry traffic to the Internet; no packet is sent.
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("192.0.2.1", 9);
            return ((IPEndPoint)socket.LocalEndPoint!).Address.ToString();
        }
        catch (SocketException)
        {
            return "127.0.0.1";
        }
    }

    private static bool IsInNet(string host, string pattern, string mask, Func<string, IPAddress?> resolve)
    {
        if (resolve(host) is not { } address || !IPAddress.TryParse(pattern, out var net) || !IPAddress.TryParse(mask, out var m))
            return false;
        var a = address.GetAddressBytes();
        var n = net.GetAddressBytes();
        var k = m.GetAddressBytes();
        if (a.Length != n.Length || n.Length != k.Length)
            return false;
        for (var i = 0; i < a.Length; i++)
        {
            if ((a[i] & k[i]) != (n[i] & k[i]))
                return false;
        }
        return true;
    }
}
