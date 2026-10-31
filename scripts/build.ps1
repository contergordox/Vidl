param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../dist/Vidl'),
    [string]$CacheDirectory = (Join-Path $PSScriptRoot '../.cache')
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$out = [IO.Path]::GetFullPath($OutputDirectory)
$cache = [IO.Path]::GetFullPath($CacheDirectory)
[IO.Directory]::CreateDirectory($out) | Out-Null
[IO.Directory]::CreateDirectory($cache) | Out-Null

function Get-VerifiedFile([string]$Url, [string]$Path, [string]$Sha256) {
    if (-not (Test-Path -LiteralPath $Path)) { Invoke-WebRequest -Uri $Url -OutFile $Path }
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Sha256) { throw "SHA-256 mismatch for $Path. The dependency may have changed; review its release before updating the pinned hash." }
}

Get-VerifiedFile 'https://github.com/yt-dlp/yt-dlp/releases/download/2026.10.26/yt-dlp.exe' (Join-Path $cache 'yt-dlp.exe') 'f5389d3698ecde4ef06f4e6b1ce0294fe9ac1396228c8990b8d3416d6d177a7d'
Get-VerifiedFile 'https://nodejs.org/dist/v24.20.0/win-x64/node.exe' (Join-Path $cache 'node.exe') '5c976096e04e5c2c1f091938926234cc9fbebfe9787ddd149351b3b0ecc707b5'
Get-VerifiedFile 'https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl-shared.zip' (Join-Path $cache 'ffmpeg-win64.zip') '74997890a4c199545e15932f62ef29e58cbb0efef9e73c56b9ef5a3ae83b3cc9'

& (Join-Path $PSScriptRoot 'make-icon.ps1')
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
dotnet run --project (Join-Path $root 'tests/Vidl.Tests') -c Release --no-launch-profile
if ($LASTEXITCODE -ne 0) { throw 'Core checks failed.' }
dotnet publish (Join-Path $root 'src/Vidl/Vidl.csproj') -c Release -r win-x64 --self-contained true '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:EnableCompressionInSingleFile=true' '-p:DebugType=None' -o $out --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

$tools = Join-Path $out 'tools'
$licenses = Join-Path $out 'licenses'
[IO.Directory]::CreateDirectory($tools) | Out-Null
[IO.Directory]::CreateDirectory($licenses) | Out-Null
Copy-Item -LiteralPath (Join-Path $cache 'yt-dlp.exe'),(Join-Path $cache 'node.exe') -Destination $tools -Force
$ffroot = Join-Path $cache 'ffmpeg'
Expand-Archive -LiteralPath (Join-Path $cache 'ffmpeg-win64.zip') -DestinationPath $ffroot -Force
$ffbuild = Join-Path $ffroot 'ffmpeg-master-latest-win64-gpl-shared'
Get-ChildItem -Path (Join-Path $ffbuild 'bin/*') -File | Where-Object { $_.Extension -eq '.dll' -or $_.Name -in @('ffmpeg.exe','ffprobe.exe') } | Copy-Item -Destination $tools -Force
Copy-Item -LiteralPath (Join-Path $ffbuild 'LICENSE.txt') -Destination (Join-Path $licenses 'FFmpeg-LICENSE.txt') -Force

$notices = @{
    'yt-dlp-LICENSE.txt' = 'https://raw.githubusercontent.com/yt-dlp/yt-dlp/2026.10.26/LICENSE'
    'yt-dlp-THIRD-PARTY.txt' = 'https://raw.githubusercontent.com/yt-dlp/yt-dlp/2026.10.26/THIRD_PARTY_LICENSES.txt'
    'Node-LICENSE.txt' = 'https://raw.githubusercontent.com/nodejs/node/v24.20.0/LICENSE'
    'dotnet-LICENSE.txt' = 'https://raw.githubusercontent.com/dotnet/runtime/v10.0.13/LICENSE.TXT'
    'dotnet-THIRD-PARTY.txt' = 'https://raw.githubusercontent.com/dotnet/runtime/v10.0.13/THIRD-PARTY-NOTICES.TXT'
}
foreach ($entry in $notices.GetEnumerator()) { Invoke-WebRequest -Uri $entry.Value -OutFile (Join-Path $licenses $entry.Key) }
Copy-Item -LiteralPath (Join-Path $root 'LICENSE'),(Join-Path $root 'THIRD-PARTY-NOTICES.txt'),(Join-Path $root 'README.md') -Destination $out -Force
$zip = Join-Path ([IO.Path]::GetDirectoryName($out)) 'Vidl-0.1.0-windows-x64.zip'
Compress-Archive -LiteralPath $out -DestinationPath $zip -CompressionLevel Optimal -Force
Write-Output "Portable app: $out"
Write-Output "Download archive: $zip"
