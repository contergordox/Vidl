using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Vidl.Standalone
{
    [DataContract]
    public sealed class BundleFile
    {
        public BundleFile() { Path = ""; Sha256 = ""; }
        [DataMember(Name = "path")] public string Path { get; set; }
        [DataMember(Name = "length")] public long Length { get; set; }
        [DataMember(Name = "sha256")] public string Sha256 { get; set; }
    }

    [DataContract]
    public sealed class BundleManifest
    {
        public BundleManifest() { Id = ""; Files = new List<BundleFile>(); }
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "files")] public List<BundleFile> Files { get; set; }

        public static BundleManifest Read(Stream source)
        {
            var serializer = new DataContractJsonSerializer(typeof(BundleManifest));
            var result = serializer.ReadObject(source) as BundleManifest;
            if (result == null) throw new InvalidDataException("The app package is missing its file list.");
            return result;
        }
    }

    public static class BundleExtractor
    {
        private static bool IsHex(string value)
        {
            if (String.IsNullOrEmpty(value) || value.Length != 64) return false;
            foreach (char character in value)
                if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'))) return false;
            return true;
        }

        private static string Inside(string root, string relative)
        {
            if (String.IsNullOrEmpty(relative) || relative.IndexOf(':') >= 0 || relative.IndexOf('\\') >= 0 || relative.StartsWith("/", StringComparison.Ordinal))
                throw new InvalidDataException("The app package contains an invalid file path.");
            foreach (var part in relative.Split('/'))
                if (part == ".." || part == ".") throw new InvalidDataException("The app package contains an invalid file path.");
            string destination = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));
            string prefix = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The app package contains an invalid file path.");
            return destination;
        }

        private static void RejectLink(string path)
        {
            if ((Directory.Exists(path) || File.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Vidl's app folder cannot be a filesystem link. Choose another cache folder.");
        }

        public static bool IsPrepared(string directory, BundleManifest manifest)
        {
            RejectLink(directory);
            string marker = System.IO.Path.Combine(directory, ".ready");
            RejectLink(marker);
            if (!File.Exists(marker) || File.ReadAllText(marker) != manifest.Id) return false;
            foreach (var file in manifest.Files)
            {
                string destination = Inside(directory, file.Path);
                RejectLink(System.IO.Path.GetDirectoryName(destination));
                RejectLink(destination);
                if (!File.Exists(destination)) return false;
                long length = new FileInfo(destination).Length;
                // Engine updates replace yt-dlp; keep the updated copy on later launches.
                if (file.Path.Equals("tools/yt-dlp.exe", StringComparison.OrdinalIgnoreCase))
                {
                    if (length < 1024 * 1024) return false;
                }
                else if (length != file.Length) return false;
            }
            return true;
        }

        public static string Prepare(string cacheRoot, BundleManifest manifest, Func<Stream> openArchive,
            Action<int, string> progress, CancellationToken cancellationToken)
        {
            if (manifest == null || !IsHex(manifest.Id) || manifest.Files == null || manifest.Files.Count < 1 || manifest.Files.Count > 128)
                throw new InvalidDataException("The app package has an invalid file list.");
            var expected = new Dictionary<string, BundleFile>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var file in manifest.Files)
            {
                Inside(cacheRoot, file.Path);
                if (file.Length < 0 || !IsHex(file.Sha256) || expected.ContainsKey(file.Path))
                    throw new InvalidDataException("The app package has an invalid file list.");
                expected.Add(file.Path, file);
                total = checked(total + file.Length);
            }
            if (total > 1024L * 1024 * 1024) throw new InvalidDataException("The app package is too large.");
            cacheRoot = System.IO.Path.GetFullPath(cacheRoot);
            RejectLink(cacheRoot);
            Directory.CreateDirectory(cacheRoot);
            string destinationRoot = Inside(cacheRoot, manifest.Id);
            using (var mutex = new Mutex(false, "Local\\VidlBundle_" + HashText(cacheRoot + manifest.Id)))
            {
                bool acquired;
                try { acquired = mutex.WaitOne(TimeSpan.FromMinutes(3)); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new IOException("Another Vidl launch is still preparing the app. Please try again shortly.");
                try
                {
                    if (IsPrepared(destinationRoot, manifest)) return destinationRoot;
                    cancellationToken.ThrowIfCancellationRequested();
                    string stage = Inside(cacheRoot, manifest.Id + ".tmp-" + Guid.NewGuid().ToString("N"));
                    string backup = "";
                    Directory.CreateDirectory(stage);
                    try
                    {
                        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        long written = 0;
                        using (var source = openArchive())
                        using (var archive = new ZipArchive(source, ZipArchiveMode.Read, false))
                        {
                            if (archive.Entries.Count > 256) throw new InvalidDataException("The app package has too many files.");
                            foreach (var entry in archive.Entries)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                if (!entry.FullName.StartsWith("Vidl/", StringComparison.Ordinal)) throw new InvalidDataException("The app package contains an unexpected folder.");
                                string relative = entry.FullName.Substring(5);
                                if (relative.Length == 0) continue;
                                string target = Inside(stage, relative);
                                if ((entry.ExternalAttributes >> 16 & 0xF000) == 0xA000) throw new InvalidDataException("The app package cannot contain filesystem links.");
                                if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) continue;
                                BundleFile file;
                                if (!expected.TryGetValue(relative, out file) || !seen.Add(relative) || entry.Length != file.Length)
                                    throw new InvalidDataException("The app package file list does not match its contents.");
                                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
                                using (var input = entry.Open())
                                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                                using (var hash = SHA256.Create())
                                {
                                    byte[] buffer = new byte[81920];
                                    long fileBytes = 0;
                                    int count;
                                    while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                                    {
                                        cancellationToken.ThrowIfCancellationRequested();
                                        fileBytes += count;
                                        if (fileBytes > file.Length) throw new InvalidDataException("The app package contains an oversized file.");
                                        output.Write(buffer, 0, count);
                                        hash.TransformBlock(buffer, 0, count, null, 0);
                                        written += count;
                                        progress((int)(written * 100 / Math.Max(total, 1)), relative);
                                    }
                                    hash.TransformFinalBlock(new byte[0], 0, 0);
                                    string actual = BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant();
                                    if (fileBytes != file.Length || actual != file.Sha256)
                                        throw new InvalidDataException("The app package failed its file integrity check. Download Vidl.exe again.");
                                }
                            }
                        }
                        cancellationToken.ThrowIfCancellationRequested();
                        if (seen.Count != expected.Count) throw new InvalidDataException("The app package is missing required files.");
                        File.WriteAllText(System.IO.Path.Combine(stage, ".ready"), manifest.Id, new UTF8Encoding(false));
                        if (Directory.Exists(destinationRoot))
                        {
                            RejectLink(destinationRoot);
                            backup = Inside(cacheRoot, manifest.Id + ".old-" + Guid.NewGuid().ToString("N"));
                            Directory.Move(destinationRoot, backup);
                        }
                        try { Directory.Move(stage, destinationRoot); }
                        catch
                        {
                            if (backup.Length > 0 && !Directory.Exists(destinationRoot)) Directory.Move(backup, destinationRoot);
                            throw;
                        }
                        if (backup.Length > 0) TryRemove(cacheRoot, backup);
                        return destinationRoot;
                    }
                    finally { TryRemove(cacheRoot, stage); }
                }
                finally { mutex.ReleaseMutex(); }
            }
        }

        private static string HashText(string text)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }

        private static void TryRemove(string parent, string directory)
        {
            string resolved = System.IO.Path.GetFullPath(directory);
            string prefix = System.IO.Path.GetFullPath(parent).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unexpected app cache cleanup path.");
            try
            {
                RejectLink(resolved);
                if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
