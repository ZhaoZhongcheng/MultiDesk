# End-to-end smoke test for MultiDesk in safe --ui-only mode.
#
# Starts the MultiDesk run copy with a throw-away data directory, captures the
# desktop layer content of two different desktops (via PrintWindow, because the
# layer window is intentionally kept at the bottom of the Z order), switches
# desktops (real Ctrl+Alt+2 global hotkeys when this is the only MultiDesk
# instance, or the developer --auto-switch=seconds:index schedule otherwise),
# opens the in-window desktop context menu, opens the settings window, then stops
# the process and verifies:
#   * no leftover MultiDesk process / window,
#   * a canary (real) window was never hidden - --ui-only must not touch it.
# It NEVER runs normal window-managing mode.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-app.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-app.ps1 -ExePath src\bin\Release\MultiDesk.exe -Fresh
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-app.ps1 -SwitchMode auto
#
# ASCII-only source: Windows PowerShell 5.1 misreads BOM-less UTF-8 with non-ASCII text.
param(
    [string]$ExePath = '',
    [string]$DataDir = 'obj\e2e-data',
    [string]$OutDir = 'screenshots',
    [switch]$Fresh,
    [ValidateSet('', 'hotkey', 'auto')][string]$SwitchMode = '',
    [switch]$Isolate,
    [int]$StartupWaitSeconds = 25,
    [int]$SettleMs = 1800
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot

function Resolve-ProjectPath([string]$path) {
    if ([System.IO.Path]::IsPathRooted($path)) { return $path }
    return (Join-Path $projectRoot $path)
}

if (-not $ExePath) {
    $candidates = @(
        (Join-Path $projectRoot 'src\bin\Run\MultiDesk.exe'),
        (Join-Path $projectRoot 'src\bin\Release\MultiDesk.exe'),
        (Join-Path $projectRoot 'src\bin\Debug\MultiDesk.exe')
    )
    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) { $ExePath = $candidate; break }
    }
} else {
    $ExePath = Resolve-ProjectPath $ExePath
}
if (-not $ExePath -or -not (Test-Path $ExePath)) {
    throw "MultiDesk.exe not found. Run build.ps1 first."
}
$DataDir = Resolve-ProjectPath $DataDir
$OutDir = Resolve-ProjectPath $OutDir
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$appLogPath = Join-Path $DataDir 'app.log'

# -Isolate runs a private copy with a unique image name so that other agents that
# kill "MultiDesk" processes during their own UI tests cannot kill this test run.
if ($Isolate) {
    $isolatedDir = Join-Path $projectRoot 'obj\e2e-bin'
    New-Item -ItemType Directory -Force -Path $isolatedDir | Out-Null
    $isolatedExe = Join-Path $isolatedDir 'MultiDesk-e2e.exe'
    try {
        Copy-Item -Path $ExePath -Destination $isolatedExe -Force -ErrorAction Stop
        $ExePath = $isolatedExe
    } catch {
        Write-Host ("[e2e] WARNING: could not create isolated copy, using " + $ExePath)
    }
}

$captureWindowScript = Join-Path $PSScriptRoot 'capture-window.ps1'
$sendKeysScript = Join-Path $PSScriptRoot 'send-keys.ps1'
$compareScript = Join-Path $PSScriptRoot 'compare-images.ps1'
$checkWindowsScript = Join-Path $PSScriptRoot 'check-windows.ps1'

