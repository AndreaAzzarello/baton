namespace Baton.Core.Integration;

/// <summary>How much MCP and the local API may do (chosen in the settings, off by default). The command line is not subject to it.</summary>
public enum AutomationLevel
{
    Off,
    ReadOnly,
    ReadAdd,
    Extended,
}

/// <summary>
/// What each origin may ask for. The pipe is reachable by any program of the user and the origin is only declared, so this is the
/// rule for well-behaved clients, not a barrier against the user's own programs: the native host forces "browser" on everything
/// the extension sends, so the extension can never reach the new types. Fixed limits, whatever the level: nothing here deletes
/// files from the disk or changes settings, credentials, proxy, rules, folders or updates.
/// </summary>
public static class OriginPolicy
{
    private static readonly string[] BrowserTypes =
        [Messages.Ping, Messages.Add, Messages.Activate, Messages.Queues, Messages.Hello, Messages.ProbeMedia, Messages.AddMedia, Messages.Batch, Messages.Grab];

    /// <summary>The level an origin without the command line's trust needs; null for what only the browser sends.</summary>
    public static AutomationLevel? RequiredLevel(string type) => type switch
    {
        Messages.Ping or Messages.Hello or Messages.Queues or Messages.Version or Messages.List or Messages.Status => AutomationLevel.ReadOnly,
        Messages.Add => AutomationLevel.ReadAdd,
        Messages.Pause or Messages.Resume or Messages.Retry or Messages.Remove or Messages.Open or Messages.QueueStart or Messages.QueueStop => AutomationLevel.Extended,
        _ => null,
    };

    /// <summary>Null when allowed; otherwise why not.</summary>
    public static string? Check(ChannelMessage message, AutomationLevel mcp, AutomationLevel api)
    {
        // A notification's button is authorised by its signature, whoever forwards it.
        if (message.Type == Messages.Link)
            return null;
        var origin = message.Origin ?? Origins.Browser;
        switch (origin)
        {
            case Origins.Browser:
                return BrowserTypes.Contains(message.Type) ? null : "The browser may not do this.";
            case Origins.Cli:
                return RequiredLevel(message.Type) is null ? "The command line may not do this." : null;
            case Origins.Mcp:
            case Origins.Api:
                var level = origin == Origins.Mcp ? mcp : api;
                if (level == AutomationLevel.Off)
                    return "Automation is turned off in the settings.";
                return RequiredLevel(message.Type) is { } needed && level >= needed ? null : "The level chosen in the settings does not allow this.";
            default:
                return "Unknown origin.";
        }
    }
}
