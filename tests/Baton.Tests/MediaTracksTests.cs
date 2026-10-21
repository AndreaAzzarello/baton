using System.Text;
using Baton.Core;
using Baton.Core.Integration;
using Baton.Core.Media;

namespace Baton.Tests;

/// <summary>Audio languages, WebVTT subtitles (next to the video or inside it) and DASH presentations in several periods.</summary>
public sealed partial class MediaTests
{
    private string ServeText(string name, string text, string type) =>
        _server.Add(name, new ServedFile { Content = Encoding.UTF8.GetBytes(text), ContentType = type });

    /// <summary>The HLS stream with two audio languages and two subtitle tracks; returns the master playlist's address.</summary>
    private string LanguageStream()
    {
        var url = Serve("lang-master.m3u8", Hls);
        foreach (var playlist in new[] { "lang-video.m3u8", "lang-audio-en.m3u8", "lang-audio-it.m3u8", "lang-subs-en.m3u8", "lang-subs-it.m3u8" })
            Serve(playlist, Hls);
        Segments(["lang-video-0.ts", "lang-video-1.ts"], size: 5000);
        Segments(["lang-audio-en-0.aac", "lang-audio-en-1.aac"], size: 800);
        Segments(["lang-audio-it-0.aac", "lang-audio-it-1.aac"], size: 900);
        // A cue crossing the segment boundary is repeated in both segments, as HLS packagers do.
        ServeText("lang-subs-en-0.vtt", "WEBVTT\nX-TIMESTAMP-MAP=MPEGTS:900000,LOCAL:00:00:00.000\n\n00:00:01.000 --> 00:00:02.500 align:center\n<i>Hello</i> &amp; welcome\n\n00:00:03.500 --> 00:00:04.500\nAcross\n", "text/vtt");
        ServeText("lang-subs-en-1.vtt", "WEBVTT\nX-TIMESTAMP-MAP=MPEGTS:900000,LOCAL:00:00:00.000\n\n00:00:03.500 --> 00:00:04.500\nAcross\n\n00:00:05.000 --> 00:00:06.000\n<c.yellow>Bye</c>\n", "text/vtt");
        ServeText("lang-subs-it-0.vtt", "WEBVTT\n\n00:01.000 --> 00:02.000\nCiao\n", "text/vtt");
        ServeText("lang-subs-it-1.vtt", "WEBVTT\n\nNOTE nothing here\n", "text/vtt");
        return url;
    }

    [Fact]
    public async Task Probe_Hls_ListsAudioLanguagesAndSubtitles()
    {
        var url = LanguageStream();
        using var store = Store();
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);

        var info = await service.ProbeMediaAsync(new MediaRequest(url));

