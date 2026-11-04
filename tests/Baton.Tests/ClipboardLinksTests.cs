using Baton.Core;
using Baton.Core.Integration;

namespace Baton.Tests;

/// <summary>Which copied text is offered as a download: a lone link to a known kind of file, once.</summary>
public sealed class ClipboardLinksTests
{
    private static readonly List<Category> Categories = Baton.Core.Categories.Defaults();
    private static readonly DateTime Now = new(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("https://example.com/files/setup.exe")]
    [InlineData("  http://example.com/a/movie.MKV?token=1  ")]
    [InlineData("ftp://files.example.com/pub/archive.zip")]
    public void ALoneLinkToAKnownKindOfFile_IsOffered(string text) =>
        Assert.NotNull(new ClipboardLinks().Accept(text, Categories, Now));

    [Theory]
    [InlineData("https://example.com/news/article")]                 // a page, no file
    [InlineData("https://example.com/")]
    [InlineData("https://example.com/readme.unknownext")]            // an extension no category has
    [InlineData("javascript:alert(1).zip")]
    [InlineData("file:///C:/Windows/notepad.exe")]
    [InlineData("see https://example.com/a.zip please")]            // text, not a lone link
    [InlineData("https://example.com/a.zip\r\nhttps://example.com/b.zip")]
    [InlineData("")]
    [InlineData(null)]
    public void AnythingElse_IsIgnored(string? text) =>
        Assert.Null(new ClipboardLinks().Accept(text, Categories, Now));

    [Fact]
    public void LongText_IsIgnoredWithoutBeingParsed() =>
        Assert.Null(new ClipboardLinks().Accept("https://example.com/" + new string('a', ClipboardLinks.MaxLength) + ".zip", Categories, Now));

    [Fact]
    public void TheSameLink_IsNotOfferedAgainForAWhile_ThenIsAgain()
    {
        var links = new ClipboardLinks(TimeSpan.FromMinutes(10));
        const string link = "https://example.com/files/setup.exe";

        Assert.NotNull(links.Accept(link, Categories, Now));
        Assert.Null(links.Accept(link + "#top", Categories, Now.AddMinutes(1)));   // same address, fragment aside
        Assert.NotNull(links.Accept("https://example.com/files/other.exe", Categories, Now.AddMinutes(2)));
        Assert.NotNull(links.Accept(link, Categories, Now.AddMinutes(11)));
    }
}
