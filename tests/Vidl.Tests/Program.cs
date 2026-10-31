using System.Diagnostics;
using System.Text.Json;
using Vidl.Core;

if (args.FirstOrDefault() == "--child") { Console.WriteLine("child-started"); await Task.Delay(30000); return; }

if (args.FirstOrDefault() == "--integration")
{
    var service = new DownloadService(args[1]);
    using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(4));
    var info = await service.InspectAsync(args[2], cancel.Token);
    Console.WriteLine($"Metadata: {info.Title} ({info.Duration}s)");
    var file = await service.DownloadAsync(args[2], new(args.Length > 4 ? args[4] : "mp4", 360), args[3],
        new Progress<DownloadProgress>(p => Console.WriteLine($"{p.Percent:0}% {p.Detail}")), cancel.Token);
    Console.WriteLine("Saved: " + file);
    if (new FileInfo(file).Length == 0) throw new Exception("Empty output file");
    return;
}

var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    passed++;
    Console.WriteLine("PASS: " + name);
}

foreach (var url in new[]
{
    "https://www.youtube.com/watch?v=BaW_jenozKc&list=abc", "https://youtu.be/BaW_jenozKc?t=1",
    "https://youtube.com/shorts/BaW_jenozKc", "https://m.youtube.com/watch?v=BaW_jenozKc",
    "https://music.youtube.com/watch?v=BaW_jenozKc", "youtube.com/watch?v=BaW_jenozKc",
    " https://youtube.com/live/BaW_jenozKc ", "https://youtube.com/embed/BaW_jenozKc"
}) Check(YouTubeUrl.TryParse(url, out var canonical) && canonical == "https://www.youtube.com/watch?v=BaW_jenozKc", "Normalize " + url);

foreach (var url in new[]
{
    "https://youtube.com.evil.example/watch?v=BaW_jenozKc", "https://evil.example/youtube.com/watch?v=BaW_jenozKc",
    "file:///etc/passwd", "--exec calc.exe", "https://youtube.com/playlist?list=abc",
    "https://youtube.com/watch?v=x", "https://youtube.com/watch?v=BaW_jenozK!", "",
    "https://user@youtube.com/watch?v=BaW_jenozKc", "https://youtube.com:444/watch?v=BaW_jenozKc"
}) Check(!YouTubeUrl.TryParse(url, out _), "Reject " + url);

Check(DownloadService.TryParseProgress("VIDL_PROGRESS| 24.2%| 1.5MiB/s|00:12", out var update)
    && update!.Percent == 24.2 && update.Detail.Contains("00:12 left"), "Parse download progress");
Check(DownloadService.TryParseProgress("VIDL_PROGRESS|120%|N/A|Unknown", out update) && update!.Percent == 100 && update.Detail == "", "Clamp and omit unknown progress");
Check(!DownloadService.TryParseProgress("VIDL_PROGRESS|NaN|N/A|N/A", out _), "Reject NaN progress");
Check(DownloadService.TryParseProgress("VIDL_PROGRESS|0%|Unknown B/s|NA", out update) && update!.Detail == "", "Hide engine placeholder values");
Check(!DownloadService.TryParseProgress("[download] noise", out _), "Ignore other lines");
Check(new DownloadProfile("mp4", 720).Arguments().Any(x => x.Contains("[height<=720]")), "Apply video quality cap");
Check(!new DownloadProfile("mp4", 0).Arguments().Any(x => x.Contains("height<=")), "Best video has no height cap");
Check(new DownloadProfile("mp3").Arguments().Contains("mp3"), "MP3 conversion requested");
Check(new DownloadProfile("m4a").Arguments().Contains("m4a"), "M4A conversion requested");
Check(DownloadService.FriendlyError("ERROR: Sign in to confirm you're not a bot").Contains("verification"), "Explain YouTube verification");
Check(DownloadService.FriendlyError("ERROR: HTTP Error 403: Forbidden").Contains("Update"), "Explain stale engine failure");

