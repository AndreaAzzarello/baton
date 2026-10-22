using Baton.Core;
using Baton.Core.Grabber;
using Baton.Core.Integration;

namespace Baton.Tests;

/// <summary>Phase 7 (security hardening): each test is a hostile case for one point of the security review.</summary>
public sealed class Phase7Tests
{
    // ------------------------------------------------------------------ B3: file names

    [Theory]
    [InlineData("invoice‮fdp.exe", "invoicefdp.exe")]      // right-to-left override: would display as "invoiceexe.pdf"
    [InlineData("a⁦b⁩c​d﻿.txt", "abcd.txt")] // isolates, zero-width space, byte order mark
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("nul", "_nul")]
    [InlineData("com1.tar.gz", "_com1.tar.gz")]
    [InlineData("LPT9 .log", "_LPT9 .log")]
    [InlineData("report.pdf. . ", "report.pdf")]
    [InlineData("..", "download")]
    [InlineData("a<b>c:d\"e|f?g*h", "a_b_c_d_e_f_g_h")]
    [InlineData(@"..\..\Windows\evil.dll", "evil.dll")]
    [InlineData(".con", "_con")]                                    // a device once the leading dot goes
    [InlineData("..nul.txt", "_nul.txt")]
    [InlineData(". aux", "_aux")]
    [InlineData("​.prn.log", "_prn.log")]                      // the dot appears only once the invisible character is gone
    public void DownloadNames_AreSafeAndShowWhatTheyAre(string name, string expected) => Assert.Equal(expected, FileNames.Sanitize(name));

    [Theory]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("x‮gnp.exe", "xgnp.exe")]
    [InlineData("folder. ", "folder")]
    public void GrabberNames_UseTheSameRules(string name, string expected) => Assert.Equal(expected, LocalPaths.Segment(name, isFile: true));

    // ------------------------------------------------------------------ B2 / I3: requests made for a page's video

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.20.30.40", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("169.254.169.254", true)] // cloud metadata
    [InlineData("100.64.0.1", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("::ffff:10.0.0.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("2001:4860:4860::8888", false)]
    [InlineData("64:ff9b::c0a8:101", true)]     // NAT64 of 192.168.1.1
    [InlineData("64:ff9b::808:808", false)]     // NAT64 of 8.8.8.8: how IPv6-only networks reach public sites
    [InlineData("64:ff9b:1::a00:5", true)]      // local-use NAT64 of 10.0.0.5
    [InlineData("2002:7f00:1::", true)]         // 6to4 of 127.0.0.1
    [InlineData("2002:808:808::1", false)]
    [InlineData("::10.0.0.1", true)]            // IPv4-compatible
    [InlineData("198.18.0.1", true)]
    [InlineData("198.20.0.1", false)]
    [InlineData("192.0.0.170", true)]
    [InlineData("192.0.2.1", false)]            // documentation range: not a local network
    public void PrivateAddresses_AreRecognized(string address, bool isPrivate) =>
        Assert.Equal(isPrivate, Core.Net.UrlGuard.IsPrivate(System.Net.IPAddress.Parse(address)));

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add(request);
            var response = answer(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private static HttpResponseMessage Redirect(string to) =>
        new(System.Net.HttpStatusCode.Found) { Headers = { Location = new Uri(to, UriKind.RelativeOrAbsolute) } };

    [Fact]
    public async Task VideoRedirects_AreCheckedAtEveryHop_EvenThroughAProxy()
    {
        // The handler without redirects stands for the network (a proxy would hide the target from the connection check).
        var network = new StubHandler(r => r.RequestUri!.AbsolutePath switch
        {
            "/start" => Redirect("http://10.0.0.5/secret"),
            "/hop" => Redirect("/final"),
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK),
        });
        var ordinary = new StubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        using var client = new HttpClient(new Core.Net.GuardedRedirectHandler(ordinary, network));

        using (var toPrivate = new HttpRequestMessage(HttpMethod.Get, "http://8.8.8.8/start"))
        {
            toPrivate.Options.Set(Core.Net.UrlGuard.PublicOnly, true);
            await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(toPrivate));
        }
        Assert.DoesNotContain(network.Seen, r => r.RequestUri!.Host == "10.0.0.5"); // refused before it was asked

        using (var allowed = new HttpRequestMessage(HttpMethod.Get, "http://8.8.8.8/hop"))
        {
            allowed.Options.Set(Core.Net.UrlGuard.PublicOnly, true);
            allowed.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 99);
            allowed.Headers.Referrer = new Uri("https://page.example/");
            allowed.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", "c2VjcmV0");
            using var response = await client.SendAsync(allowed);
            Assert.Equal("http://8.8.8.8/final", response.RequestMessage!.RequestUri!.AbsoluteUri);
            var last = network.Seen[^1];
            Assert.Equal("bytes=0-99", last.Headers.Range!.ToString());
            Assert.Equal("https://page.example/", last.Headers.Referrer!.AbsoluteUri);
            Assert.Null(last.Headers.Authorization); // a login is never carried to the next hop
        }

        // An ordinary download keeps the handler's own redirects.
        using (var plain = new HttpRequestMessage(HttpMethod.Get, "http://8.8.8.8/start"))
            await client.SendAsync(plain);
        Assert.Single(ordinary.Seen);
    }

    [Theory]
    [InlineData("http://127.0.0.1:9/v.m3u8")]
    [InlineData("http://10.0.0.5/v.m3u8")]
    [InlineData("http://[::1]/v.m3u8")]
    [InlineData("http://localhost/v.m3u8")]
    public async Task PublicPage_CannotMakeTheAppRequestPrivateAddresses(string variant)
    {
        await using var site = await GrabberTestSite.StartAsync();
        site.Set("cdn.test", "/master.m3u8", Playlist($"#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1000\n{variant}\n"));
        using var http = site.CreateClient(new System.Net.CookieContainer());

        var ex = await Assert.ThrowsAsync<DownloadFailedException>(() =>
            Core.Media.MediaCatalog.ProbeAsync(http, "http://cdn.test/master.m3u8", null, "https://page.test/watch", default));
        Assert.Equal(Core.Resources.Strings.MediaErrorPrivate, ex.Message);
        Assert.DoesNotContain(site.Requests, r => r.Path == "/v.m3u8");
    }

    [Fact]
    public async Task ManifestWithAFileAddress_IsRefusedCleanly()
    {
        await using var site = await GrabberTestSite.StartAsync();
        site.Set("cdn.test", "/master.m3u8", Playlist("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1000\nfile:///C:/Windows/win.ini\n"));
        using var http = site.CreateClient(new System.Net.CookieContainer());

        var ex = await Assert.ThrowsAsync<DownloadFailedException>(() =>
            Core.Media.MediaCatalog.ProbeAsync(http, "http://cdn.test/master.m3u8", null, "https://page.test/", default));
        Assert.Contains("file", ex.Message);
    }

    [Fact]
    public async Task Connections_MarkedPublicOnly_NeverReachAPrivateAddress()
    {
        await using var server = await TestFileServer.StartAsync();
        var url = server.Add("x.bin", new ServedFile { Content = [1, 2, 3] });
        using var http = new HttpClient(new System.Net.Http.SocketsHttpHandler
        {
            ConnectCallback = (connection, ct) => Core.Net.UrlGuard.ConnectAsync(connection, viaProxy: false, ct),
        });

        using var marked = new HttpRequestMessage(HttpMethod.Get, url);
        marked.Options.Set(Core.Net.UrlGuard.PublicOnly, true);
        await Assert.ThrowsAsync<HttpRequestException>(() => http.SendAsync(marked));
        using (var plain = await http.GetAsync(url))
            Assert.True(plain.IsSuccessStatusCode); // ordinary downloads to this computer keep working
    }

    [Fact]
    public async Task VideoDownload_FromAPublicPage_CannotFetchSegmentsOnThisComputer()
    {
        await using var server = await TestFileServer.StartAsync();
        var url = server.Add("m.m3u8", new ServedFile
        {
            Content = System.Text.Encoding.UTF8.GetBytes("#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXTINF:2,\ns0.ts\n#EXT-X-ENDLIST\n"),
            ContentType = "application/vnd.apple.mpegurl",
        });
        var dir = Path.Combine(Path.GetTempPath(), "dm-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var store = new DownloadStore(Path.Combine(dir, "state.db"));
            store.SaveSettings(new AppSettings { DownloadFolder = dir, ProxyMode = ProxyMode.None });
            await using var service = new DownloadService(store, autoTick: false);
            var id = service.AddMedia(new MediaRequest(url, "https://public.example/watch", "x", VariantId: "hls:0"), "hls", "x");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (service.GetSnapshot(id) is not { Status: DownloadStatus.Failed } && watch.Elapsed < TimeSpan.FromSeconds(20))
                await Task.Delay(50);
            var failed = service.GetSnapshot(id)!;
            Assert.Equal(DownloadStatus.Failed, failed.Status);
            Assert.Contains(Core.Resources.Strings.MediaErrorPrivate, failed.Error);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    // ------------------------------------------------------------------ B1: forms from another site

    [Theory]
    [InlineData("https://bank.example/export", "https://evil.test/page", true)]       // another site's form: always asked
    [InlineData("https://bank.example/export", null, true)]                          // origin unknown: asked
    [InlineData("https://dl.bank.example/export", "https://www.bank.example/", false)] // same site
    [InlineData("https://bank.co.uk/x", "https://evil.co.uk/", true)]                // a public suffix is not a site
    [InlineData("https://bank.example/export", "http://bank.example/", true)]        // http is another site
    public void FormsFromAnotherSite_AreRecognized(string url, string? referrer, bool crossSite) =>
        Assert.Equal(crossSite, new BrowserDownload(url, Referrer: referrer, Method: "POST").IsCrossSitePost);

    [Fact]
    public void LinksAreNeverTreatedAsForms() =>
        Assert.False(new BrowserDownload("https://bank.example/a.zip", Referrer: "https://evil.test/").IsCrossSitePost);

    // ------------------------------------------------------------------ B5: form fields never in a log

    [Fact]
    public void FormFields_NamedLikePasswordsOrTokens_AreMaskedInLogs()
    {
        var line = Log.Redact("form user=anna&pass=hunter2&pwd=p2&password=p3&access_token=t4&passcode=1234 sent; Cookie: sid=s5");
        foreach (var secret in new[] { "hunter2", "p2", "p3", "t4", "1234", "s5" })
            Assert.DoesNotContain(secret, line);
        Assert.Contains("user=anna", line); // ordinary fields stay readable
    }

    // ------------------------------------------------------------------ B4: ffmpeg

    [Theory]
    [InlineData("track0.ts", "mpegts")]
    [InlineData("track0.mp4", "mp4")]
    [InlineData("track1.m4a", "mp4")]
    [InlineData("s0.vtt", "webvtt")]
    [InlineData("track0.webm", "matroska")]
    public void FfmpegInputs_HaveAnExplicitFormat(string file, string format) => Assert.Equal(format, Core.Media.Ffmpeg.InputFormat(file));

    [Fact]
    public void AudioTrack_ThatIsReallyMpegTs_IsReadAsMpegTs()
    {
        // HLS audio renditions with segments without a known extension are named ".aac", but may hold MPEG-TS packets.
        var dir = Path.Combine(Path.GetTempPath(), "dm-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var ts = new byte[188 * 3];
            for (var i = 0; i < ts.Length; i += 188)
                ts[i] = 0x47;
            File.WriteAllBytes(Path.Combine(dir, "track1.aac"), ts);
            File.WriteAllBytes(Path.Combine(dir, "raw.aac"), [0xFF, 0xF1, 0x50, 0x80, 0x02, 0x1F, 0xFC]); // an ADTS header
            File.WriteAllText(Path.Combine(dir, "list.aac"), "#EXTM3U\n#EXT-X-ENDLIST\n");
            Assert.Equal("mpegts", Core.Media.Ffmpeg.InputFormat(Path.Combine(dir, "track1.aac")));
            Assert.Equal("aac", Core.Media.Ffmpeg.InputFormat(Path.Combine(dir, "raw.aac")));
            Assert.Equal("aac", Core.Media.Ffmpeg.InputFormat(Path.Combine(dir, "list.aac"))); // never "hls"
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task HostileSegment_ThatIsReallyAPlaylist_IsNeverReadAsOne()
    {
        // A "segment" from a web page whose content is an HLS playlist pointing at a local file. ffmpeg is told the format
        // (MPEG-TS) and may only open local files given on the command line: a format it cannot guess, a protocol it cannot use.
        var dir = Path.Combine(Path.GetTempPath(), "dm-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var segment = Path.Combine(dir, "track0.ts");
            File.WriteAllText(segment, "#EXTM3U\n#EXTINF:1,\nfile:///C:/Windows/win.ini\n#EXT-X-ENDLIST\n");
            var log = Path.Combine(dir, "args.txt");
            var fake = Path.Combine(dir, "ffmpeg.cmd");
            File.WriteAllText(fake, $"@echo off\r\n:loop\r\nif \"%~1\"==\"\" goto done\r\n>>\"{log}\" echo %~1\r\nshift\r\ngoto loop\r\n:done\r\nexit /b 1\r\n");
            await Core.Media.Ffmpeg.MuxAsync(fake, [segment], Path.Combine(dir, "out.mp4"), default);
            var args = File.ReadAllLines(log);
            var input = Array.IndexOf(args, "-i");
            Assert.Equal(["-protocol_whitelist", "file", "-f", "mpegts", "-i", segment], args[(input - 4)..(input + 2)]);
            // An unknown track type is not given to ffmpeg at all.
            Assert.NotNull(await Core.Media.Ffmpeg.MuxAsync(fake, [Path.Combine(dir, "track0.m3u8")], Path.Combine(dir, "x.mp4"), default));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    // ------------------------------------------------------------------ B6: proxy password and PAC files

    private const string Pac = "function FindProxyForURL(url, host) { return 'PROXY proxy.attacker.test:8080'; }";

    [Fact]
    public async Task SavedProxyPassword_IsNotOfferedToProxiesChosenByAPacFromPlainHttp()
    {
        await using var server = await TestFileServer.StartAsync();
        var pacUrl = server.Add("proxy.pac", new ServedFile { Content = System.Text.Encoding.UTF8.GetBytes(Pac), ContentType = "application/x-ns-proxy-autoconfig" });
        var settings = new AppSettings { ProxyMode = ProxyMode.Pac, PacUrl = pacUrl, ProxyUser = "me", ProxyPassword = "secret" };
        var resolver = new Core.Net.ProxyResolver(settings);
        await resolver.LoadPacAsync();

        Assert.Null(resolver.PacError);
        Assert.False(resolver.PacTrusted);
        Assert.Null(resolver.Credentials); // the PAC could have been rewritten on the way: no saved password for its proxy
        resolver.Credentials = new System.Net.NetworkCredential("me", "typed"); // typed by the user when the proxy asked
        Assert.NotNull(resolver.Credentials);
    }

    [Fact]
    public async Task PacFromThisComputer_IsTrusted_FromANetworkShareNeverRead()
    {
        var file = Path.Combine(Path.GetTempPath(), "dm-tests", Guid.NewGuid().ToString("N") + ".pac");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, Pac);
        try
        {
            var local = new Core.Net.ProxyResolver(new AppSettings { ProxyMode = ProxyMode.Pac, PacUrl = file, ProxyUser = "me", ProxyPassword = "secret" });
            await local.LoadPacAsync();
            Assert.True(local.PacTrusted);
            Assert.NotNull(local.Credentials);

            foreach (var share in new[] { @"\\attacker.test\share\proxy.pac", "file://attacker.test/share/proxy.pac", @"\\?\UNC\attacker.test\s\p.pac" })
            {
                var remote = new Core.Net.ProxyResolver(new AppSettings { ProxyMode = ProxyMode.Pac, PacUrl = share });
                await remote.LoadPacAsync();
                Assert.Equal(Core.Resources.Strings.PacNotLocal, remote.PacError);
                Assert.Null(remote.Resolve(new Uri("https://example.com/")));
            }
        }
        finally
        {
            File.Delete(file);
        }
    }

    // ------------------------------------------------------------------ I1: notification buttons

    [Fact]
    public void NotificationButton_OpensOnlyTheFileOfItsOwnFinishedDownload()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dm-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var db = Path.Combine(dir, "downloads.db");
        try
        {
            long done, paused;
            using (var store = new DownloadStore(db))
            {
                done = store.Insert(new DownloadRecord { Url = "https://x.test/a.zip", Directory = @"C:\dl", FileName = "a.zip", Status = DownloadStatus.Completed, AddedUtc = DateTime.UtcNow });
                paused = store.Insert(new DownloadRecord { Url = "https://x.test/b.zip", Directory = @"C:\dl", FileName = "b.zip", Status = DownloadStatus.Paused, AddedUtc = DateTime.UtcNow });
            }
            Assert.Equal(@"C:\dl\a.zip", DownloadStore.CompletedFileOf(db, done));
            Assert.Null(DownloadStore.CompletedFileOf(db, paused));   // not finished
            Assert.Null(DownloadStore.CompletedFileOf(db, done + 99)); // removed, or another download
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    // ------------------------------------------------------------------ I5: the project's guard hook fails closed

    [Fact]
    public async Task GuardHook_BlocksWhatItCannotCheck_AndIsConfiguredToBlockOnFailure()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "Baton.slnx")))
            root = root.Parent!;
        var settings = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root.FullName, ".claude", "settings.json")));
        var guard = settings.RootElement.GetProperty("hooks").GetProperty("PreToolUse").EnumerateArray()
            .SelectMany(m => m.GetProperty("hooks").EnumerateArray()).Single(h => h.GetProperty("command").GetString()!.Contains("guard.ps1"));
        Assert.Equal("block", guard.GetProperty("onFailure").GetString());

        var info = new System.Diagnostics.ProcessStartInfo("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{Path.Combine(root.FullName, ".claude", "hooks", "guard.ps1")}\"")
        {
            RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false,
        };
        using var process = System.Diagnostics.Process.Start(info)!;
        await process.StandardInput.WriteAsync("{ not json");
        process.StandardInput.Close();
        // Windows PowerShell can take long to start on a busy CI runner.
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" ? 60 : 20));
        Assert.Equal(2, process.ExitCode);
    }

    // ------------------------------------------------------------------ I2: the app's pipe

    [Fact]
    public async Task PipeFlood_IsLimited_AndIdleConnectionsAreDropped()
    {
        var pipeName = "dm-test-" + Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource();
        var server = AppChannel.ServeAsync(pipeName, _ => Task.FromResult(new ChannelReply(true)), cts.Token, maxConnections: 3,
            readTimeout: TimeSpan.FromSeconds(1));
        var idle = new List<System.IO.Pipes.NamedPipeClientStream>();
        try
        {
            // Three silent clients take every slot: a fourth cannot even connect...
            for (var i = 0; i < 3; i++)
            {
                var client = new System.IO.Pipes.NamedPipeClientStream(".", pipeName, System.IO.Pipes.PipeDirection.InOut,
                    System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
                await client.ConnectAsync(5000);
                idle.Add(client);
            }
            Assert.Null(await AppChannel.SendAsync(pipeName, new ChannelMessage(Messages.Ping), TimeSpan.FromMilliseconds(400)));

            // ...until the silent ones are dropped after the read timeout; then the app answers again.
            await Task.Delay(1500);
            var reply = await AppChannel.SendAsync(pipeName, new ChannelMessage(Messages.Ping), TimeSpan.FromSeconds(5));
            Assert.True(reply?.Ok);
        }
        finally
        {
            idle.ForEach(c => c.Dispose());
            cts.Cancel();
            await server;
        }
    }

    [Fact]
    public async Task PipeShutdown_WhileAMessageIsBeingAnswered_LeavesNoFailedTask()
    {
        var pipeName = "dm-test-" + Guid.NewGuid().ToString("N");
        var gate = new TaskCompletionSource<ChannelReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var server = AppChannel.ServeAsync(pipeName, _ => { entered.TrySetResult(); return gate.Task; }, cts.Token);
        var disposedSemaphore = false;
        EventHandler<UnobservedTaskExceptionEventArgs> watch = (_, e) =>
            disposedSemaphore |= e.Exception.Flatten().InnerExceptions.Any(x => x is ObjectDisposedException { ObjectName: var n } && n.Contains("Semaphore"));
        TaskScheduler.UnobservedTaskException += watch;
        try
        {
            var send = AppChannel.SendAsync(pipeName, new ChannelMessage(Messages.Ping), TimeSpan.FromSeconds(5));
            await entered.Task;
            cts.Cancel(); // the app closes while the answer is still being prepared
            await server;
            gate.SetResult(new ChannelReply(true));
            await send;
            await Task.Delay(300);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.False(disposedSemaphore);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= watch;
        }
    }

    private static SiteResource Playlist(string text) =>
        new() { Body = System.Text.Encoding.UTF8.GetBytes(text), ContentType = "application/vnd.apple.mpegurl" };
}
