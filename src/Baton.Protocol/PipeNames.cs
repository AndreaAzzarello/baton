using System.Security.Cryptography;
using System.Text;

namespace Baton.Core.Integration;

/// <summary>
/// Which pipe the app of a given copy listens on, decided the same way by the app and by its command line: the user's pipe
/// for the installed app; a pipe of its own data folder for a sandboxed run (BATON_DATA) or a portable copy.
/// </summary>
public static class PipeNames
{
    public const string PortableFlag = "portable.flag";
    public const string PortableDataFolder = "Data";

    /// <summary>The normalized folder of BATON_DATA, or of DOWNLOADMANAGER_DATA when it is the only one; null outside a sandbox.</summary>
    public static string? SandboxFolder()
    {
        foreach (var variable in new[] { "BATON_DATA", "DOWNLOADMANAGER_DATA" })
        {
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value)
                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        }
        return null;
    }

    /// <param name="exeFolder">The folder of Baton.exe (the command line sits in the same folder).</param>
    public static string For(string? exeFolder)
    {
        if (SandboxFolder() is { } sandbox)
            return ChannelClient.DefaultPipeName + "." + Hash(sandbox);
        if (exeFolder is not null && File.Exists(Path.Combine(exeFolder, PortableFlag)))
            return ChannelClient.DefaultPipeName + "." + Hash(Path.Combine(exeFolder, PortableDataFolder));
        return ChannelClient.DefaultPipeName;
    }

    private static string Hash(string folder) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(folder.ToLowerInvariant())))[..8];
}
