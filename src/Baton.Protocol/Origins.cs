namespace Baton.Core.Integration;

/// <summary>Where a message on the pipe comes from. A missing origin is the browser: every message of protocols 1–3 has none.</summary>
public static class Origins
{
    public const string Browser = "browser";
    public const string Cli = "cli";
    public const string Mcp = "mcp";
    public const string Api = "api";

    /// <summary>Set by the app itself for what the clipboard offers; it never comes through the pipe.</summary>
    public const string Clipboard = "clipboard";

    public static readonly string[] Known = [Browser, Cli, Mcp, Api, Clipboard];
}
