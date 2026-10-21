using System.Buffers.Binary;
using System.Text.Json;

namespace Baton.Core.Integration;

/// <summary>
/// Browser native messaging host: the browser starts it with stdin/stdout pipes and exchanges
/// JSON messages, each prefixed by its length as a 32-bit little-endian integer.
/// Messages are forwarded to the running app, which is started first when needed.
/// </summary>
public static class NativeHost
{
    public const string Name = "com.andreaazzarello.baton";

    /// <summary>ID of the bundled extension in Chromium browsers, fixed by the public key in its manifest.</summary>
    public const string ExtensionId = "fngmjmgaglmoebphcjohnhlaceogneno";

    /// <summary>ID of the extension in Firefox (browser_specific_settings.gecko.id).</summary>
    public const string FirefoxExtensionId = "baton@andreaazzarello";

    /// <summary>The browser limits messages to the host to 4 MB; "download all" of a big page comes close.</summary>
    private const int MaxIncomingBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Chromium passes the caller's origin as the first argument; Firefox passes the path of the host manifest and
    /// then the extension's ID.
    /// </summary>
    public static bool IsHostInvocation(string[] args) =>
        args.Length > 0 && args[0].StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)
        || args.Length > 1 && args[1] == FirefoxExtensionId && args[0].EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    /// <param name="appVersion">This executable's version, reported to the extension's connection check.</param>
    public static async Task RunAsync(Stream input, Stream output, string pipeName, Func<bool> launchApp, TimeSpan startupTimeout,
        string? appVersion = null)
    {
        while (await ReadMessageAsync(input) is { } json)
        {
            ChannelReply reply;
            try
            {
                var message = JsonSerializer.Deserialize<ChannelMessage>(json, Messages.Json) ?? throw new JsonException("Empty message.");
                // Checked here as well as in the app: an invalid message never even starts the app.
                reply = MessageValidator.Validate(message) is { } error
                    ? new ChannelReply(false, Error: error, Protocol: Messages.ProtocolVersion)
                    : await ForwardAsync(message, pipeName, launchApp, startupTimeout, appVersion);
            }
            catch (JsonException)
            {
                reply = new ChannelReply(false, Error: "Malformed message.", Protocol: Messages.ProtocolVersion);
            }
            await WriteMessageAsync(output, JsonSerializer.SerializeToUtf8Bytes(reply, Messages.Json));
        }
    }

    public static async Task<ChannelReply> ForwardAsync(ChannelMessage message, string pipeName, Func<bool> launchApp, TimeSpan startupTimeout,
        string? appVersion = null)
    {
        var reply = await AppChannel.SendAsync(pipeName, message, TimeSpan.FromMilliseconds(500));
        if (reply is not null)
            return reply;
        // Questions from the popup and the connection check never start the app.
        if (message.Type == Messages.Ping)
            return new ChannelReply(true, Running: false);
        if (message.Type == Messages.Queues)
            return new ChannelReply(true, Running: false, Queues: []);
        if (message.Type == Messages.Hello)
            return new ChannelReply(true, Running: false, Protocol: Messages.ProtocolVersion, AppVersion: appVersion);

        if (!launchApp())
            return new ChannelReply(false, Running: false, Error: "Impossibile avviare Baton.");
        var deadline = DateTime.UtcNow + startupTimeout;
        while (DateTime.UtcNow < deadline)
        {
            reply = await AppChannel.SendAsync(pipeName, message, TimeSpan.FromSeconds(1));
            if (reply is not null)
                return reply;
        }
        return new ChannelReply(false, Running: false, Error: "Baton non ha risposto in tempo.");
    }

    public static async Task<byte[]?> ReadMessageAsync(Stream input)
    {
        var header = new byte[4];
        if (!await ReadExactlyOrEndAsync(input, header))
            return null;
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > MaxIncomingBytes)
            throw new InvalidDataException($"Invalid message length {length}.");
        var body = new byte[length];
        if (!await ReadExactlyOrEndAsync(input, body))
            throw new EndOfStreamException("Truncated message.");
        return body;
    }

    public static async Task WriteMessageAsync(Stream output, byte[] json)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, json.Length);
        await output.WriteAsync(header);
        await output.WriteAsync(json);
        await output.FlushAsync();
    }

    private static async Task<bool> ReadExactlyOrEndAsync(Stream input, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await input.ReadAsync(buffer.AsMemory(total));
            if (read == 0)
                return total == 0 && buffer.Length > 0 ? false : throw new EndOfStreamException("Truncated message.");
            total += read;
        }
        return true;
    }
}
