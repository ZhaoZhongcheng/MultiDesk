# Capture the whole virtual screen into a PNG file.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\capture-screenshot.ps1 -Path screenshots\01-desktop-work.png
#
# ASCII-only source on purpose: Windows PowerShell 5.1 misreads BOM-less UTF-8
# scripts that contain non-ASCII characters.
param(
    [Parameter(Mandatory = $true)][string]$Path,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

# DPI aware so the bitmap matches real device pixels on scaled displays.
try {
    Add-Type -Namespace MultiDeskScripts -Name DpiHelper -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool SetProcessDPIAware();
'@
    [MultiDeskScripts.DpiHelper]::SetProcessDPIAware() | Out-Null
} catch {
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$target = $Path
if (-not [System.IO.Path]::IsPathRooted($target)) {
    $projectRoot = Split-Path -Parent $PSScriptRoot
    $target = Join-Path $projectRoot $target
}
$dir = Split-Path -Parent $target
if ($dir -and -not (Test-Path $dir)) {
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
}

$bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen
$bitmap = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
try {
    $graphics.CopyFromScreen($bounds.Left, $bounds.Top, 0, 0, $bitmap.Size)
} finally {
    $graphics.Dispose()
}
try {
    $bitmap.Save($target, [System.Drawing.Imaging.ImageFormat]::Png)
} finally {
    $bitmap.Dispose()
}

if (-not $Quiet) {
    Write-Host ("[screenshot] " + $target + " (" + $bounds.Width + "x" + $bounds.Height + ")")
}
Write-Output $target
