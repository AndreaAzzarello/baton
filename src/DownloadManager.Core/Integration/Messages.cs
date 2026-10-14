using System.Text.Json;

namespace DownloadManager.Core.Integration;

/// <summary>A download handed over by the browser extension.</summary>
public sealed record BrowserDownload(
    string Url,
    string? FileName = null,
    string? Referrer = null,
    string? UserAgent = null,
    List<BrowserCookie>? Cookies = null)
{
    public RequestContext ToContext() => new(Referrer, UserAgent, Cookies ?? []);
}

/// <summary>Message from the extension (through the native host) to the app. Type is "ping" or "add".</summary>
public sealed record ChannelMessage(string Type, BrowserDownload? Download = null);

public sealed record ChannelReply(bool Ok, bool Running = true, string? Error = null);

public static class Messages
{
    public const string Ping = "ping";
    public const string Add = "add";
    public const string Activate = "activate";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
