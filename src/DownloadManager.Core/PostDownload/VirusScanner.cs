using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace DownloadManager.Core.PostDownload;

/// <summary>
/// Runs an antivirus command line on a finished file: by default Windows Defender's
/// <c>MpCmdRun.exe -Scan -ScanType 3 -File "{file}"</c>. The file itself is never run: it is only an argument, passed
/// as one element of the argument list (no shell, so no quoting tricks), and the scanner may not be the file.
/// A scan that takes longer than the time allowed is stopped with its whole process tree.
/// </summary>
public static class VirusScanner
{
    public const string FilePlaceholder = "{file}";
    public const string DefaultArguments = "-Scan -ScanType 3 -File \"{file}\"";

    /// <summary>MpCmdRun: 0 = no threat, 2 = threat found.</summary>
    public const string DefaultThreatCodes = "2";

    public static string DefaultScanner =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe");

    /// <summary>The scanner to run: the one configured, else Windows Defender's.</summary>
    public static string ResolveScanner(string? configured) =>
        string.IsNullOrWhiteSpace(configured) ? DefaultScanner : Environment.ExpandEnvironmentVariables(configured.Trim().Trim('"'));

    /// <summary>Why the scanner cannot be used, or null.</summary>
    public static string? Check(string scanner)
    {
        if (!Path.IsPathFullyQualified(scanner))
            return Resources.Strings.ScanErrorNotFullPath;
        if (!File.Exists(scanner))
            return Resources.Strings.ScanErrorNotFound;
        // Only programs: a .cmd or .bat would go through cmd.exe, which interprets "&" and similar in the file's name.
        return Path.GetExtension(scanner).Equals(".exe", StringComparison.OrdinalIgnoreCase) ? null : Resources.Strings.ScanErrorNotProgram;
    }

    /// <summary>The arguments, split as a command line would be, with {file} replaced inside each one.</summary>
    public static List<string> BuildArguments(string template, string file)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var any = false;
        foreach (var c in string.IsNullOrWhiteSpace(template) ? DefaultArguments : template)
        {
            if (c == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any)
                    result.Add(current.ToString());
                current.Clear();
                any = false;
            }
            else
            {
                current.Append(c);
                any = true;
            }
        }
        if (any)
            result.Add(current.ToString());
        // A template without the placeholder still names the file, as the last argument.
        if (!result.Any(a => a.Contains(FilePlaceholder, StringComparison.Ordinal)))
            result.Add(FilePlaceholder);
        return result.Select(a => a.Replace(FilePlaceholder, file, StringComparison.Ordinal)).ToList();
    }

    public static async Task<(ScanOutcome Outcome, string? Detail)> ScanAsync(string scanner, string arguments, string threatCodes, string file,
        TimeSpan timeout, CancellationToken ct)
    {
        if (Check(scanner) is { } problem)
            return (ScanOutcome.Failed, problem);
        if (string.Equals(Path.GetFullPath(scanner), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase))
            return (ScanOutcome.Failed, Resources.Strings.ScanErrorSelf);

        var info = new ProcessStartInfo(scanner)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = Path.GetDirectoryName(scanner)!,
        };
        foreach (var argument in BuildArguments(arguments, file))
            info.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = info };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (ScanOutcome.Failed, ex.Message);
        }
        try
        {
            process.StandardInput.Close(); // nothing to answer: a scanner that asks a question gets end of input
        }
        catch (IOException)
        {
            // The scanner already exited and closed its end.
        }
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            ct.ThrowIfCancellationRequested();
            return (ScanOutcome.TimedOut, null);
        }
        var text = ((await output) + "\n" + (await errors)).Trim();
        var last = text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
        if (last is { Length: > 300 })
            last = last[..300];
        var threats = threatCodes.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(c => int.TryParse(c, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : int.MinValue);
        if (process.ExitCode == 0)
            return (ScanOutcome.Clean, last);
        if (threats.Contains(process.ExitCode))
            return (ScanOutcome.Threat, last);
        return (ScanOutcome.Failed, string.Format(CultureInfo.CurrentCulture, Resources.Strings.ScanExitCode, process.ExitCode) + (last is null ? "" : ": " + last));
    }
}
