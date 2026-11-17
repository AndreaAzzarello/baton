using Baton.Core.Integration;
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

    /// <summary>The words of each code the app answers with, in both languages. The terminal shows the Italian words when Windows is in Italian;
    /// the English words are what the code means (the app's own English text is what <c>--json</c> and an English terminal keep showing).</summary>
    public static Msg ForCode(ReplyCode code) => code switch
    {
        ReplyCode.Invalid => new("The request is not valid.", "La richiesta non è valida."),
        ReplyCode.NotAllowed => new("This is not allowed from here.", "Da qui non è consentito."),
        ReplyCode.AutomationOff => new("Automation is turned off in the settings.", "L'automazione è disattivata nelle impostazioni."),
        ReplyCode.LevelTooLow => new("The level chosen in the settings does not allow this.", "Il livello scelto nelle impostazioni non lo consente."),
        ReplyCode.TooManyRequests => new("Too many requests: wait a moment.", "Troppe richieste: attendi un momento."),
        ReplyCode.NoDownload => new("No download with this number.", "Nessun download con questo numero."),
        ReplyCode.NoQueue => new("No queue with this name.", "Nessuna coda con questo nome."),
        ReplyCode.NoCategory => new("No category with this name.", "Nessuna categoria con questo nome."),
        ReplyCode.MissingId => new("A download number is needed.", "Serve il numero di un download."),
        ReplyCode.PrivateNetwork => new("This address is on this computer or a private network.", "Questo indirizzo è su questo computer o su una rete privata."),
        ReplyCode.BadScheme => new("Only http, https and ftp addresses can be downloaded.", "Si possono scaricare solo indirizzi http, https e ftp."),
        ReplyCode.FolderRefused => new("This origin may not choose a folder.", "Da qui non si può scegliere la cartella."),
        ReplyCode.CliOnly => new("Only the command line may do this.", "Solo la riga di comando può farlo."),
        ReplyCode.NoFile => new("The file is not there.", "Il file non c'è."),
        ReplyCode.CannotRestart => new("This download cannot be restarted.", "Questo download non si può riavviare."),
        ReplyCode.UnknownType => new("Unknown message type.", "Tipo di messaggio sconosciuto."),
        ReplyCode.ProbeFailed => new("The address could not be checked.", "Non è stato possibile controllare l'indirizzo."),
        ReplyCode.Unresolvable => new("The name of this address cannot be resolved.", "Il nome di questo indirizzo non si risolve."),
        ReplyCode.NotFinished => new("The download is not finished.", "Il download non è finito."),
        ReplyCode.TooManyPending => new("Too many downloads of agents are still waiting.", "Troppi download degli agenti sono ancora in attesa."),
        ReplyCode.Busy => new("Baton is busy; try again in a moment.", "Baton è occupato; riprova tra un momento."),
        ReplyCode.AppTooOld => new("This Baton is older than the command needs: update it.", "Questo Baton è più vecchio di quanto serve al comando: aggiornalo."),
        _ => new("The request failed.", "La richiesta non è riuscita."), // Failed, and any code added later
    };

    public static string Skipped(string reason, long? existingId, bool italian) =>
        italian ? $"saltato (duplicato: {reason}{(existingId is { } id ? $", #{id}" : "")})" : $"skipped (duplicate: {reason}{(existingId is { } id2 ? $", #{id2}" : "")})";
}
