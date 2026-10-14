using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DownloadManager.Core.Integration;

/// <summary>
/// Local named pipe the running app listens on. Each connection carries one JSON line each way.
/// The pipe is restricted to the current Windows user.
/// </summary>
public static class AppChannel
{
    private const int MaxMessageBytes = 8 * 1024 * 1024;

    public static string DefaultPipeName { get; } =
        "DownloadManager." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Environment.UserDomainName}\\{Environment.UserName}")))[..16];

    /// <summary>Accepts connections until cancelled. The handler runs once per message.</summary>
    public static async Task ServeAsync(string pipeName, Func<ChannelMessage, Task<ChannelReply>> handler, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                return;
            }
            _ = HandleConnectionAsync(pipe, handler, ct);
        }
    }

    /// <summary>Sends one message; returns null when no app is listening.</summary>
    public static async Task<ChannelReply?> SendAsync(string pipeName, ChannelMessage message, TimeSpan connectTimeout, CancellationToken ct = default)
    {
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(connectTimeout, ct);
        }
        catch (TimeoutException)
        {
            return null;
        }

        await WriteLineAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(message, Messages.Json), ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var line = await ReadLineAsync(pipe, timeout.Token);
        return line is null ? null : JsonSerializer.Deserialize<ChannelReply>(line, Messages.Json);
    }

    private static async Task HandleConnectionAsync(NamedPipeServerStream pipe, Func<ChannelMessage, Task<ChannelReply>> handler, CancellationToken ct)
    {
        await using (pipe)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                var line = await ReadLineAsync(pipe, timeout.Token);
                if (line is null)
                    return;

                ChannelReply reply;
                try
                {
                    var message = JsonSerializer.Deserialize<ChannelMessage>(line, Messages.Json)
                                  ?? throw new JsonException("Empty message.");
                    // The pipe is reachable by any program of this user, not only the native host: checked again.
                    reply = MessageValidator.Validate(message) is { } error
                        ? new ChannelReply(false, Error: error, Protocol: Messages.ProtocolVersion)
                        : await handler(message);
                }
                catch (Exception ex) when (ex is JsonException or ArgumentException)
                {
                    reply = new ChannelReply(false, Error: ex.Message);
                }
                await WriteLineAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(reply, Messages.Json), timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // The client went away; nothing to answer.
            }
        }
    }

    private static async Task WriteLineAsync(Stream stream, byte[] json, CancellationToken ct)
    {
        await stream.WriteAsync(json, ct);
        await stream.WriteAsync("\n"u8.ToArray(), ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<byte[]?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var one = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(one, ct);
            if (read == 0)
                return buffer.Length > 0 ? buffer.ToArray() : null;
            if (one[0] == (byte)'\n')
                return buffer.ToArray();
            if (buffer.Length >= MaxMessageBytes)
                throw new IOException("Message too large.");
            buffer.WriteByte(one[0]);
        }
    }
}
