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
    /// <paramref name="output"/> (MP4 or MKV), with <paramref name="subtitles"/> (WebVTT files) as subtitle tracks
    /// tagged with their language. Returns null on success, otherwise ffmpeg's last error lines.
    /// </summary>
    public static async Task<string?> MuxAsync(string ffmpeg, IReadOnlyList<string> inputs, string output, CancellationToken ct,
        IReadOnlyList<(string Path, string? Language, string Label)>? subtitles = null)
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
        subtitles ??= [];
        try
        {
            foreach (var input in inputs.Concat(subtitles.Select(s => s.Path)))
                AddInput(info, input);
        }
        catch (ArgumentException ex)
        {
            return ex.Message; // nothing is joined: the tracks are kept as they are
        }
        if (inputs.Count > 1)
        {
            // The first input's video and the second input's audio.
            foreach (var arg in new[] { "-map", "0:v:0", "-map", "1:a:0" })
                info.ArgumentList.Add(arg);
        }
        else if (subtitles.Count > 0)
        {
            // With extra inputs the streams must be named: the video and audio of the one input, if it has them.
            foreach (var arg in new[] { "-map", "0:v?", "-map", "0:a?" })
                info.ArgumentList.Add(arg);
        }
        for (var i = 0; i < subtitles.Count; i++)
        {
            info.ArgumentList.Add("-map");
            info.ArgumentList.Add($"{inputs.Count + i}:0");
        }
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("copy");
        if (subtitles.Count > 0)
        {
            // MP4 carries text subtitles as mov_text, Matroska as SubRip: converted, everything else is copied.
            info.ArgumentList.Add("-c:s");
            info.ArgumentList.Add(output.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ? "mov_text" : "srt");
            for (var i = 0; i < subtitles.Count; i++)
            {
                if (Iso639Part2(subtitles[i].Language) is { } language)
                {
                    info.ArgumentList.Add($"-metadata:s:s:{i}");
                    info.ArgumentList.Add($"language={language}");
                }
                info.ArgumentList.Add($"-metadata:s:s:{i}");
                info.ArgumentList.Add($"title={subtitles[i].Label}");
            }
        }
        foreach (var arg in new[] { "-movflags", "+faststart", output })
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

    /// <summary>
    /// One input, read as the format the app wrote it in and only from a local file. The content came from a web page:
    /// left to guess, ffmpeg could take a segment that is really a playlist (#EXTM3U) and follow it to other files or
    /// addresses; with the format given and only the "file" protocol allowed it cannot.
    /// </summary>
    private static void AddInput(ProcessStartInfo info, string path)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("ffmpeg inputs must be files prepared by the app.", nameof(path));
        foreach (var arg in new[] { "-protocol_whitelist", "file", "-f", InputFormat(path), "-i", path })
            info.ArgumentList.Add(arg);
    }

    /// <summary>
    /// The demuxer for a file the app joined or wrote, by its extension; only the formats it produces. An HLS audio track
    /// named after its segments (".aac") may really be MPEG-TS: its sync bytes decide, still among these demuxers only.
    /// </summary>
    public static string InputFormat(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".ts" => "mpegts",
        ".mp4" or ".m4a" => "mp4",
        ".aac" or ".mp3" when IsTransportStream(path) => "mpegts",
        ".aac" => "aac",
        ".mp3" => "mp3",
        ".webm" => "matroska",
        ".vtt" => "webvtt",
        var other => throw new ArgumentException($"Unexpected track format \"{other}\".", nameof(path)),
    };

    /// <summary>MPEG-TS: a 0x47 sync byte at the start of each 188-byte packet (the first two checked).</summary>
    private static bool IsTransportStream(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            Span<byte> head = stackalloc byte[189];
            var read = file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            return read > 0 && head[0] == 0x47 && (read < 189 || head[188] == 0x47);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The three-letter code MP4 and Matroska use for a language ("it" → "ita"); null when unknown.</summary>
    public static string? Iso639Part2(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return null;
        try
        {
            var code = System.Globalization.CultureInfo.GetCultureInfo(language.Trim(), predefinedOnly: true).ThreeLetterISOLanguageName;
            return code.Length == 3 && code.All(char.IsAsciiLetterLower) ? code : null;
        }
        catch (System.Globalization.CultureNotFoundException)
        {
            return null;
        }
    }
}
