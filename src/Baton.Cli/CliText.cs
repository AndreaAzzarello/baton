using System.Runtime.InteropServices;

namespace Baton.Cli;

/// <summary>
/// A message for people in both languages. <c>--json</c> and MCP always print <see cref="En"/> (stable, with the codes next to it);
/// the terminal shows <see cref="It"/> when Windows is in Italian. A text that comes from the app or from a user (already what it is)
/// converts from a plain string and is shown as it is in both.
/// </summary>
internal sealed record Msg(string En, string It)
{
    public static implicit operator Msg(string text) => new(text, text);

    public string For(bool italian) => italian ? It : En;

    public override string ToString() => En;
}

/// <summary>
/// The messages of batonctl for people reading the terminal (English, and Italian when Windows is in Italian). The protocol carries
/// stable codes (<c>sameUrl</c>, <c>sameName</c>, <c>sameFile</c>); the words are only for the terminal. With --json the codes and
/// the English texts are printed as they are.
/// </summary>
internal static class CliText
{
    [DllImport("kernel32.dll")]
    private static extern ushort GetUserDefaultUILanguage();

    /// <summary>True when the user's Windows display language is Italian (the CLI is built without culture data, so it asks Windows).</summary>
    public static bool WindowsIsItalian()
    {
        try
        {
            return (GetUserDefaultUILanguage() & 0x3FF) == 0x10; // LANG_ITALIAN
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Why an address was skipped as a duplicate; an unknown code (a newer Baton) is shown as it is.</summary>
    public static string DuplicateReason(string? code, bool italian) => code switch
    {
        "sameUrl" => italian ? "stesso indirizzo già in elenco" : "same address already in the list",
        "sameName" => italian ? "nella cartella c'è già un file con lo stesso nome" : "a file with the same name is already in the folder",
        "sameFile" => italian ? "stesso file da un altro indirizzo (stessa dimensione e versione)" : "same file from another address (same size and version)",
        _ => code ?? "?",
    };

    public static string Skipped(string reason, long? existingId, bool italian) =>
        italian ? $"saltato (duplicato: {reason}{(existingId is { } id ? $", #{id}" : "")})" : $"skipped (duplicate: {reason}{(existingId is { } id2 ? $", #{id2}" : "")})";
}
