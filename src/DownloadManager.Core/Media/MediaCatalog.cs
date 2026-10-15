using System.Globalization;
using System.Net.Http.Headers;
using DownloadManager.Core.Integration;
using DownloadManager.Core.Resources;

namespace DownloadManager.Core.Media;

/// <summary>One stream of a download: its segments, put together into one file with <see cref="Extension"/>.</summary>
/// <param name="Kind">"video", "audio" or "muxed" (video and audio in the same segments).</param>
public sealed record MediaTrack(string Kind, MediaSegment? Init, List<MediaSegment> Segments, string Extension);

/// <summary>A WebVTT subtitle track: its segments are joined into one file.</summary>
public sealed record SubtitlePlan(string? Language, string Label, List<MediaSegment> Segments);

/// <summary>
/// What to download for the chosen quality: one track (muxed, or audio only) or a video and an audio track, and the
/// subtitles chosen.
/// </summary>
public sealed record MediaPlan(string Kind, List<MediaTrack> Tracks, double? Duration, long? EstimatedBytes, List<SubtitlePlan>? Subtitles = null);

/// <summary>Why a stream is not offered for download.</summary>
public static class MediaBlock
{
    public const string Drm = "drm";
    public const string Terms = "terms";
    public const string Live = "live";
    public const string Unsupported = "unsupported";
}

/// <summary>
/// Sites whose terms of service forbid downloading their videos: nothing is offered there, whatever the format.
/// The list is deliberately conservative and matched on the page and on the media address.
/// </summary>
public static class MediaPolicy
{
    public static readonly string[] RestrictedSites =
    [
        "youtube.com", "youtu.be", "youtube-nocookie.com", "googlevideo.com", "ytimg.com",
        "netflix.com", "nflxvideo.net",
        "primevideo.com", "amazonvideo.com",
        "disneyplus.com", "disney-plus.net", "dssott.com",
        "hulu.com", "max.com", "hbomax.com",
        "tv.apple.com",
        "spotify.com", "scdn.co",
        "twitch.tv", "ttvnw.net",
        "tiktok.com", "instagram.com", "facebook.com", "fbcdn.net",
        "dazn.com", "paramountplus.com", "peacocktv.com",
    ];

