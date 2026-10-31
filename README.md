# Vidl

A small Windows app for saving YouTube video and audio to your computer.

## Download and run

1. [Download Vidl.exe](https://github.com/contergordox/Vidl/releases/latest/download/Vidl.exe).
2. Run it. The included app and tools are prepared automatically on first launch.
3. Paste a public YouTube video, Shorts, or music link.
4. Choose MP4 video, MP3 audio, or M4A audio and click **Add to queue**.

Windows 10/11 x64. Only the downloaded EXE is needed. No separate Python, Node,
FFmpeg, SDK, account, or administrator installation is required. The launcher
uses Windows' built-in .NET Framework; the desktop app includes .NET 10.
App files are unpacked to `%LOCALAPPDATA%\Vidl\bundles`, and later launches reuse
that cache. There are no companion files to keep beside the downloaded EXE.
This build is unsigned.

## Included

- MP4 video: best available, 1080p, 720p, 480p, or 360p (up to source quality).
- MP3 audio at best VBR quality; M4A audio with AAC extraction/conversion.
- A sequential queue with live progress, cancellation, retries, and thumbnails.
- Resumable partial downloads when the same link, profile, and folder are reused.
- Video titles, creators, durations, and a **Show file** button.
- A folder picker, remembered format/quality, and optional local download history.
- **Preferences → Update engine** uses yt-dlp's updater to check GitHub.
- Paste with Ctrl+V, press Enter to queue, or drop a link into the app.

Settings/history: `%LOCALAPPDATA%\Vidl\settings.json`.
Default destination: `%USERPROFILE%\Downloads\Vidl`.
Each queued item keeps the destination selected when it was added.
Clearing finished entries removes them from the app's list; it keeps saved files.

Videos use their source codecs inside MP4; some source formats, including AV1,
require a compatible media player. Lower source resolution is never upscaled.
Playlists, active livestreams, private videos, sign-in, and DRM are not supported.
YouTube can restrict requests; Vidl reports these errors and offers retry.
Use content you own or have permission to download.

## Build from source

Install the .NET 10 SDK on Windows x64, then run:

```powershell
.\scripts\build.ps1
```

The script verifies tool checksums, runs the tests, and builds the portable app
in `dist/Vidl`. The download ZIP is written to `dist`.
For the single-file build, run `.\standalone\build.ps1` afterward. See
[the standalone build instructions](standalone/README.md).
The FFmpeg upstream `latest` asset is mutable; if upstream replaces it, the pinned
checksum intentionally fails. Use the verified original archive in `.cache`, or
review a new build and update its URL/checksum before rebuilding.

Core checks alone:

```powershell
dotnet run --project tests/Vidl.Tests -c Release
```

Real download integration check (requires internet; saves the selected content):

```powershell
dotnet run --project tests/Vidl.Tests -c Release -- --integration "C:\path\to\Vidl\tools" "YOUTUBE_VIDEO_URL" "C:\path\to\test-downloads" mp4
```

The app uses .NET/WPF without third-party UI or NuGet libraries. All subprocess
arguments are passed without a shell. Links are normalized to one YouTube video;
unrelated sites and command-like inputs are rejected. Machine-wide yt-dlp config
and playlists are ignored. Cancel stops the downloader and its child processes.

`VIDL_DATA_DIR` and `VIDL_TOOLS_DIR` can override the settings/tools locations for
isolated development and verification; normal users do not need these variables.

## Distribution

Publish `standalone/Vidl.exe` as a GitHub Release asset and use its direct download
URL on your website. The EXE exceeds GitHub's regular Git file limit, so binaries
are published through Releases while their build source is tracked in this repo.
Video downloads run on the user's computer.
Code signing and an automatic app updater are not included.

## Licenses

Vidl source: MIT. Separate bundled components retain their upstream licenses;
see `THIRD-PARTY-NOTICES.txt` and the `licenses` folder in the portable bundle.
