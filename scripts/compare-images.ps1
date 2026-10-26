# Compare two PNG screenshots by sampling pixels (no third-party dependencies).
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\compare-images.ps1 `
#       -First screenshots\01-desktop-work.png -Second screenshots\02-desktop-game.png
#
# Exit code 0 = images differ by at least -MinDiffRatio (i.e. different desktop content).
# Exit code 1 = images are (nearly) identical.
# ASCII-only source: Windows PowerShell 5.1 misreads BOM-less UTF-8 with non-ASCII text.
param(
    [Parameter(Mandatory = $true)][string]$First,
    [Parameter(Mandatory = $true)][string]$Second,
    [int]$Step = 8,
    [double]$MinDiffRatio = 0.01
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$projectRoot = Split-Path -Parent $PSScriptRoot
function Resolve-ImagePath([string]$path) {
    if ([System.IO.Path]::IsPathRooted($path)) { return $path }
    return (Join-Path $projectRoot $path)
}

$firstPath = Resolve-ImagePath $First
$secondPath = Resolve-ImagePath $Second
if (-not (Test-Path $firstPath)) { throw "Image not found: $firstPath" }
if (-not (Test-Path $secondPath)) { throw "Image not found: $secondPath" }

$bitmapA = [System.Drawing.Bitmap]::FromFile($firstPath)
$bitmapB = [System.Drawing.Bitmap]::FromFile($secondPath)
try {
    if ($bitmapA.Width -ne $bitmapB.Width -or $bitmapA.Height -ne $bitmapB.Height) {
        Write-Host ("[compare] " + (Split-Path -Leaf $firstPath) + " vs " + (Split-Path -Leaf $secondPath) +
            ": size differs (" + $bitmapA.Width + "x" + $bitmapA.Height + " vs " + $bitmapB.Width + "x" + $bitmapB.Height + ")")
        Write-Host "[compare] RESULT: DIFFERENT"
        exit 0
    }
    $total = 0
    $different = 0
    for ($y = 0; $y -lt $bitmapA.Height; $y += $Step) {
        for ($x = 0; $x -lt $bitmapA.Width; $x += $Step) {
            $colorA = $bitmapA.GetPixel($x, $y).ToArgb()
            $colorB = $bitmapB.GetPixel($x, $y).ToArgb()
            $total++
            if ($colorA -ne $colorB) { $different++ }
        }
    }
    $ratio = 0.0
    if ($total -gt 0) { $ratio = [double]$different / [double]$total }
    $percent = [math]::Round([double]$ratio * 100.0, 2)
    Write-Host ("[compare] " + (Split-Path -Leaf $firstPath) + " vs " + (Split-Path -Leaf $secondPath) +
        ": " + $different + "/" + $total + " samples differ (" + $percent + "%)")
    if ($ratio -ge $MinDiffRatio) {
        Write-Host "[compare] RESULT: DIFFERENT"
        exit 0
    }
    $minPercent = [math]::Round([double]$MinDiffRatio * 100.0, 2)
    Write-Host ("[compare] RESULT: TOO SIMILAR (expected at least " + $minPercent + "%)")
    exit 1
} finally {
    $bitmapA.Dispose()
    $bitmapB.Dispose()
}