    public static bool IsRestricted(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && RestrictedSites.Any(site =>
            uri.Host.Equals(site, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + site, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Reads a stream's manifest to list its qualities (with an estimated size) and, for the chosen one, the segments to
/// download. Protected (DRM or encrypted), live and restricted streams are reported, never downloaded.
/// </summary>
public static class MediaCatalog
{
    private const int MaxManifestBytes = 20 * 1024 * 1024;

    public static async Task<MediaInfoReply> ProbeAsync(HttpClient http, string url, RequestContext? context, string? pageUrl, CancellationToken ct)
    {
        if (MediaPolicy.IsRestricted(url) || MediaPolicy.IsRestricted(pageUrl))
            return Blocked("unknown", MediaBlock.Terms, Strings.MediaBlockedTerms);

        var (text, finalUrl, contentType) = await FetchAsync(http, url, context, ct);
        if (text is null)
        {
            // Not a manifest: a plain video or audio file, downloaded like any other file.
            var probe = await HttpProbe.ProbeAsync(http, url, ct, context);
            var label = probe.ContentType ?? Path.GetExtension(probe.FileName).TrimStart('.').ToUpperInvariant();
            return new MediaInfoReply("file", [new MediaVariantInfo("file", label, probe.TotalSize, null, "file")], null);
        }

        if (HlsPlaylist.LooksLikeHls(text))
            return await ProbeHlsAsync(http, HlsPlaylist.Parse(text, finalUrl), context, ct);
        if (DashManifest.LooksLikeDash(text))
            return ProbeDash(DashManifest.Parse(text, finalUrl));
        return Blocked("unknown", MediaBlock.Unsupported, string.Format(CultureInfo.CurrentCulture, Strings.MediaUnsupported, contentType ?? "?"));
    }

    private static async Task<MediaInfoReply> ProbeHlsAsync(HttpClient http, HlsPlaylist playlist, RequestContext? context, CancellationToken ct)
    {
        if (playlist.IsEncrypted)
            return Blocked("hls", MediaBlock.Drm, Strings.MediaBlockedDrm);
        if (!playlist.IsMaster)
            return CheckMedia(playlist) ?? new MediaInfoReply("hls",
                [new MediaVariantInfo("hls:0", Strings.MediaSingleQuality, playlist.KnownSize, null, "video")], playlist.Duration);

        // The first quality's playlist tells the duration, and whether the segments are encrypted or the stream is live.
        var first = playlist.Variants.OrderByDescending(v => v.Bandwidth).First();
        var (text, finalUrl, _) = await FetchAsync(http, first.Url.AbsoluteUri, context, ct);
        if (text is null || !HlsPlaylist.LooksLikeHls(text))
            throw new DownloadFailedException("The stream's quality playlist could not be read.");
        var media = HlsPlaylist.Parse(text, finalUrl);
        if (CheckMedia(media) is { } blocked)
            return blocked;

        var duration = media.Duration;
        var variants = playlist.Variants
            .Select((v, index) => (v, index))
            .OrderByDescending(x => x.v.Height ?? 0).ThenByDescending(x => x.v.Bandwidth)
            .Select(x => new MediaVariantInfo($"hls:{x.index}", Label(x.v.Height, x.v.Bandwidth),
                Estimate(x.v.AverageBandwidth ?? x.v.Bandwidth, duration), x.v.Height, "video"))
            .GroupBy(v => v.Label).Select(g => g.First()) // the same quality listed twice (e.g. for two CDNs)
            .ToList();
        var (audio, subtitles) = HlsTracks(playlist, first);
        return new MediaInfoReply("hls", variants, duration, AudioTracks: audio, Subtitles: subtitles);
    }

    /// <summary>The audio languages and subtitles a quality offers (each quality points at its own groups).</summary>
    private static (List<MediaTrackInfo>? Audio, List<MediaTrackInfo>? Subtitles) HlsTracks(HlsPlaylist master, HlsVariant variant)
    {
        List<MediaTrackInfo>? List(IEnumerable<HlsRendition> renditions)
        {
            var list = renditions.DistinctBy(r => r.Key)
                .Select(r => new MediaTrackInfo(r.Key, TrackLabel(r.Name, r.Language), r.Language, r.IsDefault)).ToList();
            return list.Count > 0 ? list : null;
        }
        // An audio rendition without its own playlist is the audio inside the video: a choice too ("the video's own").
        var audio = variant.AudioGroup is { } group ? List(master.Renditions.Where(r => r.Type == "AUDIO" && r.GroupId == group)) : null;
        var subtitles = List(master.Renditions.Where(r => r.Type == "SUBTITLES" && r.Url is not null
                                                        && (variant.SubtitlesGroup is null || r.GroupId == variant.SubtitlesGroup)));
        return (audio, subtitles);
    }

    /// <summary>The name the stream gives a track, else the language in its own words ("italiano" → "Italiano").</summary>
    public static string TrackLabel(string? name, string? language)
    {
        if (!string.IsNullOrWhiteSpace(name))
            return name.Trim();
        if (string.IsNullOrWhiteSpace(language))
            return "?";
        try
        {
            var native = CultureInfo.GetCultureInfo(language.Trim(), predefinedOnly: true).NativeName;
            return char.ToUpper(native[0], CultureInfo.CurrentCulture) + native[1..];
        }
        catch (CultureNotFoundException)
        {
            return language.Trim();
        }
    }

    private static MediaInfoReply? CheckMedia(HlsPlaylist media)
    {
        if (media.IsEncrypted)
            return Blocked("hls", MediaBlock.Drm, Strings.MediaBlockedDrm);
        if (!media.HasEnd)
            return Blocked("hls", MediaBlock.Live, Strings.MediaBlockedLive);
        if (media.Segments.Count == 0)
            return Blocked("hls", MediaBlock.Unsupported, string.Format(CultureInfo.CurrentCulture, Strings.MediaUnsupported, "HLS"));
        return null;
    }

    private static MediaInfoReply ProbeDash(DashManifest manifest)
    {
        if (manifest.IsProtected)
            return Blocked("dash", MediaBlock.Drm, Strings.MediaBlockedDrm);
        if (manifest.IsLive)
            return Blocked("dash", MediaBlock.Live, Strings.MediaBlockedLive);
        var videos = manifest.Representations.Where(r => r.ContentType == "video").ToList();
        var audio = BestAudio(manifest);
        var duration = manifest.Duration;
        List<MediaVariantInfo> variants;
        if (videos.Count > 0)
        {
            variants = videos
                .OrderByDescending(v => v.Height ?? 0).ThenByDescending(v => v.Bandwidth)
                .Select(v => new MediaVariantInfo($"dash:{v.Id}|{audio?.Id}", Label(v.Height, v.Bandwidth),
                    Estimate(v.Bandwidth + (audio?.Bandwidth ?? 0), duration), v.Height, "video"))
                .ToList();
        }
        else
        {
            variants = manifest.Representations.Where(r => r.ContentType == "audio").OrderByDescending(a => a.Bandwidth)
                .Select(a => new MediaVariantInfo($"dash:|{a.Id}", Label(null, a.Bandwidth), Estimate(a.Bandwidth, duration), null, "audio"))
                .ToList();
        }
        if (variants.Count == 0)
            return Blocked("dash", MediaBlock.Unsupported, string.Format(CultureInfo.CurrentCulture, Strings.MediaUnsupported, "DASH"));

        // One choice per adaptation set: an audio language (its best bit rate) or a subtitle track.
        var languages = manifest.Representations.Where(r => r.ContentType == "audio").GroupBy(r => r.SetIndex)
            .Select(g => g.OrderByDescending(r => r.Bandwidth).First())
            .Select(a => new MediaTrackInfo(a.Id, TrackLabel(a.Label, a.Language), a.Language, a.Id == audio?.Id)).ToList();
        var subtitles = manifest.Representations.Where(r => r.ContentType == "text").GroupBy(r => r.SetIndex).Select(g => g.First())
            .Select(t => new MediaTrackInfo(t.Id, TrackLabel(t.Label, t.Language), t.Language, false)).ToList();
        return new MediaInfoReply("dash", variants, duration, AudioTracks: languages.Count > 0 ? languages : null,
            Subtitles: subtitles.Count > 0 ? subtitles : null);
    }

    /// <summary>
    /// The segments to download for <paramref name="variantId"/>, in the audio language <paramref name="audioId"/>
    /// (default: the stream's own), with the subtitles <paramref name="subtitleIds"/>; throws when the stream may not be
    /// downloaded.
    /// </summary>
    public static async Task<MediaPlan> ResolveAsync(HttpClient http, string url, string variantId, RequestContext? context, CancellationToken ct,
        string? audioId = null, IReadOnlyList<string>? subtitleIds = null)
    {
        if (MediaPolicy.IsRestricted(url) || MediaPolicy.IsRestricted(context?.Referrer))
            throw new DownloadFailedException(Strings.MediaBlockedTerms);
        var (text, finalUrl, _) = await FetchAsync(http, url, context, ct);
        if (text is null)
            throw new DownloadFailedException("The address no longer returns a stream manifest.");

        if (variantId.StartsWith("hls:", StringComparison.Ordinal) && HlsPlaylist.LooksLikeHls(text))
        {
            var playlist = HlsPlaylist.Parse(text, finalUrl);
            if (!playlist.IsMaster)
                return HlsPlan(playlist, null);
            if (playlist.IsEncrypted)
                throw new DownloadFailedException(Strings.MediaBlockedDrm);
            var index = int.Parse(variantId[4..], CultureInfo.InvariantCulture);
            if (index < 0 || index >= playlist.Variants.Count)
                throw new DownloadFailedException("The chosen quality is no longer offered.");
            var variant = playlist.Variants[index];
            var video = await FetchPlaylistAsync(http, variant.Url, context, ct);
            HlsPlaylist? audio = null;
            if (ChooseAudio(playlist, variant, audioId) is { Url: { } audioUrl })
                audio = await FetchPlaylistAsync(http, audioUrl, context, ct);
            var subtitles = new List<SubtitlePlan>();
            foreach (var id in subtitleIds ?? [])
            {
                var candidates = playlist.Renditions.Where(r => r.Type == "SUBTITLES" && r.Key == id && r.Url is not null).ToList();
                if ((candidates.FirstOrDefault(r => r.GroupId == variant.SubtitlesGroup) ?? candidates.FirstOrDefault()) is not { Url: { } subtitleUrl } rendition)
                    continue; // no longer offered: the video is still downloaded
                try
                {
                    var track = await FetchPlaylistAsync(http, subtitleUrl, context, ct);
                    subtitles.Add(new SubtitlePlan(rendition.Language, TrackLabel(rendition.Name, rendition.Language), track.Segments));
                }
                catch (Exception ex) when (ex is HttpRequestException or DownloadFailedException or IOException or TimeoutException
                                               or FormatException or Net.AuthenticationRequiredException)
                {
                    // Subtitles that cannot be read do not cost the video.
                }
            }
            return HlsPlan(video, audio, variant.AverageBandwidth ?? variant.Bandwidth) with { Subtitles = subtitles };
        }

        if (variantId.StartsWith("dash:", StringComparison.Ordinal) && DashManifest.LooksLikeDash(text))
        {
            var manifest = DashManifest.Parse(text, finalUrl);
            if (manifest.IsProtected)
                throw new DownloadFailedException(Strings.MediaBlockedDrm);
            if (manifest.IsLive)
                throw new DownloadFailedException(Strings.MediaBlockedLive);
            var ids = variantId[5..].Split('|');
            var video = manifest.Representations.FirstOrDefault(r => r.ContentType == "video" && r.Id == ids[0]);
            var audio = ids.Length > 1 ? manifest.Representations.FirstOrDefault(r => r.ContentType == "audio" && r.Id == ids[1]) : null;
            // Another language chosen for a video: its own representation replaces the default audio. Audio-only
            // qualities are languages and bit rates already, so the quality chosen is kept.
            if (video is not null && audio is not null && audioId is not null && manifest.Representations.FirstOrDefault(r => r.ContentType == "audio" && r.Id == audioId) is { } chosen)
                audio = chosen;
            if (video is null && audio is null)
                throw new DownloadFailedException("The chosen quality is no longer offered.");
            var subtitles = (subtitleIds ?? [])
                .Select(id => manifest.Representations.FirstOrDefault(r => r.ContentType == "text" && r.Id == id))
                .OfType<DashRepresentation>()
                .Select(t => new SubtitlePlan(t.Language, TrackLabel(t.Label, t.Language), t.Init is null ? t.Segments : [t.Init, .. t.Segments]))
                .ToList();
            var tracks = new List<MediaTrack>();
            if (video is not null)
                tracks.Add(new MediaTrack("video", video.Init, video.Segments, ExtensionOf(video, ".mp4")));
            if (audio is not null)
                tracks.Add(new MediaTrack("audio", audio.Init, audio.Segments, ExtensionOf(audio, ".m4a")));
            return new MediaPlan("dash", tracks, manifest.Duration,
                Estimate((video?.Bandwidth ?? 0) + (audio?.Bandwidth ?? 0), manifest.Duration), subtitles);
        }
        throw new DownloadFailedException("The stream changed since it was added.");
    }

    private static async Task<HlsPlaylist> FetchPlaylistAsync(HttpClient http, Uri url, RequestContext? context, CancellationToken ct)
    {
        var (text, finalUrl, _) = await FetchAsync(http, url.AbsoluteUri, context, ct);
        if (text is null || !HlsPlaylist.LooksLikeHls(text))
            throw new DownloadFailedException("A playlist of the stream could not be read.");
        var playlist = HlsPlaylist.Parse(text, finalUrl);
        if (playlist.IsEncrypted)
            throw new DownloadFailedException(Strings.MediaBlockedDrm);
        if (!playlist.HasEnd)
            throw new DownloadFailedException(Strings.MediaBlockedLive);
        return playlist;
    }

    private static MediaPlan HlsPlan(HlsPlaylist video, HlsPlaylist? audio, long? bandwidth = null)
    {
        if (video.IsEncrypted || audio?.IsEncrypted == true)
            throw new DownloadFailedException(Strings.MediaBlockedDrm);
        if (!video.HasEnd)
            throw new DownloadFailedException(Strings.MediaBlockedLive);
        var tracks = new List<MediaTrack> { new(audio is null ? "muxed" : "video", video.Init, video.Segments, HlsExtension(video, ".ts")) };
        if (audio is not null)
            tracks.Add(new MediaTrack("audio", audio.Init, audio.Segments, HlsExtension(audio, ".aac")));
        var size = video.KnownSize is { } known ? known + (audio?.KnownSize ?? 0) : Estimate(bandwidth ?? 0, video.Duration);
        return new MediaPlan("hls", tracks, video.Duration, size);
    }

    /// <summary>
    /// The audio rendition of the variant's group: the one chosen (by language and name, else by language), else the
    /// default one, else the first with its own playlist. A rendition without a playlist is the variant's own audio.
    /// </summary>
    public static HlsRendition? ChooseAudio(HlsPlaylist master, HlsVariant variant, string? audioId = null)
    {
        if (variant.AudioGroup is not { } group)
            return null;
        // A rendition without a URI is in the variant itself: choosing it (or it being the default) means no separate audio.
        var candidates = master.Renditions.Where(r => r.Type == "AUDIO" && r.GroupId == group).ToList();
        if (audioId is not null)
        {
            var language = audioId.Split('|')[0];
            if ((candidates.FirstOrDefault(r => r.Key == audioId) ?? candidates.FirstOrDefault(r => language.Length > 0 && r.Language == language)) is { } chosen)
                return chosen;
        }
        return candidates.FirstOrDefault(r => r.IsDefault) ?? candidates.FirstOrDefault(r => r.Url is not null);
    }

    /// <summary>The audio to pair with every video quality: the highest bandwidth of the main (else the first) language listed.</summary>
    public static DashRepresentation? BestAudio(DashManifest manifest)
    {
        var audio = manifest.Representations.Where(r => r.ContentType == "audio").ToList();
        if (audio.Count == 0)
            return null;
        var set = (audio.FirstOrDefault(a => a.IsDefault) ?? audio[0]).SetIndex;
        return audio.Where(a => a.SetIndex == set).OrderByDescending(a => a.Bandwidth).First();
    }

    private static string HlsExtension(HlsPlaylist playlist, string fallback)
    {
        if (playlist.Init is not null)
            return fallback == ".aac" ? ".m4a" : ".mp4"; // fragmented MP4
        var path = playlist.Segments.FirstOrDefault()?.Url.AbsolutePath ?? "";
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".aac" => ".aac",
            ".mp3" => ".mp3",
            ".m4s" or ".mp4" or ".m4v" => ".mp4",
            ".m4a" => ".m4a",
            _ => fallback,
        };
    }

    private static string ExtensionOf(DashRepresentation rep, string fallback)
    {
        var sample = (rep.Init ?? rep.Segments.FirstOrDefault())?.Url.AbsolutePath ?? "";
        if (sample.EndsWith(".webm", StringComparison.OrdinalIgnoreCase) || rep.Codecs is { } c && (c.StartsWith("vp") || c is "opus" or "vorbis"))
            return ".webm";
        return fallback;
    }

    public static long? Estimate(long bitsPerSecond, double? seconds) =>
        bitsPerSecond > 0 && seconds is > 0 ? (long)(bitsPerSecond / 8.0 * seconds.Value) : null;

    /// <summary>"1080p" for video, otherwise the bit rate.</summary>
    public static string Label(int? height, long bandwidth) =>
        height is > 0 ? $"{height}p" : bandwidth >= 1_000_000
            ? string.Format(CultureInfo.InvariantCulture, "{0:0.#} Mbit/s", bandwidth / 1_000_000.0)
            : string.Format(CultureInfo.InvariantCulture, "{0} kbit/s", Math.Max(1, bandwidth / 1000));

    private static MediaInfoReply Blocked(string kind, string reason, string message) => new(kind, [], null, reason, message);

    /// <summary>
    /// Downloads a manifest. Returns no text when the address is not a manifest (a plain media file), without reading
    /// its body.
    /// </summary>
    private static async Task<(string? Text, Uri FinalUrl, string? ContentType)> FetchAsync(HttpClient http, string url, RequestContext? context,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        context.ApplyTo(request);
        using var response = await HttpErrors.SendAsync(http, request, TimeSpan.FromSeconds(30), ct);
        if (!response.IsSuccessStatusCode)
            throw HttpErrors.FromResponse(response);
        var finalUrl = response.RequestMessage?.RequestUri ?? new Uri(url);
        var type = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
        if (!IsManifest(type, finalUrl))
            return (null, finalUrl, type);
        if (response.Content.Headers.ContentLength > MaxManifestBytes)
            throw new DownloadFailedException("The stream manifest is too large.");
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxManifestBytes)
                throw new DownloadFailedException("The stream manifest is too large.");
        }
        return (System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length), finalUrl, type);
    }

    public static bool IsManifest(string? contentType, Uri url)
    {
        if (contentType is "application/vnd.apple.mpegurl" or "application/x-mpegurl" or "audio/mpegurl" or "audio/x-mpegurl"
            or "application/dash+xml" or "video/vnd.mpeg.dash.mpd")
            return true;
        var path = url.AbsolutePath;
        return path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase)
               // Some servers send manifests as text/plain or application/octet-stream with a query-string address.
               || (contentType is "text/plain" or "application/octet-stream" or "binary/octet-stream" && path.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A file name for the stream, from the page title when there is one.</summary>
    public static string FileNameFor(string? title, string url, string extension)
    {
        var name = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(new Uri(url).AbsolutePath) : title.Trim();
        if (string.IsNullOrWhiteSpace(name))
            name = "video";
        name = FileNames.Sanitize(name.Replace('/', '_').Replace('\\', '_')); // a title is not a path
        if (name.Length > 120)
            name = name[..120].TrimEnd();
        return name + extension;
    }

    internal static RangeHeaderValue? RangeOf(MediaSegment segment) =>
        segment.Offset is { } offset && segment.Length is { } length ? new RangeHeaderValue(offset, offset + length - 1) : null;
}
