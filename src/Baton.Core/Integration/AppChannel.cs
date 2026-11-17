using System.IO.Pipes;
using System.Text.Json;

namespace Baton.Core.Integration;

/// <summary>
/// Local named pipe the running app listens on. Each connection carries one JSON line each way.
/// The pipe is restricted to the current Windows user. The client side lives in <see cref="ChannelClient"/>
/// (the command line uses it without the rest of the core).
/// </summary>
public static class AppChannel
{
    /// <summary>Connections served at the same time; more wait (or time out) instead of each holding a buffer.</summary>
    public const int MaxConnections = 8;

    public static string DefaultPipeName => ChannelClient.DefaultPipeName;

    /// <summary>
    /// Accepts connections until cancelled, at most <paramref name="maxConnections"/> at a time. The handler runs once per
    /// message; a client has <paramref name="readTimeout"/> (default 10 s) to send it, so idle connections do not pile up.
    /// </summary>
    public static async Task ServeAsync(string pipeName, Func<ChannelMessage, Task<ChannelReply>> handler, CancellationToken ct,
        int maxConnections = MaxConnections, TimeSpan? readTimeout = null)
    {
        // Not disposed: connections still being answered release their slot after this method has returned.
        var slots = new SemaphoreSlim(maxConnections, maxConnections);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await slots.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            NamedPipeServerStream pipe;
            try
            {
                pipe = CreateServerPipe(pipeName, maxConnections);
            }
            catch (IOException)
            {
                // Every instance busy (or the name briefly taken): the slot goes back, and the next try comes a little later.
                slots.Release();
                try
                {
                    await Task.Delay(200, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                continue;
            }
            try
            {
                await pipe.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                slots.Release();
                return;
            }
            _ = HandleConnectionAsync(pipe, handler, readTimeout ?? TimeSpan.FromSeconds(10), ct).ContinueWith(_ => slots.Release(), TaskScheduler.Default);
        }
    }

    /// <summary>
    /// The pipe only the current user may open, and never a remote client: besides the current user's own entry the
    /// access list has an explicit refusal for the NETWORK group (S-1-5-2), so a connection that arrives over the
    /// network is turned away even with the same account's credentials.
    /// </summary>
    public static NamedPipeServerStream CreateServerPipe(string pipeName, int maxConnections)
    {
        if (!OperatingSystem.IsWindows())
            return new NamedPipeServerStream(pipeName, PipeDirection.InOut, maxConnections,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, maxConnections,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, BuildSecurity());
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static PipeSecurity BuildSecurity()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var network = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.NetworkSid, null);
        security.AddAccessRule(new PipeAccessRule(network, PipeAccessRights.FullControl, System.Security.AccessControl.AccessControlType.Deny));
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        security.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize | PipeAccessRights.CreateNewInstance, System.Security.AccessControl.AccessControlType.Allow));
        return security;
    }

    /// <summary>Sends one message; returns null when no app is listening.</summary>
    public static Task<ChannelReply?> SendAsync(string pipeName, ChannelMessage message, TimeSpan connectTimeout, CancellationToken ct = default) =>
        ChannelClient.SendAsync(pipeName, message, connectTimeout, ct);

    private static async Task HandleConnectionAsync(NamedPipeServerStream pipe, Func<ChannelMessage, Task<ChannelReply>> handler, TimeSpan readTimeout,
        CancellationToken ct)
    {
        await using (pipe)
        {
            try
            {
                using var reading = CancellationTokenSource.CreateLinkedTokenSource(ct);
                reading.CancelAfter(readTimeout);
                var line = await ChannelClient.ReadLineAsync(pipe, reading.Token);
                if (line is null)
                    return;

                ChannelReply reply;
                try
                {
                    var message = JsonSerializer.Deserialize(line, ProtocolJson.Default.ChannelMessage)
                                  ?? throw new JsonException("Empty message.");
                    // The pipe is reachable by any program of this user, not only the native host: checked again.
                    reply = MessageValidator.Validate(message) is { } error
                        ? new ChannelReply(false, Error: error, Protocol: Messages.ProtocolVersion, Code: ReplyCode.Invalid)
                        : await handler(message);
                }
                catch (Exception ex) when (ex is JsonException or ArgumentException)
                {
                    reply = new ChannelReply(false, Error: ex.Message, Code: ReplyCode.Invalid);
                }
                using var writing = CancellationTokenSource.CreateLinkedTokenSource(ct);
                writing.CancelAfter(TimeSpan.FromSeconds(10));
                await ChannelClient.WriteLineAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(reply, ProtocolJson.Default.ChannelReply), writing.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // The client went away; nothing to answer.
            }
        }
    }
}
