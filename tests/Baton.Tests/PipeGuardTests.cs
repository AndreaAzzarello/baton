using System.Diagnostics;
using Baton.Core.Integration;

namespace Baton.Tests;

/// <summary>
/// Without BATON_DATA the pipe is the one of the real app: tests must refuse it by themselves (a test once messaged the installed Baton).
/// </summary>
public sealed class PipeGuardTests
{
    [Fact]
    public async Task SendingToTheUsersOwnPipe_FailsTheTest()
    {
        Assert.True(ChannelClient.ForbidDefaultPipe);   // set by TestIsolation before any test
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ChannelClient.SendAsync(ChannelClient.DefaultPipeName, new ChannelMessage(Messages.Ping), TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public async Task SandboxAndPortablePipes_AreStillAllowed()
    {
        var sandbox = PipeNames.ForSandbox(Path.Combine(Path.GetTempPath(), "dm-tests", Guid.NewGuid().ToString("N")));
        Assert.NotEqual(ChannelClient.DefaultPipeName, sandbox);
        Assert.Null(await ChannelClient.SendAsync(sandbox, new ChannelMessage(Messages.Ping), TimeSpan.FromMilliseconds(50)));   // nobody listens
    }

    [Fact]
    public void EveryTestThatStartsBatonOrBatonctl_SetsBatonData()
    {
        var folder = AppContext.BaseDirectory;
        while (folder is not null && !File.Exists(Path.Combine(folder, "Baton.slnx")))
            folder = Path.GetDirectoryName(folder);
        Assert.NotNull(folder);
        var offenders = Directory.EnumerateFiles(Path.Combine(folder!, "tests"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(@"\obj\") && !f.Contains(@"\bin\") && Path.GetFileName(f) != nameof(PipeGuardTests) + ".cs")
            .Select(f => (f, text: File.ReadAllText(f)))
            .Where(x => x.text.Contains("FindBatonctl()") && x.text.Contains("Process.Start") && !x.text.Contains("BATON_DATA"))
            .Select(x => Path.GetFileName(x.f)).ToList();
        Assert.Empty(offenders);
    }
}
