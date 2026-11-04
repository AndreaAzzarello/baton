using System.Text.Json;
using System.Text.Json.Serialization;

namespace Baton.Core.Integration;

/// <summary>Source-generated JSON for the two messages that cross the pipe and the native host (no reflection: trim and AOT safe).</summary>
[JsonSerializable(typeof(ChannelMessage))]
[JsonSerializable(typeof(ChannelReply))]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public sealed partial class ProtocolJson : JsonSerializerContext;
