namespace Baton.Core;

/// <summary>
/// The order of the main window's toolbar buttons, stored as ids separated by commas ("|" = separator, "~" = the gap
/// after which buttons sit on the right). Unknown ids are dropped, so a layout saved by another version still loads.
/// </summary>
public static class ToolbarLayout
{
    public const string Separator = "|";
    public const string Spacer = "~";

    public static readonly string[] Buttons =
        ["add", "start", "pause", "enqueue", "remove", "deletefiles", "progress", "properties", "up", "down", "queue", "folder", "categories", "queues", "grabber", "settings"];

    public const string Default = "add,|,start,pause,enqueue,remove,progress,properties,|,up,down,|,queue,~,folder,categories,queues,grabber,settings";

    /// <summary>The ids in order: each button at most once, exactly one spacer, no empty or doubled separators.</summary>
    public static List<string> Parse(string? text)
    {
        var result = new List<string>();
        foreach (var raw in (string.IsNullOrWhiteSpace(text) ? Default : text).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw == Separator ? result.Count > 0 && result[^1] != Separator && result[^1] != Spacer
                : raw == Spacer ? !result.Contains(Spacer)
                : Buttons.Contains(raw) && !result.Contains(raw))
                result.Add(raw);
        }
        if (!result.Contains(Spacer))
            result.Add(Spacer);
        // A separator right before the spacer or at the end separates nothing.
        for (var i = result.Count - 1; i >= 0; i--)
        {
            if (result[i] == Separator && (i == result.Count - 1 || result[i + 1] == Spacer))
                result.RemoveAt(i);
        }
        return result;
    }

    public static string Format(IEnumerable<string> ids) => string.Join(",", ids);
}