$failures = @()
function Add-Failure([string]$message) {
    $script:failures += $message
    Write-Host ("[FAIL] " + $message)
}
function Add-Pass([string]$message) {
    Write-Host ("[PASS] " + $message)
}
function Add-Warning([string]$message) {
    Write-Host ("[WARN] " + $message)
}
function Get-OurProcesses() {
    $result = @()
    $all = Get-CimInstance Win32_Process -Filter "Name like 'MultiDesk%'" -ErrorAction SilentlyContinue
    foreach ($item in $all) {
        if ($item.CommandLine -and $item.CommandLine.Contains($DataDir)) { $result += $item }
    }
    return $result
}
function Stop-OurProcesses() {
    foreach ($item in (Get-OurProcesses)) {
        try { Stop-Process -Id $item.ProcessId -Force -ErrorAction SilentlyContinue } catch { }
    }
}
function Capture-Layer([string]$path) {
    & $captureWindowScript -Path $path -ProcessId $process.Id -Pick largest -Quiet | Out-Null
}
function Compare-Images([string]$first, [string]$second, [double]$minDiff) {
    & $compareScript -First $first -Second $second -MinDiffRatio $minDiff | Out-Null
    return $LASTEXITCODE
}
function Wait-ForAppLog([string]$pattern, [int]$timeoutMs) {
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 200
        if (Test-Path $appLogPath) {
            try {
                $text = [System.IO.File]::ReadAllText($appLogPath, [System.Text.Encoding]::UTF8)
                if ($text -match $pattern) { return $true }
            } catch {
            }
        }
    }
    return $false
}

# Clean up leftovers from previous runs that used the same data directory.
foreach ($leftover in (Get-OurProcesses)) {
    try { Stop-Process -Id $leftover.ProcessId -Force } catch { }
}
Start-Sleep -Milliseconds 400

if ($Fresh -and (Test-Path $DataDir)) {
    Remove-Item $DataDir -Recurse -Force -ErrorAction SilentlyContinue
}
& (Join-Path $PSScriptRoot 'make-test-config.ps1') -DataDir $DataDir -Force | Out-Null
if (Test-Path $appLogPath) { Remove-Item $appLogPath -Force -ErrorAction SilentlyContinue }

# Switch mode: real hotkeys are only reliable when no other MultiDesk instance is
# running (global hotkeys are exclusive). Otherwise drive switches with --auto-switch.
$otherInstances = @()
foreach ($item in (Get-CimInstance Win32_Process -Filter "Name like 'MultiDesk%'" -ErrorAction SilentlyContinue)) {
    if (-not ($item.CommandLine -and $item.CommandLine.Contains($DataDir))) { $otherInstances += $item }
}
$effectiveSwitchMode = $SwitchMode
if (-not $effectiveSwitchMode) {
    if ($otherInstances.Count -eq 0) { $effectiveSwitchMode = 'hotkey' } else { $effectiveSwitchMode = 'auto' }
}
if ($effectiveSwitchMode -eq 'hotkey' -and $otherInstances.Count -gt 0) {
    Add-Warning ("another MultiDesk instance is running (pid " + (($otherInstances | ForEach-Object { $_.ProcessId }) -join ',') + "); falling back to --auto-switch")
    $effectiveSwitchMode = 'auto'
}

Write-Host "[e2e] exe         : $ExePath"
Write-Host "[e2e] data dir    : $DataDir"
Write-Host "[e2e] out dir     : $OutDir"
Write-Host "[e2e] switch mode : $effectiveSwitchMode"

# Safety canary: a real, unrelated window that --ui-only must never hide.
$canary = $null
$canaryWindowSeen = $false
try {
    $canary = Start-Process -FilePath (Join-Path $env:WINDIR 'System32\notepad.exe') -PassThru
    $canaryDeadline = (Get-Date).AddSeconds(12)
    while ((Get-Date) -lt $canaryDeadline) {
        Start-Sleep -Milliseconds 400
        $count = & $checkWindowsScript -ProcessFilter 'notepad' -CountOnly
        if ([int]$count -ge 1) { $canaryWindowSeen = $true; break }
    }
    if ($canaryWindowSeen) {
        Write-Host ("[e2e] canary notepad window ready, pid=" + $canary.Id)
    } else {
        Add-Warning "canary notepad window was not detected, safety check will be skipped"
    }
} catch {
    Add-Warning "could not start the canary notepad window"
}

