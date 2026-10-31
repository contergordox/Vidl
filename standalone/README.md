# Standalone Vidl

This folder builds the single-file Windows release. Users download **Vidl.exe**
and run it; no adjacent tools folder or manual extraction is required.

[Download Vidl.exe](https://github.com/contergordox/Vidl/releases/latest/download/Vidl.exe)

The executable includes the desktop app, .NET desktop runtime, yt-dlp, FFmpeg,
Node.js, and license notices. On first launch it verifies and extracts the included
files to `%LOCALAPPDATA%\Vidl\bundles`. Later launches reuse that private app cache.
The executable can be renamed or moved without moving the cache.

The launcher uses the .NET Framework included with Windows 10/11. The desktop app
includes its own .NET 10 runtime. No Python, Node, FFmpeg, SDK, or administrator
installation is needed. This build is unsigned.

## Build

On Windows x64 with the .NET 10 SDK:

```powershell
.\scripts\build.ps1
.\standalone\build.ps1
```

The first command prepares the portable app. The second checks the launcher,
bundles the app, and writes `standalone/Vidl.exe` plus an optional checksum file.
Generated binaries are ignored by Git. Upload `Vidl.exe` as a GitHub Release asset;
it is too large for GitHub's regular Git file limit.

Standalone checks alone:

```powershell
dotnet run --project standalone/tests/Bundle.Tests.csproj -c Release
```

To verify the real embedded package without opening the app:

```powershell
.\standalone\Vidl.exe --extract-only "C:\path\to\test-cache"
```

`VIDL_BUNDLE_DIR` can override the cache root for isolated verification. The cache
uses a fingerprint of the embedded package, coordinates simultaneous launches,
and repairs missing files. Updated yt-dlp copies are preserved between launches.
Settings and download history stay outside the bundle cache in the normal Vidl
app-data folder. All bundled license notices remain accessible in Preferences.
