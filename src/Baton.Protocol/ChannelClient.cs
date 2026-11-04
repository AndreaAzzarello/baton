using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Baton.Core.Integration;

/// <summary>The client side of the local pipe the running app listens on: one JSON line each way, per connection.</summary>
public static class ChannelClient
{
    public const int MaxMessageBytes = 8 * 1024 * 1024;

    public static string DefaultPipeName { get; } =
        "Baton." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Environment.UserDomainName}\\{Environment.UserName}")))[..16];

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

        await WriteLineAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(message, ProtocolJson.Default.ChannelMessage), ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var line = await ReadLineAsync(pipe, timeout.Token);
        return line is null ? null : JsonSerializer.Deserialize(line, ProtocolJson.Default.ChannelReply);
    }

    public static async Task WriteLineAsync(Stream stream, byte[] json, CancellationToken ct)
    {
        await stream.WriteAsync(json, ct);
        await stream.WriteAsync("\n"u8.ToArray(), ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<byte[]?> ReadLineAsync(Stream stream, CancellationToken ct)
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
