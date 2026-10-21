using System.Text.Json;

namespace Baton.Core;

public sealed class ColumnState
{
    public string Key { get; set; } = "";
    public int DisplayIndex { get; set; }
    public double Width { get; set; }
    public bool Visible { get; set; } = true;
}

/// <summary>How the user arranged the download list: column order, widths, visibility and sorting.</summary>
public sealed class ColumnLayout
{
    public List<ColumnState> Columns { get; set; } = [];

    /// <summary>Column the list is sorted by; null keeps the queue order.</summary>
    public string? SortKey { get; set; }

    public bool SortDescending { get; set; }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    /// <summary>
    /// Reads a saved layout and reconciles it with the columns this version has: unknown columns are dropped,
    /// new ones are added with their defaults, and the order is made contiguous. Damaged input gives the defaults.
    /// </summary>
    public static ColumnLayout Restore(string? json, IReadOnlyList<ColumnState> defaults)
    {
        ColumnLayout? saved = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(json))
                saved = JsonSerializer.Deserialize<ColumnLayout>(json, Json);
        }
        catch (JsonException)
        {
        }

        var columns = new List<ColumnState>();
        foreach (var column in defaults)
        {
            var stored = saved?.Columns.FirstOrDefault(c => c.Key == column.Key);
            columns.Add(new ColumnState
            {
                Key = column.Key,
                DisplayIndex = stored?.DisplayIndex ?? int.MaxValue / 2 + column.DisplayIndex,
                Width = stored is { Width: > 0 and < 5000 } ? stored.Width : column.Width,
                Visible = stored?.Visible ?? column.Visible,
            });
        }
        var ordered = columns.OrderBy(c => c.DisplayIndex).ToList();
        for (var i = 0; i < ordered.Count; i++)
            ordered[i].DisplayIndex = i;

        var sortKey = saved?.SortKey is { } key && defaults.Any(d => d.Key == key) ? key : null;
        return new ColumnLayout { Columns = columns, SortKey = sortKey, SortDescending = sortKey is not null && saved!.SortDescending };
    }
}
