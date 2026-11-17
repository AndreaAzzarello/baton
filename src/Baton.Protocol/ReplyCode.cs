using System.Text.Json;
using System.Text.Json.Serialization;

namespace Baton.Core.Integration;

/// <summary>
/// Why the app refused or could not do a request (protocol 5). Stable and in English on the wire ("noDownload"); the text next to
/// it in <see cref="ChannelReply.Error"/> is for people and may change. Callers choose status codes and reasons from the code,
/// never from the beginning of a sentence.
/// </summary>
[JsonConverter(typeof(ReplyCodeConverter))]
public enum ReplyCode
{
    /// <summary>Anything not listed below.</summary>
    Failed,
    /// <summary>The message is malformed or not valid.</summary>
    Invalid,
    /// <summary>The origin may not send this type of message.</summary>
    NotAllowed,
    /// <summary>The level of the origin of the call is Off.</summary>
    AutomationOff,
    /// <summary>The level of the origin of the call does not reach what the message needs.</summary>
    LevelTooLow,
    TooManyRequests,
    NoDownload,
    NoQueue,
    NoCategory,
    MissingId,
    PrivateNetwork,
    BadScheme,
    FolderRefused,
    CliOnly,
    NoFile,
    CannotRestart,
    UnknownType,
    ProbeFailed,
    /// <summary>The name of the address cannot be resolved (or took too long), so it cannot be shown to be public.</summary>
    Unresolvable,
    /// <summary>The download is not finished: an agent may not remove it from the list (that would delete its partial file).</summary>
    NotFinished,
    /// <summary>Too many downloads of the agents are still waiting: an agent may not add more until some finish.</summary>
    TooManyPending,
    /// <summary>The app is running but cannot take the message now (every connection of its pipe is in use); try again in a moment.</summary>
    Busy,
    /// <summary>The app speaks a protocol older than the caller needs (set by the caller, never by the app).</summary>
    AppTooOld,
}

/// <summary>Writes the codes as camelCase words.</summary>
public sealed class ReplyCodeConverter() : JsonStringEnumConverter<ReplyCode>(JsonNamingPolicy.CamelCase);
