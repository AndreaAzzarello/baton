using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Baton.Core;

/// <summary>Applies a browser request context to outgoing requests and stores it encrypted at rest.</summary>
public static class RequestContexts
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    // Kept from before the rename to Baton: another value would make the saved contexts unreadable.
    private static readonly byte[] Entropy = "DownloadManager.RequestContext.v1"u8.ToArray();

    public static void ApplyTo(this RequestContext? context, HttpRequestMessage request)
    {
        if (context is null)
            return;
        if (Uri.TryCreate(context.Referrer, UriKind.Absolute, out var referrer) && referrer.Scheme is "http" or "https")
            request.Headers.Referrer = referrer;
        if (!string.IsNullOrWhiteSpace(context.UserAgent))
        {
            request.Headers.Remove("User-Agent");
            request.Headers.TryAddWithoutValidation("User-Agent", context.UserAgent);
        }
    }

    /// <summary>
    /// Cookies go in a container rather than a fixed header so each one is only sent to the hosts and
    /// paths it belongs to, including after a redirect to another site.
    /// </summary>
    public static CookieContainer ToCookieContainer(IEnumerable<BrowserCookie> cookies)
    {
        var container = new CookieContainer();
        foreach (var c in cookies)
        {
            try
            {
                var domain = c.HostOnly ? c.Domain.TrimStart('.') : "." + c.Domain.TrimStart('.');
                container.Add(new Cookie(c.Name, c.Value, string.IsNullOrEmpty(c.Path) ? "/" : c.Path, domain) { Secure = c.Secure });
            }
            catch (CookieException)
            {
                // A value the .NET cookie parser rejects (for example one containing a comma) is skipped.
            }
        }
        return container;
    }

    /// <summary>Serializes the context, encrypted with the Windows user's DPAPI key because it holds session cookies.</summary>
    public static string Protect(RequestContext context)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(context, Json);
        if (OperatingSystem.IsWindows())
            return "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser));
        return "json:" + Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Returns null when the value cannot be read, for example after the Windows profile changed.</summary>
    public static RequestContext? Unprotect(string? value)
    {
        try
        {
            if (value is null)
                return null;
            if (value.StartsWith("dpapi:", StringComparison.Ordinal) && OperatingSystem.IsWindows())
                return JsonSerializer.Deserialize<RequestContext>(
                    ProtectedData.Unprotect(Convert.FromBase64String(value[6..]), Entropy, DataProtectionScope.CurrentUser), Json);
            if (value.StartsWith("json:", StringComparison.Ordinal))
                return JsonSerializer.Deserialize<RequestContext>(value[5..], Json);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        {
        }
        return null;
    }
}
