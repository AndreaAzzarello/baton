using System.Text.Json;

namespace Baton.Core.Integration;

/// <summary>A download handed over by the browser extension.</summary>
/// <param name="Queue">Name of the queue (0.4); older extensions omit it and get the main queue.</param>
/// <param name="Method">"GET" or "POST" (protocol 2): a download started by submitting a form.</param>
/// <param name="Body">The form fields, URL-encoded, for a POST.</param>
/// <param name="BodyType">How the form encodes its fields: application/x-www-form-urlencoded or multipart/form-data (the fields are passed URL-encoded either way).</param>
public sealed record BrowserDownload(
    string Url,
    string? FileName = null,
    string? Referrer = null,
    string? UserAgent = null,
    List<BrowserCookie>? Cookies = null,
    string? Queue = null,
    string? Method = null,
    string? Body = null,
    string? BodyType = null)
{
    public RequestContext ToContext() => new(Referrer, UserAgent, Cookies ?? [])
    {
        Method = string.Equals(Method, "POST", StringComparison.OrdinalIgnoreCase) ? "POST" : null,
        Body = Body,
        BodyType = BodyType,
    };

    /// <summary>
    /// A form another site sent to this address (or with no known origin). Repeating it carries the user's session to
    /// that address on behalf of a page they may not trust, so the app always asks first.
    /// </summary>
    public bool IsCrossSitePost => string.Equals(Method, "POST", StringComparison.OrdinalIgnoreCase) && !Urls.SameSite(Url, Referrer);
}

/// <summary>A video or audio resource the extension saw the page load (protocol 2).</summary>
/// <param name="Url">The stream: an HLS (.m3u8) or DASH (.mpd) manifest, or a media file.</param>
/// <param name="VariantId">For "addMedia": the quality chosen from the list "probeMedia" returned.</param>
/// <param name="Label">For "addMedia": that quality as the list showed it (e.g. "720p").</param>
/// <param name="AudioId">For "addMedia" (protocol 3): the audio language chosen from "audioTracks"; null = the default one.</param>
/// <param name="SubtitleIds">For "addMedia" (protocol 3): the subtitles chosen from "subtitles".</param>
/// <param name="SubtitleMode">"vtt" or "srt" (a file next to the video) or "embed" (inside the video, with ffmpeg).</param>
public sealed record MediaRequest(
    string Url,
    string? PageUrl = null,
    string? Title = null,
    string? Referrer = null,
    string? UserAgent = null,
    List<BrowserCookie>? Cookies = null,
    string? VariantId = null,
    string? Queue = null,
    string? Label = null,
    string? AudioId = null,
    List<string>? SubtitleIds = null,
    string? SubtitleMode = null)
{
    public RequestContext ToContext() => new(Referrer ?? PageUrl, UserAgent, Cookies ?? []);
}

/// <summary>One link or image of a page, for "download all" (protocol 2).</summary>
/// <param name="Kind">"link" or "image".</param>
public sealed record BatchItem(string Url, string? Text = null, string? Kind = null);

/// <summary>Every link and image of a page, for "download all" (protocol 2).</summary>
public sealed record BatchRequest(
    string PageUrl,
    string? PageTitle,
    List<BatchItem> Items,
    string? UserAgent = null,
    List<BrowserCookie>? Cookies = null,
    string? Queue = null);

/// <summary>"Capture this site": the page the extension's menu was used on, with its cookies (protocol 3).</summary>
public sealed record GrabRequest(string Url, string? Title = null, string? UserAgent = null, List<BrowserCookie>? Cookies = null);

/// <summary>One quality of a video, as offered in the extension's list.</summary>
public sealed record MediaVariantInfo(string Id, string Label, long? EstimatedBytes, int? Height, string Kind);

/// <summary>An audio language or a subtitle track of a video (protocol 3).</summary>
public sealed record MediaTrackInfo(string Id, string Label, string? Language, bool IsDefault);

/// <summary>What the app found at a media address.</summary>
/// <param name="Blocked">Not offered: "drm" (protected content), "terms" (site that forbids it), "live" (a live stream).</param>
/// <param name="AudioTracks">The audio languages to choose from (protocol 3); absent or one when there is no choice.</param>
/// <param name="Subtitles">WebVTT subtitles the stream offers (protocol 3).</param>
public sealed record MediaInfoReply(string Kind, List<MediaVariantInfo> Variants, double? DurationSeconds, string? Blocked = null, string? Message = null,
    List<MediaTrackInfo>? AudioTracks = null, List<MediaTrackInfo>? Subtitles = null);

/// <summary>
/// Message from the extension (through the native host) to the app. <see cref="Version"/> is the protocol version
/// the extension speaks: absent in 0.5 and earlier (protocol 1), 2 from 0.6.
/// </summary>
public sealed record ChannelMessage(
    string Type,
    BrowserDownload? Download = null,
    int? Version = null,
    MediaRequest? Media = null,
    BatchRequest? Batch = null,
    GrabRequest? Grab = null);

/// <param name="Queues">For "queues": the names of the app's queues, the main one first (added in 0.5).</param>
/// <param name="Protocol">The protocol version the app speaks (2 from 0.6; absent from older apps, which speak 1).</param>
/// <param name="AppVersion">The app's version, for the extension's connection check.</param>
public sealed record ChannelReply(
    bool Ok,
    bool Running = true,
    string? Error = null,
    List<string>? Queues = null,
    int? Protocol = null,
    string? AppVersion = null,
    MediaInfoReply? Media = null);

public static class Messages
{
    /// <summary>
    /// Version 1: ping, add, queues (extension 0.2–0.5). Version 2: hello, POST downloads, probeMedia, addMedia, batch.
    /// Version 3: grab, audio languages and subtitles of videos. A newer app keeps answering older messages unchanged.
    /// </summary>
    public const int ProtocolVersion = 3;

    public const string Ping = "ping";
    public const string Add = "add";
    public const string Activate = "activate";

    /// <summary>The extension's popup asks for the queue names, to offer them for intercepted downloads.</summary>
    public const string Queues = "queues";

    /// <summary>Connection check: the app answers with its protocol and version (protocol 2).</summary>
    public const string Hello = "hello";

    /// <summary>What qualities a video address offers (protocol 2).</summary>
    public const string ProbeMedia = "probeMedia";

    /// <summary>Download a video in the chosen quality (protocol 2).</summary>
    public const string AddMedia = "addMedia";

    /// <summary>Open "download all" with the links and images of a page (protocol 2).</summary>
    public const string Batch = "batch";

    /// <summary>Open a new grabber project for the page (protocol 3).</summary>
    public const string Grab = "grab";

    /// <summary>Messages that never start the app: the answer without it is enough.</summary>
    public static readonly string[] Questions = [Ping, Queues, Hello];

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
