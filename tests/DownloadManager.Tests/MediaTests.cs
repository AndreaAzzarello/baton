using DownloadManager.Core;
using DownloadManager.Core.Integration;
using DownloadManager.Core.Media;
using Microsoft.Data.Sqlite;

namespace DownloadManager.Tests;

/// <summary>
/// HLS and DASH: parsing (local fixtures), the qualities offered and their sizes, what is refused (DRM, live,
/// restricted sites), and whole downloads through the service: segments in parallel, joined, kept on pause, muxed
/// by ffmpeg when there is one.
/// </summary>
public sealed partial class MediaTests : IAsyncLifetime
{
    private TestFileServer _server = null!;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dm-tests", Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_dir, "state.db");
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Media");
    private static readonly Uri Base = new("https://media.test/path/");

    private static readonly EngineOptions Fast = new()
    {
        MinSegmentSize = 64 * 1024,
        RetryBaseDelay = TimeSpan.FromMilliseconds(50),
        ReadTimeout = TimeSpan.FromSeconds(10),
        CheckpointInterval = TimeSpan.FromMilliseconds(100),
    };

    public async Task InitializeAsync()
    {
        _server = await TestFileServer.StartAsync();
        Directory.CreateDirectory(_dir);
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(Fixtures, name));

    private string Serve(string fixture, string type)
        => _server.Add(fixture, new ServedFile { Content = File.ReadAllBytes(Path.Combine(Fixtures, fixture)), ContentType = type });

    private const string Hls = "application/vnd.apple.mpegurl";
    private const string Dash = "application/dash+xml";

    /// <summary>Serves segments with distinct content and returns what joining them must give.</summary>
    private byte[] Segments(IEnumerable<string> names, int size = 40_000, Func<long, int>? throttle = null)
    {
        var all = new MemoryStream();
        var seed = 100;
        foreach (var name in names)
        {
            var content = TestFileServer.RandomBytes(size + seed, seed++);
            _server.Add(name, new ServedFile { Content = content, ContentType = "video/mp2t", Throttle = throttle ?? (_ => 0) });
            all.Write(content);
        }
        return all.ToArray();
    }

    private DownloadStore Store(string? ffmpeg = null, int connections = 4)
    {
        var store = new DownloadStore(DbPath);
        store.SaveSettings(new AppSettings
        {
            DownloadFolder = _dir,
            MaxConnections = connections,
            ProxyMode = ProxyMode.None,
            // A path that does not exist: no ffmpeg, whatever is installed on the test machine.
            FfmpegPath = ffmpeg ?? Path.Combine(_dir, "no-ffmpeg", "ffmpeg.exe"),
        });
        return store;
    }

    /// <summary>Video downloads go to the Video category's folder, inside the download folder.</summary>
    private string VideoDir => Path.Combine(_dir, "Video");

    private string? PartsDir => Directory.Exists(VideoDir) ? Directory.GetDirectories(VideoDir).SingleOrDefault() : null;

