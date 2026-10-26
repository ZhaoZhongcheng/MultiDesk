# Write a deterministic MultiDesk config.json for --ui-only screenshot/E2E verification.
# Two desktops, clearly different solid-color wallpapers and different icon sets.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\make-test-config.ps1 -DataDir obj\e2e-data -Force
#
# ASCII-only source: Windows PowerShell 5.1 misreads BOM-less UTF-8 with non-ASCII text.
param(
    [Parameter(Mandatory = $true)][string]$DataDir,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not [System.IO.Path]::IsPathRooted($DataDir)) {
    $DataDir = Join-Path $projectRoot $DataDir
}
$configPath = Join-Path $DataDir 'config.json'
if ((Test-Path $configPath) -and -not $Force) {
    Write-Host ("[config] exists: " + $configPath)
    Write-Output $configPath
    exit 0
}
New-Item -ItemType Directory -Force -Path $DataDir | Out-Null

function Get-ExistingPaths([string[]]$candidates) {
    $result = @()
    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) { $result += $candidate }
    }
    return $result
}

function New-IconList([string[]]$targets, [string]$prefix) {
    $icons = @()
    for ($i = 0; $i -lt $targets.Count; $i++) {
        $target = $targets[$i]
        $icons += [ordered]@{
            id      = $prefix + '-icon-' + $i
            name    = [System.IO.Path]::GetFileNameWithoutExtension($target)
            target  = $target
            args    = ''
            workdir = [System.IO.Path]::GetDirectoryName($target)
            x       = 24
            y       = 16 + ($i * 104)
        }
    }
    return $icons
}

$windir = $env:WINDIR
if (-not $windir) { $windir = 'C:\Windows' }
$system32 = Join-Path $windir 'System32'

$workTargets = Get-ExistingPaths @(
    (Join-Path $system32 'notepad.exe'),
    (Join-Path $system32 'mspaint.exe'),
    (Join-Path $windir 'explorer.exe')
)
$gameTargets = Get-ExistingPaths @(
    (Join-Path $system32 'charmap.exe'),
    (Join-Path $system32 'write.exe'),
    (Join-Path $system32 'control.exe'),
    (Join-Path $system32 'taskmgr.exe')
)
if ($workTargets.Count -eq 0) { $workTargets = @((Join-Path $system32 'notepad.exe')) }
if ($gameTargets.Count -eq 0) { $gameTargets = @((Join-Path $system32 'mspaint.exe')) }

$workDesktop = [ordered]@{
    id              = 'desk-work'
    name            = 'Work'
    wallpaperMode   = 2
    wallpaperPath   = ''
    wallpaperFit    = 0
    backgroundColor = '#FF17324F'
    showBadge       = $true
    icons           = (New-IconList $workTargets 'work')
}

$gameDesktop = [ordered]@{
    id              = 'desk-game'
    name            = 'Game'
    wallpaperMode   = 2
    wallpaperPath   = ''
    wallpaperFit    = 0
    backgroundColor = '#FF4F1732'
    showBadge       = $true
    icons           = (New-IconList $gameTargets 'game')
}

$config = [ordered]@{
    version         = 1
    desktops        = @($workDesktop, $gameDesktop)
    activeDesktopId = 'desk-work'
    options         = [ordered]@{
        startWithWindows = $false
        showDesktopName  = $true
        firstRunDone     = $true
    }
}

$json = $config | ConvertTo-Json -Depth 8
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($configPath, $json, $utf8NoBom)
Write-Host ("[config] wrote " + $configPath + " (work icons: " + $workTargets.Count + ", game icons: " + $gameTargets.Count + ")")
Write-Output $configPath