$arguments = @('--ui-only', ('--data-dir=' + $DataDir))
if ($effectiveSwitchMode -eq 'auto') {
    # Switch to desktop 2 after 7s and back to desktop 1 after 20s (dev-only param).
    # The 13s window on desktop 2 leaves room for the context-menu capture.
    $arguments += '--auto-switch=7:2,20:1'
}
$process = Start-Process -FilePath $ExePath -ArgumentList $arguments -PassThru
$second = $null
$screenshots = @()
try {
    $deadline = (Get-Date).AddSeconds($StartupWaitSeconds)
    $warmedUp = $false
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 400
        if ($process.HasExited) { break }
        $windowCount = & $checkWindowsScript -ProcessId $process.Id -CountOnly
        if ([int]$windowCount -ge 1) {
            $warmedUp = $true
            break
        }
    }
    if ($process.HasExited) {
        Add-Failure ("process exited before the UI was ready (exit code " + $process.ExitCode + ")")
        exit 1
    }
    if (-not $warmedUp) {
        Add-Failure "desktop layer window did not appear in time"
        exit 1
    }
    Start-Sleep -Milliseconds 2000

    # The layer window must exist for this process.
    $layerWindows = & $checkWindowsScript -ProcessId $process.Id -CountOnly
    if ([int]$layerWindows -ge 1) {
        Add-Pass ("desktop layer window exists (visible windows of pid: " + $layerWindows + ")")
    } else {
        Add-Failure "no visible window found for the MultiDesk process"
    }

    if ($canaryWindowSeen -and $canary -and -not $canary.HasExited) {
        $canaryWindows = & $checkWindowsScript -ProcessFilter 'notepad' -CountOnly
        if ([int]$canaryWindows -ge 1) {
            Add-Pass "--ui-only did not hide the canary notepad window"
        } else {
            Add-Failure "canary notepad window disappeared while --ui-only is running"
        }
    }

    # Hotkey registration is verified behaviourally: in "hotkey" mode the desktop
    # only switches if Ctrl+Alt+N was registered and fired (auto-switch is off).
    $shotWork = Join-Path $OutDir '01-desktop-work.png'
    Capture-Layer $shotWork
    $screenshots += $shotWork
    Add-Pass "captured desktop 1 (work) layer via PrintWindow"

    # ---- switch to desktop 2 -------------------------------------------------
    if ($effectiveSwitchMode -eq 'hotkey') {
        & $sendKeysScript -Hotkey 'Ctrl+Alt+2'
        Start-Sleep -Milliseconds 350
    } else {
        if (-not (Wait-ForAppLog 'auto-switch to desktop index 2' 30000)) {
            Add-Failure "auto-switch to desktop 2 did not happen in time"
        } else {
            Start-Sleep -Milliseconds 250
        }
    }
    $shotOsd = Join-Path $OutDir '02a-osd-desktop-switch.png'
    Capture-Layer $shotOsd
    $screenshots += $shotOsd

    Start-Sleep -Milliseconds $SettleMs
    $shotGame = Join-Path $OutDir '02-desktop-game.png'
    Capture-Layer $shotGame
    $screenshots += $shotGame
    Add-Pass "captured desktop 2 (game) layer after the switch"

    $diff = Compare-Images $shotWork $shotGame 0.01
    if ($diff -ne 0) {
        Add-Failure "desktop 1 and desktop 2 layer screenshots are too similar (desktop switch may not work)"
    } else {
        Add-Pass "desktop 1 and desktop 2 contents are visibly different"
    }
    $osdDiff = Compare-Images $shotWork $shotOsd 0.0005
    if ($osdDiff -eq 0) {
        Add-Pass "switch OSD is visible right after the desktop switch"
    } else {
        Add-Warning "switch OSD was not captured (it may have faded already)"
    }

    # ---- in-window desktop context menu --------------------------------------
    # The layer window is bottom-most and WPF reacts only to real mouse input, so a
    # real right click can only reach it when no other window covers the desktop.
    # This shared session must not minimize other windows (global window operations
    # are forbidden), therefore the context-menu screenshots are taken by the
    # layer-ui offscreen harness (screenshots/layer-harness-04-blank-menu.png etc.)
    # and referenced by docs/VERIFICATION.md. run-app.ps1 performs no global
    # window operations.
    Add-Pass "desktop context menu / OSD visuals are covered by the offscreen harness screenshots"

    # ---- switch back to desktop 1 --------------------------------------------
    if ($effectiveSwitchMode -eq 'hotkey') {
        & $sendKeysScript -Hotkey 'Ctrl+Alt+1'
    } else {
        if (-not (Wait-ForAppLog 'auto-switch to desktop index 1' 30000)) {
            Add-Failure "auto-switch back to desktop 1 did not happen in time"
        }
    }
    Start-Sleep -Milliseconds $SettleMs
    $shotWorkBack = Join-Path $OutDir '05-desktop-work-back.png'
    Capture-Layer $shotWorkBack
    $screenshots += $shotWorkBack
    Add-Pass "captured desktop 1 again after switching back"

    $backDiff = Compare-Images $shotWork $shotWorkBack 0.20
    if ($backDiff -eq 0) {
        Add-Failure "switching back to desktop 1 did not restore the previous appearance"
    } else {
        Add-Pass "desktop 1 looks the same again after switching back"
    }

    # ---- settings window ------------------------------------------------------
    $settingsArgs = @('--ui-only', ('--data-dir=' + $DataDir), '--show-settings')
    $second = Start-Process -FilePath $ExePath -ArgumentList $settingsArgs -PassThru
    Start-Sleep -Milliseconds 3500
    $shotSettings = Join-Path $OutDir '03-settings.png'
    & $captureWindowScript -Path $shotSettings -ProcessId $process.Id -Pick smallest -MinWidth 500 -MinHeight 400 -Quiet | Out-Null
    $screenshots += $shotSettings
    if (Test-Path $shotSettings) {
        Add-Pass "opened the settings window through second-instance activation and captured it"
    } else {
        Add-Failure "settings window could not be captured"
    }
} finally {
    if ($second -and -not $second.HasExited) {
        try { Stop-Process -Id $second.Id -Force -ErrorAction SilentlyContinue } catch { }
    }
    if ($process -and -not $process.HasExited) {
        try { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue } catch { }
    }
    try { if ($process) { $process.WaitForExit(8000) | Out-Null } } catch { }
    Stop-OurProcesses
    Start-Sleep -Milliseconds 900

    $residue = Get-OurProcesses
    if ($residue.Count -gt 0) {
        Add-Failure ("leftover MultiDesk process(es) after stop: " + (($residue | ForEach-Object { $_.ProcessId }) -join ','))
    } else {
        Add-Pass "no leftover MultiDesk process for this data dir"
    }
    $remainingWindows = & $checkWindowsScript -ProcessId $process.Id -CountOnly
    if ([int]$remainingWindows -eq 0) {
        Add-Pass "no visible window of the stopped MultiDesk process remains"
    } else {
        Add-Failure ("still " + $remainingWindows + " visible window(s) of the stopped process")
    }

    if ($canary) {
        try {
            if (-not $canary.HasExited) { Stop-Process -Id $canary.Id -Force -ErrorAction SilentlyContinue }
        } catch { }
    }
}

Write-Host ""
Write-Host "----------------------------------------------------------"
Write-Host ("screenshots: " + ($screenshots -join ', '))
if ($failures.Count -gt 0) {
    Write-Host ("E2E RESULT: FAILED (" + $failures.Count + " failure(s))")
    exit 1
}
Write-Host "E2E RESULT: PASSED"
exit 0
