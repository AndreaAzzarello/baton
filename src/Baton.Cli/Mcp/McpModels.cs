using System.Text.Json;
using System.Text.Json.Serialization;

namespace Baton.Cli.Mcp;

// What the MCP tools return as structured content. Keys and codes are English and stable; every text that came from outside (a file name,
// an error from a web server) went through AgentText and is only ever a field value, never part of a sentence.

internal sealed record DownloadView(long Id, string Name, string Url, string Status, long Downloaded, long? TotalSize, string? Error, string? Queue,
    string? Category, DateTime AddedUtc, DateTime? CompletedUtc, string? Path);

internal sealed record ListResult(List<DownloadView> Downloads, string? NextCursor);

internal sealed record GetResult(DownloadView Download);

internal sealed record QueuesResult(List<string> Queues);

internal sealed record ProbeResult(string Name, string? ContentType, long? Size, bool Resumable);

/// <summary>The outcome for one address: "added", "duplicate", "failed" or "notAttempted".</summary>
internal sealed record AddItem(string Url, string Status, long? Id, string? DuplicateReason, long? ExistingId, string? Code, string? Message);

internal sealed record AddResult(List<AddItem> Results, int Added, int Duplicates, int Failed, int NotAttempted);

/// <summary>The outcome for one download number: "ok" or "failed".</summary>
internal sealed record IdItem(long Id, string Status, string? Code, string? Message);

internal sealed record IdsResult(List<IdItem> Results, int Done, int Failed);

internal sealed record DoneResult(bool Ok);

internal sealed record ToolError(string Code, string Message);

internal sealed record ErrorResult(ToolError Error);

[JsonSerializable(typeof(ListResult))]
[JsonSerializable(typeof(GetResult))]
[JsonSerializable(typeof(QueuesResult))]
[JsonSerializable(typeof(ProbeResult))]
[JsonSerializable(typeof(AddResult))]
[JsonSerializable(typeof(IdsResult))]
[JsonSerializable(typeof(DoneResult))]
[JsonSerializable(typeof(ErrorResult))]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
internal sealed partial class McpJsonContext : JsonSerializerContext;
