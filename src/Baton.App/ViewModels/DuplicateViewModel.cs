using System.IO;
using Baton.Core.Duplicates;
using Baton.Core.Resources;

namespace Baton.App.ViewModels;

/// <summary>What the user chose in the duplicate dialog.</summary>
public enum DuplicateChoice
{
    /// <summary>Add it with a numbered name ("file (1).zip").</summary>
    AddNumbered,

    /// <summary>The old file goes to the Recycle Bin and the new one replaces it.</summary>
    Overwrite,

    /// <summary>Show the file already there, download nothing.</summary>
    OpenExisting,

    /// <summary>Do not add it (or, for a download that stopped, remove it from the list).</summary>
    Skip,
}

/// <summary>
/// The dialog that says why a download looks like one already there (same address / same name in the folder / same file from
/// another address) and what to do. After the server's answer there is nothing to overwrite yet, so that choice is hidden.
/// </summary>
public sealed class DuplicateViewModel(DuplicateMatch match, bool canOverwrite = true)
{
    public string Reason => match.Reason switch
    {
        DuplicateReason.SameUrl => Strings.DuplicateSameUrl,
        DuplicateReason.SameName => Strings.DuplicateSameName,
        _ => Strings.DuplicateSameFile,
    };

    /// <summary>The folder of the existing file and its name, on two lines: a long path does not push the buttons out.</summary>
    public string ExistingFolder => Path.GetDirectoryName(match.ExistingPath) ?? "";

    public string ExistingName => Path.GetFileName(match.ExistingPath);

    public DuplicateMatch Match => match;

    public bool CanOverwrite => canOverwrite;

    /// <summary>The existing file can be opened only while it is there.</summary>
    public bool CanOpen => File.Exists(match.ExistingPath);

    public DuplicateChoice Choice { get; set; } = DuplicateChoice.Skip;

    /// <summary>Use this choice from now on, without asking (Settings › General).</summary>
    public bool Remember { get; set; }

    /// <summary>The remembered choice, as the setting stores it.</summary>
    public DuplicateAction ChosenAction => Choice switch
    {
        DuplicateChoice.AddNumbered => DuplicateAction.AddNumbered,
        DuplicateChoice.Overwrite => DuplicateAction.Overwrite,
        DuplicateChoice.OpenExisting => DuplicateAction.OpenExisting,
        _ => DuplicateAction.Skip,
    };
}
