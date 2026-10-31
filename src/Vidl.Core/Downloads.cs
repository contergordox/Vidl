using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Vidl.Core;

public static class YouTubeUrl
{
    public static bool TryParse(string? input, out string canonical)
    {
        canonical = "";
        var text = input?.Trim() ?? "";
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.UserInfo) ||
            !uri.IsDefaultPort) return false;
        var host = uri.IdnHost.ToLowerInvariant();
        string? id = null;
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (host is "youtu.be" or "www.youtu.be") id = parts.FirstOrDefault();
        else if (host is "youtube.com" or "www.youtube.com" or "m.youtube.com" or "music.youtube.com")
        {
            if (parts.Length >= 2 && parts[0] is "shorts" or "live" or "embed") id = parts[1];
            else if (uri.AbsolutePath.TrimEnd('/') == "/watch")
                id = uri.Query.TrimStart('?').Split('&').Select(x => x.Split('=', 2))
                    .Where(x => x.Length == 2 && x[0] == "v").Select(x => Uri.UnescapeDataString(x[1])).FirstOrDefault();
        }
        if (id is null || id.Length != 11 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')))
            return false;
        canonical = "https://www.youtube.com/watch?v=" + id;
        return true;
    }
}

public sealed record DownloadProfile(string Kind, int Height = 1080)
{
    public string Label => Kind switch
    {
        "mp3" => "MP3 · Best audio",
        "m4a" => "M4A · Best audio",
        _ => Height == 0 ? "MP4 · Best available" : $"MP4 · {Height}p"
    };

    public IReadOnlyList<string> Arguments()
    {
        if (Kind is "mp3" or "m4a")
            return ["-f", Kind == "m4a" ? "ba[ext=m4a]/ba/b" : "ba/b", "-x", "--audio-format", Kind,
                "--audio-quality", "0", "--embed-metadata"];
        var cap = Height > 0 ? $"[height<={Height}]" : "";
        return ["-f", $"bv*[ext=mp4]{cap}+ba[ext=m4a]/b[ext=mp4]{cap}/bv*{cap}+ba/b{cap}",
            "--merge-output-format", "mp4", "--remux-video", "mp4", "--embed-metadata"];
    }
}

public sealed record VideoInfo(string Id, string Title, string Creator, double Duration, string? Thumbnail);
public sealed record DownloadProgress(double Percent, string Detail, bool Processing = false);
public sealed record ProcessResult(int ExitCode, string Output, string Error);

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string file, IEnumerable<string> arguments,
        Action<string>? onLine, CancellationToken cancellationToken);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string file, IEnumerable<string> arguments,
        Action<string>? onLine, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(file)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(file))!
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        cancellationToken.ThrowIfCancellationRequested();
        process.Start();
        using var registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        var stdout = new System.Text.StringBuilder();
        var stderr = new Queue<string>();
        var readOutput = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (stdout.Length < 16 * 1024 * 1024) stdout.AppendLine(line);
                onLine?.Invoke(line);
            }
        }, CancellationToken.None);
        var readError = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                stderr.Enqueue(line);
                if (stderr.Count > 40) stderr.Dequeue();
                onLine?.Invoke(line);
            }
        }, CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(readOutput, readError);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync(CancellationToken.None);
            }
            await Task.WhenAll(readOutput, readError);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(process.ExitCode, stdout.ToString(), string.Join(Environment.NewLine, stderr));
    }
}

public sealed class DownloadService(string toolsDirectory, IProcessRunner? runner = null)
{
    private readonly IProcessRunner process = runner ?? new ProcessRunner();
    public string ToolsDirectory { get; } = Path.GetFullPath(toolsDirectory);
    public bool IsReady => new[] { "yt-dlp.exe", "ffmpeg.exe", "ffprobe.exe", "node.exe" }
        .All(name => File.Exists(Path.Combine(ToolsDirectory, name)));

    private List<string> BaseArguments() =>
    [
        "--ignore-config", "--no-playlist", "--no-colors", "--newline",
        "--socket-timeout", "20", "--retries", "3", "--fragment-retries", "3",
        "--no-js-runtimes", "--js-runtimes", "node:" + Path.Combine(ToolsDirectory, "node.exe"),
        "--ffmpeg-location", ToolsDirectory
    ];

