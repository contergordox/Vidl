using System.IO.Compression;
using System.Security.Cryptography;
using Vidl.Standalone;

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
    passed++;
}
string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
byte[] MakeZip(Dictionary<string, byte[]> files, string extra = null)
{
    using var stream = new MemoryStream();
    using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
    {
        foreach (var file in files)
        {
            using var output = zip.CreateEntry("Vidl/" + file.Key).Open();
            output.Write(file.Value);
        }
        if (extra != null) { using var output = zip.CreateEntry(extra).Open(); output.WriteByte(1); }
    }
    return stream.ToArray();
}
BundleManifest Manifest(Dictionary<string, byte[]> files, byte[] zip) => new()
{
    Id = Hash(zip),
    Files = files.Select(f => new BundleFile { Path = f.Key, Length = f.Value.Length, Sha256 = Hash(f.Value) }).ToList(),
};
void MustFail(Action action, string name)
{
    bool failed = false;
    try { action(); }
    catch (InvalidDataException) { failed = true; }
    Check(failed, name);
}
var temporary = Path.Combine(Path.GetTempPath(), "Vidl-bundle-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
try
{
    var files = new Dictionary<string, byte[]> { ["Vidl.exe"] = "app"u8.ToArray(), ["tools/test.dll"] = "tool"u8.ToArray(), ["licenses/test.txt"] = "notice"u8.ToArray() };
    byte[] zip = MakeZip(files);
    var manifest = Manifest(files, zip);
    var cache = Path.Combine(temporary, "normal");
    string prepared = BundleExtractor.Prepare(cache, manifest, () => new MemoryStream(zip), (_, _) => { }, CancellationToken.None);
    Check(File.ReadAllText(Path.Combine(prepared, "Vidl.exe")) == "app", "Extract app from embedded package");
    Check(File.ReadAllText(Path.Combine(prepared, "tools/test.dll")) == "tool", "Extract nested tool files");
    Check(BundleExtractor.IsPrepared(prepared, manifest), "Mark a fully verified bundle ready");
    string reused = BundleExtractor.Prepare(cache, manifest, () => throw new Exception("Cached app reopened the archive"), (_, _) => { }, CancellationToken.None);
    Check(reused == prepared, "Reuse the prepared app without extracting again");

    File.Delete(Path.Combine(prepared, "tools/test.dll"));
    Check(!BundleExtractor.IsPrepared(prepared, manifest), "Detect missing cached dependencies");
    BundleExtractor.Prepare(cache, manifest, () => new MemoryStream(zip), (_, _) => { }, CancellationToken.None);
    Check(File.Exists(Path.Combine(prepared, "tools/test.dll")), "Repair an incomplete cache");

    var brokenHash = Manifest(files, zip);
    brokenHash.Files[0].Sha256 = new string('0', 64);
    var badCache = Path.Combine(temporary, "bad-hash");
    MustFail(() => BundleExtractor.Prepare(badCache, brokenHash, () => new MemoryStream(zip), (_, _) => { }, CancellationToken.None), "Reject corrupted bundle contents");
    Check(!Directory.Exists(Path.Combine(badCache, manifest.Id)) && Directory.GetDirectories(badCache).Length == 0, "Clean only the failed extraction staging folder");

    var missing = new Dictionary<string, byte[]>(files);
    missing.Remove("Vidl.exe");
    byte[] missingZip = MakeZip(missing);
    MustFail(() => BundleExtractor.Prepare(Path.Combine(temporary, "missing"), manifest, () => new MemoryStream(missingZip), (_, _) => { }, CancellationToken.None), "Reject missing files in an archive");
    byte[] unexpectedZip = MakeZip(files, "Vidl/extra.exe");
    MustFail(() => BundleExtractor.Prepare(Path.Combine(temporary, "extra"), manifest, () => new MemoryStream(unexpectedZip), (_, _) => { }, CancellationToken.None), "Reject unexpected archive entries");
    byte[] traversalZip = MakeZip(files, "Vidl/../../escape.txt");
    MustFail(() => BundleExtractor.Prepare(Path.Combine(temporary, "traversal"), manifest, () => new MemoryStream(traversalZip), (_, _) => { }, CancellationToken.None), "Reject archive path traversal");
    Check(!File.Exists(Path.Combine(temporary, "escape.txt")), "Keep extraction inside its cache folder");
    byte[] duplicateZip = MakeZip(files, "Vidl/Vidl.exe");
    MustFail(() => BundleExtractor.Prepare(Path.Combine(temporary, "duplicate"), manifest, () => new MemoryStream(duplicateZip), (_, _) => { }, CancellationToken.None), "Reject duplicate archive paths");

    using (var canceled = new CancellationTokenSource())
    {
        bool stopped = false;
        var canceledRoot = Path.Combine(temporary, "cancel");
        try { BundleExtractor.Prepare(canceledRoot, manifest, () => new MemoryStream(zip), (_, _) => canceled.Cancel(), canceled.Token); }
        catch (OperationCanceledException) { stopped = true; }
        Check(stopped && !Directory.Exists(Path.Combine(canceledRoot, manifest.Id)), "Cancel without leaving a ready partial app");
    }

    var updatedFiles = new Dictionary<string, byte[]> { ["Vidl.exe"] = "app"u8.ToArray(), ["tools/yt-dlp.exe"] = new byte[1024 * 1024 + 10] };
    var updatedZip = MakeZip(updatedFiles);
    var updatedManifest = Manifest(updatedFiles, updatedZip);
    var engineRoot = Path.Combine(temporary, "engine");
    string enginePath = BundleExtractor.Prepare(engineRoot, updatedManifest, () => new MemoryStream(updatedZip), (_, _) => { }, CancellationToken.None);
    File.WriteAllBytes(Path.Combine(enginePath, "tools/yt-dlp.exe"), new byte[1024 * 1024 + 20]);
    BundleExtractor.Prepare(engineRoot, updatedManifest, () => throw new Exception("Engine update was overwritten"), (_, _) => { }, CancellationToken.None);
    Check(new FileInfo(Path.Combine(enginePath, "tools/yt-dlp.exe")).Length == 1024 * 1024 + 20, "Preserve download engine updates between launches");

    var parallelRoot = Path.Combine(temporary, "parallel");
    int opened = 0;
    var launches = Enumerable.Range(0, 2).Select(_ => Task.Run(() => BundleExtractor.Prepare(parallelRoot, manifest, () =>
    {
        Interlocked.Increment(ref opened);
        return new MemoryStream(zip);
    }, (_, _) => { }, CancellationToken.None))).ToArray();
    await Task.WhenAll(launches);
    Check(opened == 1 && launches[0].Result == launches[1].Result, "Coordinate simultaneous launches with one extraction");
}
finally
{
    var resolved = Path.GetFullPath(temporary);
    if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new Exception("Unexpected test cleanup path");
    Directory.Delete(resolved, true);
}
Console.WriteLine($"\n{passed} standalone checks passed.");
