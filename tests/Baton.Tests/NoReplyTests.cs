using System.IO.Pipes;
using Baton.Cli;
using Baton.Core.Integration;

namespace Baton.Tests;

/// <summary>
/// A connection that closes after the message was taken is not "Baton is closed": starting the app and sending again would do an "add" twice
/// (or bring back an app the user just quit). Only "nobody is listening" may start the app and send again.
/// </summary>
public sealed class NoReplyTests
{
    private static string NewPipe() => "Baton.test." + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Client_NobodyListening_ReturnsNull()
    {
        Assert.Null(await ChannelClient.SendAsync(NewPipe(), new ChannelMessage(Messages.Ping), TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public async Task Client_ConnectedButTheServerClosesWithoutAnAnswer_Throws_NotNull()
    {
        var name = NewPipe();
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var serving = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            await ChannelClient.ReadLineAsync(server, CancellationToken.None); // takes the message...
            server.Disconnect();                                              // ...and closes without answering
        });
        await Assert.ThrowsAsync<ChannelNoReplyException>(() =>
            ChannelClient.SendAsync(name, new ChannelMessage(Messages.Add), TimeSpan.FromSeconds(5)));
        await serving;
    }

    [Fact]
    public async Task Client_ASlowAnswer_IsATimeout_WithAShortReplyTimeout()
    {
        var name = NewPipe();
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var serving = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            await ChannelClient.ReadLineAsync(server, CancellationToken.None);
            await Task.Delay(1500); // never answers in time
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ChannelClient.SendAsync(name, new ChannelMessage(Messages.List), TimeSpan.FromSeconds(5), replyTimeout: TimeSpan.FromMilliseconds(200)));
        await serving;
    }

    [Fact]
    public async Task Cli_ConnectionClosedWithoutAnAnswer_IsNotSentAgain_AndTheAppIsNotStarted()
    {
        var sent = 0;
        var starts = 0;
        var output = new StringWriter();
        var cli = new CliApp(output, new StringWriter(), new StringReader(""), _ => { sent++; throw new ChannelNoReplyException("closed"); },
            () => { starts++; return Task.FromResult(true); }, "1.2.3", "C:\\work");
        Assert.Equal(1, await cli.RunAsync(["add", "https://a.test/f.zip", "--allow-duplicate", "--json"]));
        Assert.Equal(1, sent);
        Assert.Equal(0, starts);
        Assert.Contains("may have been done", output.ToString());
    }

    [Fact]
    public async Task Cli_NobodyListening_StartsTheApp_AndSendsOnce_AfterwardsOnly()
    {
        var sent = 0;
        var starts = 0;
        var cli = new CliApp(new StringWriter(), new StringWriter(), new StringReader(""),
            _ => Task.FromResult<ChannelReply?>(++sent == 1 ? null : new ChannelReply(true, Result: "added", Id: 3)),
            () => { starts++; return Task.FromResult(true); }, "1.2.3", "C:\\work");
        Assert.Equal(0, await cli.RunAsync(["add", "https://a.test/f.zip"]));
        Assert.Equal((2, 1), (sent, starts)); // the first try found nobody, then the app was started and the message sent
    }

    [Fact]
    public async Task NativeHost_ConnectionClosedWithoutAnAnswer_DoesNotLaunchTheAppOrSendAgain()
    {
        var name = NewPipe();
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var serving = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            await ChannelClient.ReadLineAsync(server, CancellationToken.None);
            server.Disconnect();
        });
        var launches = 0;
        var reply = await NativeHost.ForwardAsync(new ChannelMessage(Messages.Add, Origin: Origins.Browser), name, () => { launches++; return true; }, TimeSpan.FromSeconds(1));
        await serving;
        Assert.False(reply.Ok);
        Assert.True(reply.Running);
        Assert.Equal(0, launches);
    }
}
