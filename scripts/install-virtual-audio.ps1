<#
.SYNOPSIS
  Installs a virtual audio output device (VB-CABLE) on Windows 10/11 for PhoneBox secondary-screen audio isolation.
#>
$ErrorActionPreference = 'Stop'

function Get-VirtualAudioDevice {
    Get-PnpDevice -Class AudioEndpoint -Status OK -ErrorAction SilentlyContinue |
        Where-Object { $_.FriendlyName -match 'CABLE Input|VB-Audio Virtual Cable|Virtual Audio Driver|Steam Streaming Speakers|VoiceMeeter' } |
        Select-Object -First 1
}

$existing = Get-VirtualAudioDevice
if ($existing) {
    Write-Host "Virtual audio device already installed: $($existing.FriendlyName)"
    exit 0
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host 'Requesting administrator privileges (UAC) to install VB-CABLE...'
    $proc = Start-Process powershell -Verb RunAs -PassThru -Wait -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath
    )
    exit $proc.ExitCode
}

$tempDir = Join-Path ([IO.Path]::GetTempPath()) ('phonebox-vbcable-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
try {
    $zipPath = Join-Path $tempDir 'vbcable.zip'
    $extractDir = Join-Path $tempDir 'pkg'
    New-Item -ItemType Directory -Path $extractDir -Force | Out-Null

    $downloaded = $false
    foreach ($url in @(
        'https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip',
        'https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack43.zip'
    )) {
        try {
            Write-Host "Downloading $url ..."
            Invoke-WebRequest -Uri $url -OutFile $zipPath -TimeoutSec 30 -UseBasicParsing
            $downloaded = $true
            break
        } catch {
            Write-Warning "Failed from $url : $($_.Exception.Message)"
        }
    }
    if (-not $downloaded) { throw 'Could not download VB-CABLE driver package.' }

    Expand-Archive -LiteralPath $zipPath -DestinationPath $extractDir -Force
    $setupName = if ([Environment]::Is64BitOperatingSystem) { 'VBCABLE_Setup_x64.exe' } else { 'VBCABLE_Setup.exe' }
    $setup = Get-ChildItem -Path $extractDir -Filter $setupName -Recurse | Select-Object -First 1
    if (-not $setup) {
        $setup = Get-ChildItem -Path $extractDir -Filter 'VBCABLE_Setup*.exe' -Recurse | Select-Object -First 1
    }
    if (-not $setup) { throw 'VB-CABLE installer executable not found in archive.' }

    Write-Host "Running $($setup.Name) -i -h ..."
    $p = Start-Process -FilePath $setup.FullName -ArgumentList '-i -h' -WorkingDirectory $setup.DirectoryName -PassThru -Wait
    if ($p.ExitCode -ne 0) { throw "VB-CABLE setup exited with code $($p.ExitCode)" }

    $deadline = (Get-Date).AddSeconds(5)
    do {
        $installed = Get-VirtualAudioDevice
        if ($installed) {
            Write-Host "Virtual audio device ready: $($installed.FriendlyName)"
            exit 0
        }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)

    Write-Warning 'VB-CABLE installed (exit 0), reboot Windows if the virtual audio endpoint does not appear immediately.'
    exit 0
} finally {
    Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}
