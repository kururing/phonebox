param(
    [Parameter(Mandatory = $true)]
    [string]$Destination
)

$ErrorActionPreference = 'Stop'
$archiveUrl = 'https://github.com/FFmpeg-Builds/FFmpeg-Builds/releases/download/autobuild-2024-12-31-13-02/ffmpeg-N-118197-gbb85423142-win64-lgpl-shared.zip'
$archiveSha256 = 'dde605c4bfddd75a4796d4688c303f0064e95babe0d0fcdd208a2b061fbb46fa'
$sourceUrl = 'https://api.github.com/repos/FFmpeg/FFmpeg/tarball/bb85423142103d694d97bad1967bd3dc55440e71'
$sourceSha256 = '3acea149066abc3c2aa73e869f4ad4d49e62693864d6ec2832747d319d4ecdaa'
$work = Join-Path ([IO.Path]::GetTempPath()) ('phonebox-ffmpeg-' + [guid]::NewGuid().ToString('N'))
$workRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$workPath = [IO.Path]::GetFullPath($work)
if (-not $workPath.StartsWith($workRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Temporary FFmpeg path is outside the system temporary directory'
}

try {
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    $archive = Join-Path $work 'ffmpeg.zip'
    Write-Host '==> Downloading the LGPL FFmpeg build (checksum verified)'
    Invoke-WebRequest -Uri $archiveUrl -OutFile $archive
    $actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $archiveSha256) { throw "FFmpeg archive checksum mismatch: $actual" }

    $unpacked = Join-Path $work 'unpacked'
    Expand-Archive -LiteralPath $archive -DestinationPath $unpacked
    $build = Get-ChildItem -LiteralPath $unpacked -Directory | Select-Object -First 1
    if (-not $build) { throw 'FFmpeg archive is missing its build folder' }
    $bin = Join-Path $build.FullName 'bin'
    if (-not (Test-Path -LiteralPath (Join-Path $bin 'ffmpeg.exe'))) { throw 'FFmpeg executable was not found in the archive' }

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $bin 'ffmpeg.exe') -Destination $Destination -Force
    Get-ChildItem -LiteralPath $bin -Filter '*.dll' -File | Copy-Item -Destination $Destination -Force
    Copy-Item -LiteralPath (Join-Path $build.FullName 'LICENSE.txt') -Destination $Destination -Force
    $source = Join-Path $work 'ffmpeg-source.tar.gz'
    Invoke-WebRequest -Uri $sourceUrl -OutFile $source
    $actualSource = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualSource -ne $sourceSha256) { throw "FFmpeg source archive checksum mismatch: $actualSource" }
    Copy-Item -LiteralPath $source -Destination (Join-Path $Destination 'source.tar.gz') -Force

    $buildInfo = @(
        'FFmpeg N-118197-gbb85423142, BtbN Windows x64 LGPL shared build; uses NVENC API 12.2 for compatibility with NVIDIA driver 566.'
        'Exact FFmpeg source archive: source.tar.gz (bb85423142103d694d97bad1967bd3dc55440e71).'
        'Builds: https://github.com/BtbN/FFmpeg-Builds'
        'The bundled command-line tool links dynamically to the LGPL libraries in this folder.'
    )
    $buildInfo += & (Join-Path $Destination 'ffmpeg.exe') -hide_banner -buildconf 2>&1
    Set-Content -LiteralPath (Join-Path $Destination 'BUILD.txt') -Encoding utf8 -Value $buildInfo
} finally {
    if (Test-Path -LiteralPath $workPath) {
        Remove-Item -LiteralPath $workPath -Recurse -Force
    }
}
