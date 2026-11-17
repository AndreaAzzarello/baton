using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Baton.Core.Integration;

/// <summary>
/// The app was reached and took the message, but no answer came back (the connection closed or broke). The message may have been
/// carried out, so it must not be sent again: unlike "nobody is listening", which is when starting the app and trying again is safe.
/// </summary>
public sealed class ChannelNoReplyException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>The client side of the local pipe the running app listens on: one JSON line each way, per connection.</summary>
public static class ChannelClient
{
    public const int MaxMessageBytes = 8 * 1024 * 1024;

    public static string DefaultPipeName { get; } =
        "Baton." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Environment.UserDomainName}\\{Environment.UserName}")))[..16];

    /// <summary>
    /// Sends one message; returns null when no app is listening (nothing was sent). Throws <see cref="ChannelNoReplyException"/> when it
    /// connected and the answer never came, and <see cref="OperationCanceledException"/> when the answer took longer than
    /// <paramref name="replyTimeout"/> (30 s by default).
    /// </summary>
    public static async Task<ChannelReply?> SendAsync(string pipeName, ChannelMessage message, TimeSpan connectTimeout, CancellationToken ct = default,
        TimeSpan? replyTimeout = null)
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

        try
        {
            await WriteLineAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(message, ProtocolJson.Default.ChannelMessage), ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(replyTimeout ?? TimeSpan.FromSeconds(30));
            var line = await ReadLineAsync(pipe, timeout.Token);
            return line is null ? throw new ChannelNoReplyException("The connection closed without an answer.")
                : JsonSerializer.Deserialize(line, ProtocolJson.Default.ChannelReply);
        }
        catch (Exception ex) when (ex is IOException and not ChannelNoReplyException or JsonException)
        {
            throw new ChannelNoReplyException("The connection broke before an answer came.", ex);
        }
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
