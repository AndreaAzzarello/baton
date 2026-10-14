using System.Diagnostics;

namespace DownloadManager.Core.Media;

/// <summary>
/// ffmpeg, if the user has it: used only to copy the downloaded video and audio into one MP4 container, without
/// re-encoding. It is never bundled or downloaded by the app.
/// </summary>
public static class Ffmpeg
{
    /// <summary>The configured ffmpeg.exe if it exists, else the first one in PATH; null when there is none.</summary>
    public static string? Find(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var path = Environment.ExpandEnvironmentVariables(configured.Trim().Trim('"'));
            if (Directory.Exists(path))
                path = Path.Combine(path, "ffmpeg.exe");
            return File.Exists(path) ? Path.GetFullPath(path) : null;
        }
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(folder.Trim().Trim('"'), OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry.
            }
        }
        return null;
    }

    /// <summary>
    /// Copies the streams of <paramref name="inputs"/> (one muxed file, or a video and an audio file) into
    /// <paramref name="output"/> (MP4). Returns null on success, otherwise ffmpeg's last error lines.
    /// </summary>
    public static async Task<string?> MuxAsync(string ffmpeg, IReadOnlyList<string> inputs, string output, CancellationToken ct)
    {
        var info = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
        };
        foreach (var arg in new[] { "-hide_banner", "-nostdin", "-loglevel", "error", "-y" })
            info.ArgumentList.Add(arg);
        foreach (var input in inputs)
        {
            info.ArgumentList.Add("-i");
            info.ArgumentList.Add(input);
        }
        if (inputs.Count > 1)
        {
            // The first input's video and the second input's audio.
            foreach (var arg in new[] { "-map", "0:v:0", "-map", "1:a:0" })
                info.ArgumentList.Add(arg);
        }
        foreach (var arg in new[] { "-c", "copy", "-movflags", "+faststart", output })
            info.ArgumentList.Add(arg);

        using var process = Process.Start(info) ?? throw new InvalidOperationException("ffmpeg could not be started.");
        process.StandardInput.Close();
        var errors = process.StandardError.ReadToEndAsync(ct);
        _ = process.StandardOutput.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
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
            throw;
        }
        if (process.ExitCode == 0 && File.Exists(output))
            return null;
        var text = (await errors).Trim();
        var lines = text.Split('\n').TakeLast(3);
        return string.Join(" ", lines).Trim() is { Length: > 0 } message ? message : $"ffmpeg exit code {process.ExitCode}";
    }
}
