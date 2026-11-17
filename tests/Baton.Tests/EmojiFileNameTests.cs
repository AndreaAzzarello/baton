using Baton.Core;

namespace Baton.Tests;

/// <summary>
/// 1.4.2 removes invisible characters from the names of new files, and U+FE0F (the "show as emoji" selector) is one of them:
/// a heart saved as "❤️" is written as "❤". A visible change, listed in the changelog; downloads already in the list keep their name.
/// </summary>
public sealed class EmojiFileNameTests : IAsyncLifetime
{
    private TestFileServer _server = null!;
    private readonly HttpClient _http = DownloadService.CreateHttpClient();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dm-tests", Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync() => _server = await TestFileServer.StartAsync();

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        _http.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("heart❤️.jpg", "heart❤.jpg")]                         // red heart: the emoji selector goes
    [InlineData("rainbow\U0001F3F3️‍\U0001F308.png", "rainbow\U0001F3F3\U0001F308.png")] // a ZWJ sequence is split in its parts
    [InlineData("smile\U0001F600.png", "smile\U0001F600.png")]                       // an ordinary emoji stays
    public void NewFileNames_LoseTheEmojiSelector(string served, string expected) => Assert.Equal(expected, FileNames.Sanitize(served));

    [Fact]
    public async Task ADownloadAlreadyInTheList_KeepsItsName_EvenWithTheSelector()
    {
        Directory.CreateDirectory(_dir);
        var stored = "heart❤️.bin";
        var file = new ServedFile { Content = TestFileServer.RandomBytes(5000) };
        var url = _server.Add("any.bin", file);
        var job = new DownloadJob(new DownloadRecord { Id = 1, Url = url, Directory = _dir, FileName = stored }, _http, new RateLimiter(),
            new EngineOptions { MinSegmentSize = 64 * 1024, RetryBaseDelay = TimeSpan.FromMilliseconds(50), ReadTimeout = TimeSpan.FromSeconds(5) });

        await job.RunAsync(CancellationToken.None);

        Assert.Equal(stored, job.Record.FileName);
        Assert.True(File.Exists(Path.Combine(_dir, stored)));
    }
}
