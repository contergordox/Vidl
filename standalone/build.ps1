param(
    [string]$PortableDirectory = (Join-Path $PSScriptRoot '../dist/Vidl'),
    [string]$OutputFile = (Join-Path $PSScriptRoot 'Vidl.exe'),
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$portable = [IO.Path]::GetFullPath($PortableDirectory)
$output = [IO.Path]::GetFullPath($OutputFile)
$cache = Join-Path $root '.cache/standalone'
[IO.Directory]::CreateDirectory($cache) | Out-Null
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output)) | Out-Null

if (-not (Test-Path -LiteralPath (Join-Path $portable 'Vidl.exe'))) {
    & (Join-Path $root 'scripts/build.ps1') -OutputDirectory $portable
}
if (-not $SkipTests) {
    dotnet run --project (Join-Path $PSScriptRoot 'tests/Bundle.Tests.csproj') -c Release --no-launch-profile
    if ($LASTEXITCODE -ne 0) { throw 'Standalone checks failed.' }
}

$paths = @('Vidl.exe','LICENSE','README.md','THIRD-PARTY-NOTICES.txt')
$paths += @(Get-ChildItem -LiteralPath (Join-Path $portable 'tools') -File | Where-Object {
    $_.Extension -eq '.dll' -or $_.Name -in @('yt-dlp.exe','ffmpeg.exe','ffprobe.exe','node.exe')
} | ForEach-Object { 'tools/' + $_.Name })
$licenseNames = @('dotnet-LICENSE.txt','dotnet-THIRD-PARTY.txt','FFmpeg-LICENSE.txt','Node-LICENSE.txt','yt-dlp-LICENSE.txt','yt-dlp-THIRD-PARTY.txt')
$paths += @($licenseNames | ForEach-Object { 'licenses/' + $_ })
foreach ($required in @('tools/yt-dlp.exe','tools/node.exe','tools/ffmpeg.exe','tools/ffprobe.exe')) {
    if ($paths -notcontains $required) { throw "Required tool is missing: $required" }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$payload = Join-Path $cache 'payload.zip'
$stream = [IO.File]::Open($payload, [IO.FileMode]::Create)
$zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
$files = @()
try {
    foreach ($relative in ($paths | Sort-Object -Unique)) {
        $source = Join-Path $portable $relative
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Required bundle file is missing: $relative" }
        $entry = $zip.CreateEntry('Vidl/'+$relative, [IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = [DateTimeOffset]::new(2026,1,1,0,0,0,[TimeSpan]::Zero)
        $entry.ExternalAttributes = 0
        $input = [IO.File]::OpenRead($source)
        $destination = $entry.Open()
        try { $input.CopyTo($destination) } finally { $destination.Dispose(); $input.Dispose() }
        $files += @{ path=$relative; length=(Get-Item -LiteralPath $source).Length; sha256=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
} finally { $zip.Dispose(); $stream.Dispose() }
$id = (Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest = Join-Path $cache 'manifest.json'
[IO.File]::WriteAllText($manifest, (@{ id=$id; files=$files } | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))

$framework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'The Windows .NET Framework compiler is unavailable.' }
$arguments = @(
    '/nologo','/target:winexe','/platform:x64','/optimize+','/debug-','/codepage:65001',
    "/out:$output",('/win32icon:'+(Join-Path $root 'assets/Vidl.ico')),
    "/resource:$payload,Vidl.Payload.zip", "/resource:$manifest,Vidl.Manifest.json",
    '/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll',
    ('/reference:'+(Join-Path $framework 'System.IO.Compression.dll')),
    ('/reference:'+(Join-Path $framework 'System.Runtime.Serialization.dll')),
    (Join-Path $PSScriptRoot 'BundleExtractor.cs'), (Join-Path $PSScriptRoot 'Launcher.cs')
)
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Standalone compiler failed.' }
$hash = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(($output+'.sha256'), ($hash+'  '+[IO.Path]::GetFileName($output)+"`n"), [Text.UTF8Encoding]::new($false))
Write-Output ('Standalone executable: '+$output)
Write-Output ('Size: {0:N1} MB' -f ((Get-Item -LiteralPath $output).Length / 1000000))
