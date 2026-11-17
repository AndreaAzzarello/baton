using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Core.Integration;

namespace Baton.Cli;

// The shapes printed by --json. Every field is always there (null when empty), so scripts can rely on them.
internal sealed record ErrorOutput(bool Ok, string Error);

/// <summary>An error with a stable code, for the cases a script can act on ("appTooOld"); other errors keep <see cref="ErrorOutput"/>.</summary>
internal sealed record CodedErrorOutput(bool Ok, string Error, string Code);

internal sealed record HelpOutput(bool Ok, string Help);

internal sealed record VersionOutput(string Cli, string? App, bool Running);

internal sealed record AddResult(string Url, string Status, long? Id, DuplicateInfo? Duplicate, string? Error);

internal sealed record AddOutput(bool Ok, List<AddResult> Results);

internal sealed record ProbeOutput(bool Ok, ProbeInfo? Probe);

internal sealed record ListOutput(bool Ok, List<DownloadInfo> Downloads);

internal sealed record DownloadOutput(bool Ok, DownloadInfo? Download, long? Id)
{
    public int? Count { get; init; }
}

[JsonSerializable(typeof(ErrorOutput))]
[JsonSerializable(typeof(CodedErrorOutput))]
[JsonSerializable(typeof(HelpOutput))]
[JsonSerializable(typeof(VersionOutput))]
[JsonSerializable(typeof(AddOutput))]
[JsonSerializable(typeof(ListOutput))]
[JsonSerializable(typeof(ProbeOutput))]
[JsonSerializable(typeof(DownloadOutput))]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
internal sealed partial class CliJson : JsonSerializerContext;
