using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Baton.Core;
using Baton.Core.Integration;

namespace Baton.Tests;

/// <summary>A24: the pipe turns away remote clients explicitly, and an elevated Baton takes no orders from agents.</summary>
public sealed class PipeSecurityTests
{
    [Fact]
    public void ThePipe_DeniesTheNetworkGroup_AndAllowsOnlyTheCurrentUser()
    {
        Assert.True(OperatingSystem.IsWindows());
        using var pipe = AppChannel.CreateServerPipe(PipeNames.ForSandbox(Path.Combine(Path.GetTempPath(), "dm-tests", Guid.NewGuid().ToString("N"))), 2);
        var rules = pipe.GetAccessControl().GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();

        var network = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);
        Assert.Contains(rules, r => r.AccessControlType == AccessControlType.Deny && r.IdentityReference.Equals(network));
        var me = WindowsIdentity.GetCurrent().User!;
        var allowed = rules.Where(r => r.AccessControlType == AccessControlType.Allow).Select(r => r.IdentityReference).Distinct().ToList();
        Assert.Equal([me], allowed);   // no Everyone, no Users, no Administrators
    }

    [Fact]
    public async Task AClientOfTheSameUser_StillConnects()
    {
        var name = PipeNames.ForSandbox(Path.Combine(Path.GetTempPath(), "dm-tests", Guid.NewGuid().ToString("N")));
        using var stop = new CancellationTokenSource();
        var server = AppChannel.ServeAsync(name, _ => Task.FromResult(new ChannelReply(true)), stop.Token);
        var reply = await ChannelClient.SendAsync(name, new ChannelMessage(Messages.Ping), TimeSpan.FromSeconds(5));
        Assert.True(reply?.Ok);
        stop.Cancel();
        await server;
    }

    [Theory]
    [InlineData(AutomationLevel.Extended)]
    [InlineData(AutomationLevel.ReadAdd)]
    [InlineData(AutomationLevel.ReadOnly)]
    public void WhileElevated_AgentsGetNoLevelAtAll(AutomationLevel configured)
    {
        Assert.Equal(AutomationLevel.Off, Elevation.Effective(configured, elevated: true));
        Assert.Equal(configured, Elevation.Effective(configured, elevated: false));
    }
}