    private static async Task WaitUntil(Func<bool> condition, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition not reached in time.");
            await Task.Delay(30);
        }
    }

    private static async Task<DownloadSnapshot> Finished(DownloadService service, long id)
    {
        await WaitUntil(() => service.GetSnapshot(id)!.Status is DownloadStatus.Completed or DownloadStatus.Failed);
        return service.GetSnapshot(id)!;
    }

    // ------------------------------------------------------------------ parsing

    [Fact]
    public void Hls_Master_ListsQualities_WithQuotedCommas_AndIgnoresIFrameStreams()
    {
        var master = HlsPlaylist.Parse(Fixture("ts-master.m3u8"), Base);
        Assert.True(master.IsMaster);
        Assert.Equal(2, master.Variants.Count);
        var hd = master.Variants[1];
        Assert.Equal(new Uri(Base, "v720.m3u8"), hd.Url);
        Assert.Equal((1280, 720), (hd.Width!.Value, hd.Height!.Value));
        Assert.Equal(2_200_000, hd.Bandwidth);
        Assert.Equal(2_000_000, hd.AverageBandwidth);
        Assert.Equal("avc1.4d401f,mp4a.40.2", hd.Codecs);
    }

    [Fact]
    public void Hls_AudioRendition_DefaultOneIsChosen()
    {
        var master = HlsPlaylist.Parse(Fixture("alt-master.m3u8"), Base);
        var audio = MediaCatalog.ChooseAudio(master, master.Variants[0]);
        Assert.Equal("English", audio!.Name);
        Assert.Equal(new Uri(Base, "alt-audio.m3u8"), audio.Url);
    }

    [Fact]
    public void Hls_MediaPlaylist_ByteRangesContinueFromThePreviousOne()
    {
        var playlist = HlsPlaylist.Parse(Fixture("range.m3u8"), Base);
        Assert.False(playlist.IsMaster);
        Assert.True(playlist.HasEnd);
        Assert.Equal([(0L, 1000L), (1000L, 1500L), (2500L, 700L)], playlist.Segments.Select(s => (s.Offset!.Value, s.Length!.Value)));
        Assert.Equal(3200, playlist.KnownSize);
        Assert.Equal(12, playlist.Duration, 3);
    }

    [Fact]
    public void Hls_InitSection_AndDurations()
    {
        var playlist = HlsPlaylist.Parse(Fixture("alt-video.m3u8"), Base);
        Assert.Equal(new Uri(Base, "alt-vinit.mp4"), playlist.Init!.Url);
        Assert.Equal(15.5, playlist.Duration, 3);
    }

    [Theory]
    [InlineData("aes.m3u8", true)]
    [InlineData("fairplay-master.m3u8", true)]
    [InlineData("clear-key-none.m3u8", false)]
    [InlineData("v720.m3u8", false)]
    public void Hls_KeysMarkTheStreamEncrypted(string fixture, bool encrypted)
    {
        var playlist = HlsPlaylist.Parse(Fixture(fixture), Base);
        Assert.Equal(encrypted, playlist.IsEncrypted);
        if (fixture.StartsWith("fairplay"))
            Assert.Contains("com.apple.streamingkeydelivery", playlist.KeyFormats);
    }

    [Fact]
    public void Hls_WithoutEndList_IsLive()
    {
        Assert.False(HlsPlaylist.Parse(Fixture("live.m3u8"), Base).HasEnd);
        Assert.Throws<FormatException>(() => HlsPlaylist.Parse("<html>not a playlist</html>", Base));
    }

    [Fact]
    public void Dash_TemplateWithNumber_CountsSegmentsFromTheDuration()
    {
        var mpd = DashManifest.Parse(Fixture("number.mpd"), Base);
        Assert.False(mpd.IsProtected);
        Assert.False(mpd.IsLive);
        Assert.Equal(9, mpd.Duration);
        Assert.Equal(4, mpd.Representations.Count(r => r.ContentType != "text")); // the WebVTT subtitles are offered apart
        Assert.Equal("subs", mpd.Representations.Single(r => r.ContentType == "text").Id);
        var hd = mpd.Representations.Single(r => r.Id == "v720");
        Assert.Equal(new Uri(Base, "v720_init.mp4"), hd.Init!.Url);
        Assert.Equal(["v720_001.m4s", "v720_002.m4s", "v720_003.m4s", "v720_004.m4s", "v720_005.m4s"],
            hd.Segments.Select(s => s.Url.Segments.Last()));
        Assert.Equal(1, hd.Segments[^1].Duration, 3); // 9 s = 4 × 2 s + 1 s
        Assert.Equal("a128", MediaCatalog.BestAudio(mpd)!.Id);
    }

    [Fact]
    public void Dash_Timeline_RepeatsEntries_WithTheirTimes_AndBaseUrls()
    {
        var rep = DashManifest.Parse(Fixture("timeline.mpd"), Base).Representations.Single();
        Assert.Equal(["t_0.m4s", "t_90000.m4s", "t_180000.m4s", "t_270000.m4s"], rep.Segments.Select(s => s.Url.Segments.Last()));
        Assert.Equal(new Uri(Base, "media/t_0.m4s"), rep.Segments[0].Url);
        Assert.Equal(0.5, rep.Segments[^1].Duration, 3);
    }

    [Fact]
    public void Dash_SegmentList_WithByteRanges()
    {
        var rep = DashManifest.Parse(Fixture("list.mpd"), Base).Representations.Single();
        Assert.Equal("audio", rep.ContentType);
        Assert.Equal((0L, 100L), (rep.Init!.Offset!.Value, rep.Init.Length!.Value));
        Assert.Equal([(100L, 500L), (600L, 600L)], rep.Segments.Select(s => (s.Offset!.Value, s.Length!.Value)));
        Assert.All(rep.Segments, s => Assert.Equal(new Uri(Base, "voice.mp4"), s.Url));
    }

    [Fact]
    public void Dash_ContentProtection_AndDynamic_AreRecognized()
    {
        var protectedMpd = DashManifest.Parse(Fixture("protected.mpd"), Base);
        Assert.True(protectedMpd.IsProtected);
        Assert.Contains("urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed", protectedMpd.ProtectionSchemes); // Widevine
        Assert.True(DashManifest.Parse(Fixture("dynamic.mpd"), Base).IsLive);
    }

    [Fact]
    public void Dash_DocumentTypeDefinitions_AreRefused()
    {
        // An external entity must never make the parser read a local file.
        Assert.ThrowsAny<System.Xml.XmlException>(() => DashManifest.Parse(Fixture("xxe.mpd"), Base));
    }

    [Theory]
    [InlineData("$RepresentationID$/$Number%05d$.m4s", "v1/00042.m4s")]
    [InlineData("seg-$Time$-$Bandwidth$.m4s", "seg-9000-128000.m4s")]
    [InlineData("a$$b-$Number$", "a$b-42")]
    public void Dash_TemplateIdentifiers(string template, string expected) =>
        Assert.Equal(expected, DashManifest.Fill(template, "v1", 128000, 42, 9000));

    // ------------------------------------------------------------------ what is offered

    [Fact]
    public async Task Probe_HlsMaster_OffersQualities_BestFirst_WithEstimatedSizes()
    {
        var url = Serve("ts-master.m3u8", Hls);
        Serve("v720.m3u8", Hls);
        Serve("v360.m3u8", Hls);
        using var http = DownloadService.CreateHttpClient();

        var info = await MediaCatalog.ProbeAsync(http, url, null, null, CancellationToken.None);

        Assert.Null(info.Blocked);
        Assert.Equal("hls", info.Kind);
        Assert.Equal(20, info.DurationSeconds);
        Assert.Equal(["720p", "360p"], info.Variants.Select(v => v.Label));
        Assert.Equal(2_000_000 / 8 * 20, info.Variants[0].EstimatedBytes); // AVERAGE-BANDWIDTH × duration
        Assert.Equal(800_000 / 8 * 20, info.Variants[1].EstimatedBytes);
        Assert.Equal("hls:1", info.Variants[0].Id); // the index in the master playlist
    }

    [Fact]
    public async Task Probe_Dash_PairsEveryVideoWithTheBestAudio()
    {
        var url = Serve("number.mpd", Dash);
        using var http = DownloadService.CreateHttpClient();
        var info = await MediaCatalog.ProbeAsync(http, url, null, null, CancellationToken.None);
        Assert.Equal(["dash:v720|a128", "dash:v360|a128"], info.Variants.Select(v => v.Id));
        Assert.Equal((1_500_000 + 128_000) / 8 * 9, info.Variants[0].EstimatedBytes);
    }

    [Theory]
    [InlineData("aes.m3u8", Hls, MediaBlock.Drm)]
    [InlineData("fairplay-master.m3u8", Hls, MediaBlock.Drm)]
    [InlineData("protected.mpd", Dash, MediaBlock.Drm)]
    [InlineData("live.m3u8", Hls, MediaBlock.Live)]
    [InlineData("dynamic.mpd", Dash, MediaBlock.Live)]
    public async Task Probe_RefusesProtectedAndLiveStreams(string fixture, string type, string reason)
    {
        var url = Serve(fixture, type);
        Serve("v720.m3u8", Hls);
        using var http = DownloadService.CreateHttpClient();
        var info = await MediaCatalog.ProbeAsync(http, url, null, null, CancellationToken.None);
        Assert.Equal(reason, info.Blocked);
        Assert.Empty(info.Variants);
        Assert.False(string.IsNullOrEmpty(info.Message));
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abc", null)]
    [InlineData("https://cdn.example.test/video.m3u8", "https://m.youtube.com/watch?v=abc")]
    [InlineData("https://rr1---sn-x.googlevideo.com/videoplayback?itag=22", null)]
    public async Task Probe_SitesWhoseTermsForbidIt_AreRefusedWithoutAnyRequest(string url, string? page)
    {
        using var http = new HttpClient(new FailingHandler());
        var info = await MediaCatalog.ProbeAsync(http, url, null, page, CancellationToken.None);
        Assert.Equal(MediaBlock.Terms, info.Blocked);
    }

    [Fact]
    public async Task Probe_PlainVideoFile_IsOfferedAsAFile_WithItsSize()
    {
        var url = _server.Add("clip.mp4", new ServedFile { Content = TestFileServer.RandomBytes(300_000, 3), ContentType = "video/mp4" });
        using var http = DownloadService.CreateHttpClient();
        var info = await MediaCatalog.ProbeAsync(http, url, null, null, CancellationToken.None);
        Assert.Equal("file", info.Kind);
        Assert.Equal(300_000, info.Variants.Single().EstimatedBytes);
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("No request may be made for a restricted site.");
    }

    // ------------------------------------------------------------------ downloads

    [Fact]
    public async Task HlsTs_SegmentsInParallel_JoinedInOrder_SavedAsTsWithoutFfmpeg()
    {
        var url = Serve("ts-master.m3u8", Hls);
        Serve("v720.m3u8", Hls);
        Serve("v360.m3u8", Hls);
        var expected = Segments(Enumerable.Range(0, 5).Select(i => $"v720_{i}.ts"), throttle: _ => 300_000);
        using var store = Store();
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);

        // The page is on this computer like the stream: a public page could not make the app fetch it (see Phase7Tests).
        var id = service.AddMedia(new MediaRequest(url, "http://127.0.0.1/watch", "Il mio video: parte 1/2", VariantId: "hls:1"), "hls", "720p");
        var maxConnections = 0;
        await WaitUntil(() =>
        {
            var s = service.GetSnapshot(id)!;
            maxConnections = Math.Max(maxConnections, s.Connections);
            return s.Status is DownloadStatus.Completed or DownloadStatus.Failed;
        });
        var done = service.GetSnapshot(id)!;

        Assert.Equal(DownloadStatus.Completed, done.Status);
        Assert.True(maxConnections > 1, "segments are fetched in parallel");
        Assert.Equal("Il mio video_ parte 1_2.ts", Path.GetFileName(done.FinalPath)); // named after the page, as TS
        Assert.Equal(expected, File.ReadAllBytes(done.FinalPath!));
        Assert.Equal(expected.Length, done.TotalSize);
        Assert.NotNull(done.Error); // the note: saved as .ts because ffmpeg is missing
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(done.FinalPath)!)); // the segments folder is gone
        Assert.DoesNotContain(_server.Requests, r => r.Path.StartsWith("v360_"));
        Assert.All(_server.Requests.Where(r => r.Path.EndsWith(".ts")), r => Assert.Equal("http://127.0.0.1/watch", r.Referer));
    }

    [Fact]
    public async Task HlsAlternateAudio_Fmp4_WithoutFfmpeg_VideoAndAudioAreSavedSideBySide()
    {
        var url = Serve("alt-master.m3u8", Hls);
        Serve("alt-video.m3u8", Hls);
        Serve("alt-audio.m3u8", Hls);
        var video = Segments(["alt-vinit.mp4", "alt-v0.m4s", "alt-v1.m4s", "alt-v2.m4s"]);
        var audio = Segments(["alt-ainit.mp4", "alt-a0.m4s", "alt-a1.m4s", "alt-a2.m4s"], size: 9000);
        using var store = Store();
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);

        var id = service.AddMedia(new MediaRequest(url, Title: "Concerto", VariantId: "hls:0"), "hls", "1080p");
        var done = await Finished(service, id);

        Assert.Equal(DownloadStatus.Completed, done.Status);
        Assert.Equal(video, File.ReadAllBytes(Path.Combine(VideoDir, "Concerto.video.mp4"))); // init section + fragments
        Assert.Equal(audio, File.ReadAllBytes(Path.Combine(VideoDir, "Concerto.audio.m4a")));
        Assert.Contains("Concerto.audio.m4a", done.Error);
        Assert.DoesNotContain(_server.Requests, r => r.Path == "alt-audio-it.m3u8"); // the default rendition only
    }

    [Fact]
    public async Task WithFfmpeg_TheTracksBecomeOneMp4()
    {
        var ffmpeg = FakeFfmpeg(succeed: true);
        var url = Serve("alt-master.m3u8", Hls);
        Serve("alt-video.m3u8", Hls);
        Serve("alt-audio.m3u8", Hls);
        Segments(["alt-vinit.mp4", "alt-v0.m4s", "alt-v1.m4s", "alt-v2.m4s", "alt-ainit.mp4", "alt-a0.m4s", "alt-a1.m4s", "alt-a2.m4s"]);
        using var store = Store(ffmpeg);
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);

        var done = await Finished(service, service.AddMedia(new MediaRequest(url, Title: "Muxed", VariantId: "hls:0"), "hls", "1080p"));

        Assert.Equal(DownloadStatus.Completed, done.Status);
        Assert.Null(done.Error);
        Assert.Equal("Muxed.mp4", Path.GetFileName(done.FinalPath));
        Assert.Equal("MUXED", File.ReadAllText(done.FinalPath!).Trim());
        var args = File.ReadAllLines(Path.Combine(Path.GetDirectoryName(ffmpeg)!, "args.txt"));
        Assert.Contains("copy", args); // streams are copied, never re-encoded
        Assert.Contains("1:a:0", args);
        // Every input is read as the format the app wrote, from a local file only (B4): never guessed from its content.
        var inputs = args.Select((a, i) => (a, i)).Where(x => x.a == "-i").Select(x => x.i).ToList();
        Assert.Equal(2, inputs.Count);
        Assert.All(inputs, i => Assert.Equal(["-protocol_whitelist", "file", "-f", "mp4"], args[(i - 4)..i]));
        Assert.Single(Directory.GetFiles(VideoDir, "Muxed*"));
    }

    [Fact]
    public async Task FailingFfmpeg_KeepsTheTracks_AndSaysWhy()
    {
        var ffmpeg = FakeFfmpeg(succeed: false);
        var url = Serve("v720.m3u8", Hls);
        var expected = Segments(Enumerable.Range(0, 5).Select(i => $"v720_{i}.ts"));
        using var store = Store(ffmpeg);
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);

        var done = await Finished(service, service.AddMedia(new MediaRequest(url, Title: "Broken", VariantId: "hls:0"), "hls", null));

        Assert.Equal(DownloadStatus.Completed, done.Status);
        Assert.Equal(expected, File.ReadAllBytes(Path.Combine(VideoDir, "Broken.ts")));
        Assert.Contains("Invalid data found", done.Error);
    }

    [Fact]
    public async Task Dash_TemplateNumber_DownloadsTheChosenVideoAndTheBestAudio()
    {
        var url = Serve("number.mpd", Dash);
        var video = Segments(["v360_init.mp4", .. Enumerable.Range(1, 5).Select(i => $"v360_{i:D3}.m4s")], size: 20_000);
        var audio = Segments(["a128_init.mp4", .. Enumerable.Range(1, 5).Select(i => $"a128_{i:D3}.m4s")], size: 3000);
        using var store = Store();
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);

        var done = await Finished(service, service.AddMedia(new MediaRequest(url, Title: "Dash", VariantId: "dash:v360|a128"), "dash", "360p"));

        Assert.Equal(DownloadStatus.Completed, done.Status);
        Assert.Equal(video, File.ReadAllBytes(Path.Combine(VideoDir, "Dash.video.mp4")));
        Assert.Equal(audio, File.ReadAllBytes(Path.Combine(VideoDir, "Dash.audio.m4a")));
        Assert.DoesNotContain(_server.Requests, r => r.Path.StartsWith("v720_") || r.Path.StartsWith("a64_"));
    }

    [Fact]
    public async Task HlsByteRanges_EachSegmentIsItsOwnRangeOfTheFile()
    {
        var url = Serve("range.m3u8", Hls);
        var whole = TestFileServer.RandomBytes(3200, 9);
        _server.Add("range.ts", new ServedFile { Content = whole, ContentType = "video/mp2t" });
        using var store = Store();
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);

        var done = await Finished(service, service.AddMedia(new MediaRequest(url, Title: "Ranges", VariantId: "hls:0"), "hls", null));

        Assert.Equal(DownloadStatus.Completed, done.Status);
        Assert.Equal(whole, File.ReadAllBytes(done.FinalPath!));
        Assert.Equal([(0L, 999L), (1000L, 2499L), (2500L, 3199L)],
            _server.Requests.Where(r => r.Path == "range.ts").Select(r => (r.From!.Value, r.To!.Value)).OrderBy(r => r.Item1));
    }

    [Fact]
    public async Task Pause_KeepsFinishedSegments_ResumeFetchesOnlyTheRest_AndTheDownloadSurvivesARestart()
    {
        var url = Serve("v720.m3u8", Hls);
        var expected = Segments(Enumerable.Range(0, 5).Select(i => $"v720_{i}.ts"), size: 60_000, throttle: _ => 120_000);
        long id;
        using (var store = Store(connections: 1))
        {
            await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);
            id = service.AddMedia(new MediaRequest(url, Title: "Resume", VariantId: "hls:0"), "hls", null);
            await WaitUntil(() => _server.Requests.Count(r => r.Path.EndsWith(".ts")) >= 3);
            await service.PauseAsync(id);
            Assert.Equal(DownloadStatus.Paused, service.GetSnapshot(id)!.Status);
        }

        var before = _server.Requests.Count(r => r.Path.EndsWith(".ts"));
        var kept = Directory.GetFiles(PartsDir!, "*.seg").Length; // finished segments only
        Assert.InRange(kept, 2, 4);
        using (var store = Store(connections: 3))
        {
            await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);
            Assert.Equal("hls", store.LoadAll().Single().Media!.Kind); // kept across a restart
            service.Start(id);
            var done = await Finished(service, id);
            Assert.Equal(DownloadStatus.Completed, done.Status);
            Assert.Equal(expected, File.ReadAllBytes(done.FinalPath!));
        }
        // Only the segments not finished before the pause were fetched again.
        Assert.Equal(before + (5 - kept), _server.Requests.Count(r => r.Path.EndsWith(".ts")));
    }

    [Fact]
    public async Task Remove_DeletesTheSegmentsFolder()
    {
        var url = Serve("v720.m3u8", Hls);
        Segments(Enumerable.Range(0, 5).Select(i => $"v720_{i}.ts"), size: 60_000, throttle: _ => 60_000);
        using var store = Store(connections: 1);
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);
        var id = service.AddMedia(new MediaRequest(url, Title: "Gone", VariantId: "hls:0"), "hls", null);
        await WaitUntil(() => PartsDir is { } parts && Directory.GetFiles(parts, "*.seg").Length > 0);

        await service.RemoveAsync(id, deleteFile: true);

        Assert.Null(PartsDir);
    }

    [Fact]
    public async Task AddMedia_RefusesRestrictedSites_AndTheUrlOfAStreamCannotBeChanged()
    {
        using var store = Store();
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);
        Assert.Throws<ArgumentException>(() =>
            service.AddMedia(new MediaRequest("https://cdn.test/v.m3u8", "https://www.youtube.com/watch?v=1", VariantId: "hls:0"), "hls", null));

        var url = Serve("v720.m3u8", Hls);
        Segments(Enumerable.Range(0, 5).Select(i => $"v720_{i}.ts"));
        var id = service.AddMedia(new MediaRequest(url, VariantId: "hls:0"), "hls", null, startNow: false);
        await Assert.ThrowsAsync<ArgumentException>(() => service.CheckNewUrlAsync(id, url));
    }

    /// <summary>A stand-in for ffmpeg.exe: a batch file that logs its arguments and writes (or fails to write) the output.</summary>
    private string FakeFfmpeg(bool succeed)
    {
        var folder = Path.Combine(_dir, "tools");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "ffmpeg.cmd");
        File.WriteAllText(path, succeed
            ? "@echo off\r\nset \"log=%~dp0args.txt\"\r\nset \"out=\"\r\n:loop\r\nif \"%~1\"==\"\" goto done\r\nset \"out=%~1\"\r\n>>\"%log%\" echo %~1\r\nshift\r\ngoto loop\r\n:done\r\n>\"%out%\" echo MUXED\r\nexit /b 0\r\n"
            : "@echo off\r\necho [mpegts] Invalid data found when processing input 1>&2\r\nexit /b 1\r\n");
        return path;
    }
}
