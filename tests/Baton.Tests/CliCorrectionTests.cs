using System.Text.Json;
using Baton.Cli;
using Baton.Core.Integration;

namespace Baton.Tests;

/// <summary>Corrections to batonctl from the 1.4.2 review (B1-B10): each test fails on the code that had the defect.</summary>
public sealed class CliCorrectionTests
{
    private sealed class Run
    {
        public StringWriter Out { get; } = new();
        public StringWriter Err { get; } = new();
        public List<ChannelMessage> Sent { get; } = [];

        public async Task<int> ExecuteAsync(string[] args, Func<ChannelMessage, ChannelReply?> app, string input = "", bool italian = false, bool canStart = false)
        {
            var cli = new CliApp(Out, Err, new StringReader(input), message =>
            {
                Sent.Add(message);
                return Task.FromResult(app(message));
            }, () => Task.FromResult(canStart), "1.2.3", "C:\\work", italian);
            return await cli.RunAsync(args);
        }

        public JsonDocument OutJson() => JsonDocument.Parse(Out.ToString());
    }

    // ------------------------------------------------------------------ B2: --json is read before anything else

    [Theory]
    [InlineData("list", "--nope", "--json")]
    [InlineData("--nope", "--json")]
    [InlineData("frobnicate", "--json")]
    public void B2_Parser_KnowsTheJsonFlag_EvenWhenAnErrorComesBeforeIt(params string[] args)
    {
        var parsed = ArgParser.Parse(args);
        Assert.NotNull(parsed.Error);
        Assert.True(parsed.Json);
    }

    [Fact]
    public async Task B2_SyntaxErrors_AndHelp_AreJson_WhenJsonIsAskedForAnywhere()
    {
        var bad = new Run();
        Assert.Equal(2, await bad.ExecuteAsync(["list", "--nope", "--json"], _ => new ChannelReply(true)));
        using (var doc = bad.OutJson())
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("", bad.Err.ToString());

        var help = new Run();
        Assert.Equal(0, await help.ExecuteAsync(["--help", "--json"], _ => new ChannelReply(true)));
        using var helpDoc = help.OutJson();
        Assert.True(helpDoc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("Usage: batonctl", helpDoc.RootElement.GetProperty("help").GetString());
    }

    // ------------------------------------------------------------------ B3: an exception is an exit code, never a crash

    [Theory]
    [InlineData(typeof(IOException), 3)]
    [InlineData(typeof(UnauthorizedAccessException), 3)]
    [InlineData(typeof(TimeoutException), 3)]
    [InlineData(typeof(InvalidOperationException), 1)]
    [InlineData(typeof(FormatException), 1)]
    public async Task B3_AnExceptionFromTheCall_IsExitCode3_WhenBatonCannotBeReached_Otherwise1(Type exception, int exit)
    {
        foreach (var json in new[] { false, true })
        {
            var run = new Run();
            string[] args = json ? ["list", "--json"] : ["list"];
            var code = await run.ExecuteAsync(args, _ => throw (Exception)Activator.CreateInstance(exception, "boom")!);
            Assert.Equal(exit, code);
            if (json)
            {
                using var doc = run.OutJson();
                Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
                Assert.Contains("boom", doc.RootElement.GetProperty("error").GetString());
            }
            else
            {
                Assert.Contains("boom", run.Err.ToString());
            }
        }
    }

    // ------------------------------------------------------------------ B6: a folder that cannot be a folder is refused, with the likely cause

    [Theory]
    [InlineData("D:\\x\"")]        // what the Windows command line makes of  --folder "D:\x\"  (the \" is a quote)
    [InlineData("D:\\x*")]
    [InlineData("D:\\x?y")]
    [InlineData("D:\\a<b")]
    [InlineData("D:\\a|b")]
    public async Task B6_AFolderWithCharactersThatCannotBeInAPath_IsRefused_AndNothingIsSent(string folder)
    {
        var run = new Run();
        Assert.Equal(2, await run.ExecuteAsync(["add", "https://a.test/f.zip", "--folder", folder], _ => new ChannelReply(true, Result: "added", Id: 1)));
        Assert.Empty(run.Sent);
        Assert.Contains("--folder", run.Err.ToString());
        if (folder.Contains('"'))
            Assert.Contains("backslash", run.Err.ToString());  // the hint about a trailing backslash before the closing quote
    }

    [Fact]
    public async Task B6_AFolderWithATrailingBackslash_IsAccepted_WhenTheCommandLineKeptItWhole()
    {
        var run = new Run();
        Assert.Equal(0, await run.ExecuteAsync(["add", "https://a.test/f.zip", "--folder", "D:\\x\\"], _ => new ChannelReply(true, Result: "added", Id: 1)));
        Assert.Equal("D:\\x\\",run.Sent.Single().Download!.Folder);
    }

    // ------------------------------------------------------------------ B4: results already obtained are not lost

    [Fact]
    public async Task B4_WhenBatonDisappearsInTheMiddleOfAnAdd_TheDownloadsAlreadyAdded_AreReported_AndTheRestAreNotAttempted()
    {
        var calls = 0;
        ChannelReply? App(ChannelMessage _) => ++calls == 1 ? new ChannelReply(true, Result: "added", Id: 11) : null;
        string[] urls = ["https://a.test/1.zip", "https://a.test/2.zip", "https://a.test/3.zip"];

        var json = new Run();
        Assert.Equal(3, await json.ExecuteAsync(["add", ..urls, "--json"], App));
        using (var doc = json.OutJson())
        {
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            var results = doc.RootElement.GetProperty("results");
            Assert.Equal(3, results.GetArrayLength());
            Assert.Equal("added", results[0].GetProperty("status").GetString());
            Assert.Equal(11, results[0].GetProperty("id").GetInt64());
            Assert.Equal("error", results[1].GetProperty("status").GetString());
            Assert.Equal("notAttempted", results[2].GetProperty("status").GetString());
        }

        calls = 0;
        var text = new Run();
        Assert.Equal(3, await text.ExecuteAsync(["add", ..urls], App));
        Assert.Contains("added #11  https://a.test/1.zip", text.Out.ToString());
        Assert.Contains("https://a.test/2.zip", text.Err.ToString());
        Assert.Contains("not attempted", text.Err.ToString());
    }
}