        Assert.Equal(["en|English", "it|Italiano"], info.AudioTracks!.Select(t => t.Id));
        Assert.True(info.AudioTracks![0].IsDefault);
        Assert.Equal(["English", "Italiano"], info.Subtitles!.Select(t => t.Label));
        Assert.Equal("it", info.Subtitles![1].Language);
    }

    [Fact]
    public async Task Hls_ChosenLanguage_AndSubtitlesAsSrtFiles_WithoutFfmpeg()
    {
        var url = LanguageStream();
        using var store = Store();
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);

        var request = new MediaRequest(url, Title: "Clip", VariantId: "hls:0", AudioId: "it|Italiano", SubtitleIds: ["en|English", "it|Italiano"], SubtitleMode: "srt");
        var done = await Finished(service, service.AddMedia(request, "hls", "360p"));

        Assert.Equal(DownloadStatus.Completed, done.Status);
        Assert.Contains(_server.Requests, r => r.Path == "lang-audio-it-0.aac");
        Assert.DoesNotContain(_server.Requests, r => r.Path.StartsWith("lang-audio-en"));
        Assert.Equal(1000 + 1001, new FileInfo(Path.Combine(VideoDir, "Clip.audio.aac")).Length); // the two Italian segments
        var srt = File.ReadAllText(Path.Combine(VideoDir, "Clip.en.srt"));
        Assert.Equal("1\r\n00:00:01,000 --> 00:00:02,500\r\n<i>Hello</i> & welcome\r\n\r\n2\r\n00:00:03,500 --> 00:00:04,500\r\nAcross\r\n\r\n3\r\n00:00:05,000 --> 00:00:06,000\r\nBye\r\n\r\n", srt);
        Assert.Contains("Ciao", File.ReadAllText(Path.Combine(VideoDir, "Clip.it.srt")));
        Assert.DoesNotContain(Directory.GetFiles(VideoDir), f => f.EndsWith(".vtt"));
    }

    [Fact]
    public async Task Subtitles_AreEmbeddedWithFfmpeg_TaggedWithTheirLanguage()
    {
        var ffmpeg = FakeFfmpeg(succeed: true);
        var url = LanguageStream();
        using var store = Store(ffmpeg);
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);

        var request = new MediaRequest(url, Title: "Inside", VariantId: "hls:0", SubtitleIds: ["it|Italiano", "en|English"], SubtitleMode: "embed");
        var done = await Finished(service, service.AddMedia(request, "hls", "360p"));

        Assert.Equal(DownloadStatus.Completed, done.Status);
        Assert.Null(done.Error);
        var args = File.ReadAllLines(Path.Combine(Path.GetDirectoryName(ffmpeg)!, "args.txt"));
        Assert.Contains("mov_text", args);
        // cmd.exe splits "language=ita" at the "=" (the real ffmpeg.exe receives it whole).
        Assert.Equal(["ita", "eng"], args.Select((a, i) => (a, i)).Where(x => x.a == "language").Select(x => args[x.i + 1]));
        Assert.Contains("2:0", args); // the first subtitle file is the third input
        Assert.Equal(2, args.Count(a => a.EndsWith(".vtt")));
        Assert.Equal(["Inside.mp4"], Directory.GetFiles(VideoDir, "Inside*").Select(Path.GetFileName));
    }

    [Fact]
    public async Task EmbedWithoutFfmpeg_KeepsTheSubtitlesNextToTheVideo_AndSaysSo()
    {
        var url = LanguageStream();
        using var store = Store();
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);

        var request = new MediaRequest(url, Title: "Plain", VariantId: "hls:0", SubtitleIds: ["en|English"], SubtitleMode: "embed");
        var done = await Finished(service, service.AddMedia(request, "hls", "360p"));

        Assert.Equal(DownloadStatus.Completed, done.Status);
        Assert.True(File.Exists(Path.Combine(VideoDir, "Plain.en.vtt")));
        Assert.StartsWith("WEBVTT", File.ReadAllText(Path.Combine(VideoDir, "Plain.en.vtt")));
        Assert.Contains(".vtt", done.Error);
        Assert.Contains(_server.Requests, r => r.Path == "lang-audio-en-0.aac"); // no language chosen: the default one
    }

    [Fact]
    public void Dash_Periods_AreJoined_MatchingRepresentations_AndTheirInitSegments()
    {
        var manifest = DashManifest.Parse(Fixture("periods.mpd"), Base);
        Assert.Equal(2, manifest.PeriodCount);
        Assert.Equal(6, manifest.Duration);

        var video = manifest.Representations.Single(r => r.ContentType == "video");
        Assert.Equal(["p1_v1_1.m4s", "p1_v1_2.m4s", "p2_v1_init.mp4", "p2_v1_1.m4s"], video.Segments.Select(s => s.Url.Segments[^1]));
        Assert.Equal("p1_v1_init.mp4", video.Init!.Url.Segments[^1]);

        // The Italian audio has another id in the second period: matched by its language. Its init segment is the same.
        var italian = manifest.Representations.Single(r => r.Id == "a-it");
        Assert.Equal(["p1_a-it_1.m4s", "p1_a-it_2.m4s", "p2_a-it-2_1.m4s"], italian.Segments.Select(s => s.Url.Segments[^1]));
        Assert.Equal("Italiano (doppiaggio)", italian.Label);
        Assert.True(manifest.Representations.Single(r => r.Id == "a-en").IsDefault);
        Assert.Equal("text", manifest.Representations.Single(r => r.Id == "t-en").ContentType);
    }

    [Fact]
    public async Task Dash_Periods_AreDownloadedWhole_InTheChosenLanguage_WithSubtitles()
    {
        var url = Serve("periods.mpd", Dash);
        var video = Segments(["p1_v1_init.mp4", "p1_v1_1.m4s", "p1_v1_2.m4s", "p2_v1_init.mp4", "p2_v1_1.m4s"], size: 3000);
        Segments(["p1_a-en_init.mp4", "p1_a-en_1.m4s", "p1_a-en_2.m4s", "p2_a-en_1.m4s"], size: 500);
        var italian = Segments(["p1_a-it_init.mp4", "p1_a-it_1.m4s", "p1_a-it_2.m4s", "p2_a-it-2_1.m4s"], size: 600);
        ServeText("periods-en.vtt", "WEBVTT\n\n00:00:00.500 --> 00:00:01.000\nOne\n", "text/vtt");
        ServeText("periods-en-2.vtt", "WEBVTT\n\n00:00:00.500 --> 00:00:01.000\nTwo\n", "text/vtt");
        using var store = Store();
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);

        var info = await service.ProbeMediaAsync(new MediaRequest(url));
        Assert.Equal(["a-en", "a-it"], info.AudioTracks!.Select(a => a.Id));
        Assert.Equal("Italiano (doppiaggio)", info.AudioTracks![1].Label);
        Assert.Equal("t-en", Assert.Single(info.Subtitles!).Id);
        Assert.Equal(6, info.DurationSeconds);

        var request = new MediaRequest(url, Title: "Periods", VariantId: info.Variants[0].Id, AudioId: "a-it", SubtitleIds: ["t-en"]);
        var done = await Finished(service, service.AddMedia(request, "dash", "360p"));

        Assert.Equal(DownloadStatus.Completed, done.Status);
        Assert.Equal(video, File.ReadAllBytes(Path.Combine(VideoDir, "Periods.video.mp4")));
        Assert.Equal(italian, File.ReadAllBytes(Path.Combine(VideoDir, "Periods.audio.m4a")));
        var vtt = File.ReadAllText(Path.Combine(VideoDir, "Periods.en.vtt"));
        Assert.Contains("00:00:00.500 --> 00:00:01.000\nOne", vtt);
        Assert.Contains("00:00:04.500 --> 00:00:05.000\nTwo", vtt); // the second period starts at 4 s
    }

    [Fact]
    public void WebVtt_Merge_Dedupe_AndSrt()
    {
        var cues = WebVtt.Cues(["WEBVTT\n\n1\n00:00:02.000 --> 00:00:03.000\nB\n", "WEBVTT\r\n\r\n00:00:01.000 --> 00:00:02.000 line:0\r\nA\r\nsecond line\r\n\r\n00:00:02.000 --> 00:00:03.000\nB\n"]);
        Assert.Equal(["A\nsecond line", "B"], cues.Select(c => c.Text));
        Assert.Equal("1\r\n00:00:01,000 --> 00:00:02,000\r\nA\r\nsecond line\r\n\r\n2\r\n00:00:02,000 --> 00:00:03,000\r\nB\r\n\r\n", WebVtt.ToSrt(cues));
        Assert.StartsWith("WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nA", WebVtt.ToVtt(cues));
        Assert.Empty(WebVtt.Cues(["WEBVTT\n\n99999999999:00:00.000 --> 1:00.000\nbad\n"]));
    }

    [Fact]
    public void Validator_ChecksTheNewMediaFields_AndGrab()
    {
        var ok = new ChannelMessage(Messages.AddMedia, Version: 3, Media: new MediaRequest("https://v.test/m.m3u8", VariantId: "hls:0", AudioId: "it|Italiano", SubtitleIds: ["en|English"], SubtitleMode: "srt"));
        Assert.Null(MessageValidator.Validate(ok));
        Assert.NotNull(MessageValidator.Validate(ok with { Media = ok.Media! with { SubtitleMode = "exe" } }));
        Assert.NotNull(MessageValidator.Validate(ok with { Media = ok.Media! with { SubtitleIds = [.. Enumerable.Repeat("x", 21)] } }));
        Assert.NotNull(MessageValidator.Validate(ok with { Media = ok.Media! with { AudioId = "a\u0001" } }));

        Assert.Null(MessageValidator.Validate(new ChannelMessage(Messages.Grab, Version: 3, Grab: new GrabRequest("https://books.test/", "Libri"))));
        Assert.NotNull(MessageValidator.Validate(new ChannelMessage(Messages.Grab, Version: 3, Grab: new GrabRequest("file:///C:/Windows/"))));
        Assert.NotNull(MessageValidator.Validate(new ChannelMessage(Messages.Grab, Version: 3)));
    }

    [Fact]
    public async Task SubtitlesThatCannotBeDownloaded_DoNotCostTheVideo()
    {
        var url = LanguageStream();
        _server.Add("lang-subs-it-1.vtt", new ServedFile { Content = [], RedirectTo = "/files/gone-for-good" });
        using var store = Store();
        await using var service = new DownloadService(store, engineTemplate: new EngineOptions
        {
            MinSegmentSize = 64 * 1024, RetryBaseDelay = TimeSpan.FromMilliseconds(10), MaxRetries = 1,
            ReadTimeout = TimeSpan.FromSeconds(10), CheckpointInterval = TimeSpan.FromMilliseconds(100),
        }, autoTick: false);

        var request = new MediaRequest(url, Title: "Partial", VariantId: "hls:0", SubtitleIds: ["en|English", "it|Italiano"]);
        var done = await Finished(service, service.AddMedia(request, "hls", "360p"));

        Assert.Equal(DownloadStatus.Completed, done.Status);
        Assert.True(File.Exists(Path.Combine(VideoDir, "Partial.en.vtt")));
        Assert.False(File.Exists(Path.Combine(VideoDir, "Partial.it.vtt")));
        Assert.Contains("Italiano", done.Error);
    }

    [Fact]
    public async Task Subtitles_FollowTheNameTheVideoWasSavedUnder()
    {
        var ffmpeg = FakeFfmpeg(succeed: true);
        var url = LanguageStream();
        Directory.CreateDirectory(VideoDir);
        File.WriteAllText(Path.Combine(VideoDir, "Twice.mp4"), "older");
        using var store = Store(ffmpeg);
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);

        var request = new MediaRequest(url, Title: "Twice", VariantId: "hls:0", SubtitleIds: ["en|English"], SubtitleMode: "vtt");
        var done = await Finished(service, service.AddMedia(request, "hls", "360p"));

        Assert.Equal("Twice (1).mp4", Path.GetFileName(done.FinalPath));
        Assert.True(File.Exists(Path.Combine(VideoDir, "Twice (1).en.vtt")));
    }

    [Fact]
    public async Task DashAudioOnly_KeepsTheChosenQuality_WhateverTheAudioId()
    {
        var mpd = """
            <?xml version="1.0"?>
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT2S"><Period>
              <AdaptationSet contentType="audio" mimeType="audio/mp4" lang="en">
                <Representation id="a-128" bandwidth="128000"><BaseURL>a-128.m4a</BaseURL></Representation>
                <Representation id="a-64" bandwidth="64000"><BaseURL>a-64.m4a</BaseURL></Representation>
              </AdaptationSet></Period></MPD>
            """;
        var url = ServeText("audio-only.mpd", mpd, Dash);
        using var http = new HttpClient();
        var plan = await MediaCatalog.ResolveAsync(http, url, "dash:|a-64", null, CancellationToken.None, audioId: "a-128");
        Assert.EndsWith("a-64.m4a", Assert.Single(Assert.Single(plan.Tracks).Segments).Url.AbsolutePath);
    }

    [Fact]
    public void Dash_SubtitlesOfALaterPeriod_AreMovedToItsStart()
    {
        var manifest = DashManifest.Parse(Fixture("periods.mpd"), Base);
        var text = manifest.Representations.Single(r => r.Id == "t-en");
        Assert.Equal([0, 4], text.Segments.Select(s => s.TimeShift));
        var cues = WebVtt.Cues([("WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nA\n", TimeSpan.Zero), ("WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nB\n", TimeSpan.FromSeconds(4))]);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)], cues.Select(c => c.Start));
    }

    [Fact]
    public async Task Hls_AudioInsideTheVideo_IsTheDefaultChoice_AndAnotherCanBeChosen()
    {
        LanguageStream();
        var master = """
            #EXTM3U
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="a",NAME="Main",LANGUAGE="en",DEFAULT=YES
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="a",NAME="Commentary",LANGUAGE="en",DEFAULT=NO,URI="lang-audio-it.m3u8"
            #EXT-X-STREAM-INF:BANDWIDTH=500000,RESOLUTION=640x360,AUDIO="a"
            lang-video.m3u8
            """;
        var url = ServeText("inband-master.m3u8", master, Hls);
        using var store = Store();
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);

        var info = await service.ProbeMediaAsync(new MediaRequest(url));
        Assert.Equal(["en|Main", "en|Commentary"], info.AudioTracks!.Select(a => a.Id));

        var plain = await Finished(service, service.AddMedia(new MediaRequest(url, Title: "Main", VariantId: "hls:0"), "hls", "360p"));
        Assert.Equal(DownloadStatus.Completed, plain.Status);
        Assert.DoesNotContain(_server.Requests, r => r.Path.StartsWith("lang-audio-it"));
        Assert.Equal("Main.ts", Path.GetFileName(plain.FinalPath)); // one muxed track, no separate audio

        var commentary = await Finished(service, service.AddMedia(new MediaRequest(url, Title: "Commentary", VariantId: "hls:0", AudioId: "en|Commentary"), "hls", "360p"));
        Assert.Equal(DownloadStatus.Completed, commentary.Status);
        Assert.Contains(_server.Requests, r => r.Path == "lang-audio-it-0.aac");
    }
}