var temporary = Path.Combine(Path.GetTempPath(), "Vidl-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
try
{
    var saved = Path.Combine(temporary, "a [test].mp4");
    await File.WriteAllTextAsync(saved, "fixture");
    var fake = new FakeRunner
    {
        Output = JsonSerializer.Serialize(new { id = "test", title = "A test video", uploader = "Test channel", duration = 10.5 }),
        Lines = ["VIDL_PROGRESS|50%|1MiB/s|00:01", "[Merger] Merging formats", "VIDL_FILE|" + JsonSerializer.Serialize(saved)]
    };
    var engine = new DownloadService(temporary, fake);
    var info = await engine.InspectAsync("https://www.youtube.com/watch?v=BaW_jenozKc", CancellationToken.None);
    Check(info.Title == "A test video" && info.Duration == 10.5, "Read video metadata");
    fake.Output = "{\"id\":\"test\",\"title\":\"No duration\",\"duration\":null}";
    info = await engine.InspectAsync("test", CancellationToken.None);
    Check(info.Duration == 0, "Accept metadata without a duration");
    var reports = new List<DownloadProgress>();
    var file = await engine.DownloadAsync("https://www.youtube.com/watch?v=BaW_jenozKc", new("mp4"), temporary,
        new InlineProgress(reports.Add), CancellationToken.None);
    Check(file == saved && reports.Any(x => x.Processing), "Track final file after postprocessing");
    Check(fake.LastArguments.Contains("--ignore-config") && fake.LastArguments.Contains("--no-playlist"), "Isolate app from machine config and playlists");
    Check(fake.LastArguments[^2] == "--", "Separate URL from process options");
    Check(fake.LastArguments.Contains("%(title).110B [%(id)s] [%(height)sp].%(ext)s"), "Keep different video resolutions as separate files");
    fake.Lines = [];
    try { await engine.DownloadAsync("test", new("mp4"), temporary, null, CancellationToken.None); Check(false, "Missing output rejected"); }
    catch (InvalidOperationException) { Check(true, "Missing output rejected"); }
    fake.Output = "{\"is_live\":true}";
    try { await engine.InspectAsync("test", CancellationToken.None); Check(false, "Reject active livestream"); }
    catch (InvalidOperationException) { Check(true, "Reject active livestream"); }
    fake.ExitCode = 1;
    fake.Error = "ERROR: HTTP Error 429";
    try { await engine.InspectAsync("test", CancellationToken.None); Check(false, "Surface failed process"); }
    catch (InvalidOperationException ex) { Check(ex.Message.Contains("limiting"), "Surface failed process"); }

    var ownProcess = Environment.ProcessPath!;
    using var cancel = new CancellationTokenSource();
    var sawChild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var childTask = new ProcessRunner().RunAsync(ownProcess, ["--child"], line => { if (line == "child-started") sawChild.TrySetResult(); }, cancel.Token);
    await sawChild.Task.WaitAsync(TimeSpan.FromSeconds(15));
    var timer = Stopwatch.StartNew();
    cancel.Cancel();
    try { await childTask; Check(false, "Cancel real subprocess"); }
    catch (OperationCanceledException) { Check(timer.Elapsed < TimeSpan.FromSeconds(5), "Cancel real subprocess"); }
}
finally { Directory.Delete(temporary, recursive: true); }
Console.WriteLine($"\n{passed} checks passed.");

sealed class InlineProgress(Action<DownloadProgress> action) : IProgress<DownloadProgress>
{ public void Report(DownloadProgress value) => action(value); }

sealed class FakeRunner : IProcessRunner
{
    public string Output { get; set; } = "";
    public string Error { get; set; } = "";
    public int ExitCode { get; set; }
    public string[] Lines { get; set; } = [];
    public List<string> LastArguments { get; private set; } = [];
    public Task<ProcessResult> RunAsync(string file, IEnumerable<string> arguments, Action<string>? onLine, CancellationToken cancellationToken)
    {
        LastArguments = arguments.ToList();
        foreach (var line in Lines) onLine?.Invoke(line);
        return Task.FromResult(new ProcessResult(ExitCode, Output, Error));
    }
}
