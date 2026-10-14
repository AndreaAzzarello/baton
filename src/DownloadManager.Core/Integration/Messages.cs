using System.Text.Json;

namespace DownloadManager.Core.Integration;

/// <summary>A download handed over by the browser extension.</summary>
public sealed record BrowserDownload(
    string Url,
    string? FileName = null,
    string? Referrer = null,
    string? UserAgent = null,
    List<BrowserCookie>? Cookies = null,
    string? Queue = null) // name of the queue; added in 0.4, older extensions omit it and get the main queue
{
    public RequestContext ToContext() => new(Referrer, UserAgent, Cookies ?? []);
}

/// <summary>Message from the extension (through the native host) to the app. Type is "ping", "add", "queues" or "activate".</summary>
public sealed record ChannelMessage(string Type, BrowserDownload? Download = null);

/// <param name="Queues">For "queues": the names of the app's queues, the main one first (added in 0.5).</param>
public sealed record ChannelReply(bool Ok, bool Running = true, string? Error = null, List<string>? Queues = null);

public static class Messages
{
    public const string Ping = "ping";
    public const string Add = "add";
    public const string Activate = "activate";

    /// <summary>The extension's popup asks for the queue names, to offer them for intercepted downloads.</summary>
    public const string Queues = "queues";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
