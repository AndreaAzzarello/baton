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
}