    public async Task<VideoInfo> InspectAsync(string url, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        var args = BaseArguments();
        args.AddRange(["--skip-download", "--dump-single-json", "--", url]);
        ProcessResult result;
        try { result = await process.RunAsync(Path.Combine(ToolsDirectory, "yt-dlp.exe"), args, null, timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new InvalidOperationException("YouTube took too long to respond. Try this link again in a moment."); }
        if (result.ExitCode != 0) throw new InvalidOperationException(FriendlyError(result.Error));
        using var json = JsonDocument.Parse(result.Output);
        var root = json.RootElement;
        string Get(string key, string fallback = "") => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback : fallback;
        if (root.TryGetProperty("is_live", out var live) && live.ValueKind == JsonValueKind.True)
            throw new InvalidOperationException("This video is live. Try again after the stream has finished.");
        return new(Get("id"), Get("title", "Untitled video"), Get("uploader", Get("channel", "YouTube")),
            root.TryGetProperty("duration", out var duration) && duration.ValueKind == JsonValueKind.Number && duration.TryGetDouble(out var seconds) ? seconds : 0,
            Get("thumbnail"));
    }

    public async Task<string> DownloadAsync(string url, DownloadProfile profile, string folder,
        IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(folder);
        var args = BaseArguments();
        var nameTemplate = profile.Kind == "mp4" ? "%(title).110B [%(id)s] [%(height)sp].%(ext)s" : "%(title).120B [%(id)s].%(ext)s";
        args.AddRange(["--windows-filenames", "--trim-filenames", "150", "--no-overwrites",
            "--paths", Path.GetFullPath(folder), "-o", nameTemplate,
            "--progress", "--progress-delta", "0.25", "--progress-template",
            "download:VIDL_PROGRESS|%(progress._percent_str)s|%(progress._speed_str)s|%(progress._eta_str)s",
            "--print", "after_move:VIDL_FILE|%(filepath)j"]);
        args.AddRange(profile.Arguments());
        args.AddRange(["--", url]);
        string? outputFile = null;
        var result = await process.RunAsync(Path.Combine(ToolsDirectory, "yt-dlp.exe"), args, line =>
        {
            if (TryParseProgress(line, out var update)) progress?.Report(update!);
            else if (line.StartsWith("VIDL_FILE|", StringComparison.Ordinal))
            {
                try { outputFile = JsonSerializer.Deserialize<string>(line[10..]); }
                catch (JsonException) { }
            }
            else if (line.StartsWith("[Merger]", StringComparison.Ordinal) ||
                     line.StartsWith("[ExtractAudio]", StringComparison.Ordinal) ||
                     line.StartsWith("[VideoRemuxer]", StringComparison.Ordinal) ||
                     line.StartsWith("[Metadata]", StringComparison.Ordinal))
                progress?.Report(new(100, profile.Kind == "mp4" ? "Finishing your video…" : "Preparing your audio…", true));
        }, cancellationToken);
        if (result.ExitCode != 0) throw new InvalidOperationException(FriendlyError(result.Error));
        if (string.IsNullOrEmpty(outputFile) || !File.Exists(outputFile))
            throw new InvalidOperationException("The download finished without a usable file. Please retry.");
        return outputFile;
    }

    public static bool TryParseProgress(string line, out DownloadProgress? progress)
    {
        progress = null;
        var pieces = line.Split('|');
        if (pieces.Length != 4 || pieces[0] != "VIDL_PROGRESS" ||
            !double.TryParse(pieces[1].Trim().TrimEnd('%'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var percent) || !double.IsFinite(percent)) return false;
        var detail = new List<string>();
        if (pieces[2].Trim() is { Length: > 0 } speed && speed is not "N/A" and not "NA" and not "Unknown" and not "Unknown B/s") detail.Add(speed);
        if (pieces[3].Trim() is { Length: > 0 } eta && eta is not "N/A" and not "NA" and not "Unknown") detail.Add(eta + " left");
        progress = new(Math.Clamp(percent, 0, 100), string.Join("  ·  ", detail));
        return true;
    }

    public static string FriendlyError(string raw)
    {
        if (raw.Contains("not a bot", StringComparison.OrdinalIgnoreCase) || raw.Contains("Sign in", StringComparison.OrdinalIgnoreCase))
            return "YouTube is asking for a sign-in or verification. This version supports public videos without sign-in; try again later or try another video.";
        if (raw.Contains("private video", StringComparison.OrdinalIgnoreCase)) return "This video is private. Try a public video.";
        if (raw.Contains("unavailable", StringComparison.OrdinalIgnoreCase) || raw.Contains("removed", StringComparison.OrdinalIgnoreCase))
            return "This video is unavailable or restricted. Check the link and try another video.";
        if (raw.Contains("429", StringComparison.OrdinalIgnoreCase)) return "YouTube is limiting requests. Wait a few minutes before trying again.";
        if (raw.Contains("403", StringComparison.OrdinalIgnoreCase)) return "YouTube refused this download. Update the download engine in Preferences, then retry.";
        if (raw.Contains("disk", StringComparison.OrdinalIgnoreCase) || raw.Contains("space", StringComparison.OrdinalIgnoreCase))
            return "The file could not be saved. Check free space and your download folder.";
        if (raw.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) || raw.Contains("Access is denied", StringComparison.OrdinalIgnoreCase))
            return "Vidl cannot write to this folder. Choose another download folder in Preferences.";
        if (raw.Contains("timed out", StringComparison.OrdinalIgnoreCase) || raw.Contains("resolve", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("connection", StringComparison.OrdinalIgnoreCase))
            return "Couldn’t reach YouTube. Check your internet connection, then retry.";
        var last = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(x => x.StartsWith("ERROR:", StringComparison.Ordinal)) ?? raw.Trim();
        if (string.IsNullOrWhiteSpace(last)) return "The download could not be completed. Please retry.";
        return last.Length > 320 ? last[..320] + "…" : last;
    }
}
