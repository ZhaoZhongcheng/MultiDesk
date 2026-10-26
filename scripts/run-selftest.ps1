# Run the MultiDesk engine self test (--selftest) in a throw-away data directory.
# The self test only manages windows of the test processes it launches itself,
# therefore it is safe: it never touches real user windows.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-selftest.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-selftest.ps1 -ExePath src\bin\Release\MultiDesk.exe
#
# Exit code: 0 = selftest passed, 1 = selftest failed or a residue check failed.
# ASCII-only source: Windows PowerShell 5.1 misreads BOM-less UTF-8 with non-ASCII text.
param(
    [string]$ExePath = '',
    [string]$DataDir = 'obj\selftest-data',
    [int]$TimeoutSeconds = 180
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
if (Test-Path $DataDir) {
    Remove-Item $DataDir -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Force -Path $DataDir | Out-Null

$journalPath = Join-Path $DataDir 'session.json'
$logPath = Join-Path $DataDir 'selftest.log'

Write-Host "[selftest] exe      : $ExePath"
Write-Host "[selftest] data dir : $DataDir"

# Safety canary: a normal user window that --selftest must never touch (its window
# filter only allows the self-test processes it launches itself).
$canaryScript = Join-Path $PSScriptRoot 'check-windows.ps1'
$canary = $null
$canarySeen = $false
try {
    $canary = Start-Process -FilePath (Join-Path $env:WINDIR 'System32\notepad.exe') -PassThru
    $canaryDeadline = (Get-Date).AddSeconds(12)
    while ((Get-Date) -lt $canaryDeadline) {
        Start-Sleep -Milliseconds 400
        $canaryCount = & $canaryScript -ProcessFilter 'notepad' -CountOnly
        if ([int]$canaryCount -ge 1) { $canarySeen = $true; break }
    }
} catch {
    $canarySeen = $false
}
if ($canarySeen) {
    Write-Host ("[selftest] safety canary window ready (notepad pid=" + $canary.Id + ")")
} else {
    Write-Host "[selftest] WARNING: safety canary window not detected"
}

Write-Host "[selftest] running, please wait..."

# Use System.Diagnostics.Process directly: Start-Process -PassThru does not expose
# ExitCode reliably in Windows PowerShell 5.1.
$startInfo = New-Object System.Diagnostics.ProcessStartInfo
$startInfo.FileName = $ExePath
$startInfo.Arguments = '--selftest --data-dir="' + $DataDir + '"'
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$process = New-Object System.Diagnostics.Process
$process.StartInfo = $startInfo
$null = $process.Start()
$finished = $process.WaitForExit($TimeoutSeconds * 1000)
if (-not $finished) {
    try { $process.Kill() } catch { }
    Write-Host ("[selftest] FAILED: timeout after " + $TimeoutSeconds + "s")
    exit 1
}
$exitCode = $process.ExitCode
Write-Host ("[selftest] exit code: " + $exitCode)

$failures = @()
if ($exitCode -ne 0) { $failures += ("non-zero exit code " + $exitCode) }

if (Test-Path $logPath) {
    Write-Host "[selftest] --- selftest.log ---"
    $logText = [System.IO.File]::ReadAllText($logPath, [System.Text.Encoding]::UTF8)
    foreach ($line in ($logText -split "`r?`n")) {
        if ($line.Length -gt 0) { Write-Host ("  " + $line) }
    }
    if ($logText -match 'FAIL\s') {
        $failCount = ([regex]::Matches($logText, 'FAIL\s')).Count
        $failures += ($failCount.ToString() + " FAIL line(s) in selftest.log")
    }
    $summaryMatch = [regex]::Match($logText, 'Total=(\d+) Passed=(\d+) Failed=(\d+)')
    if ($summaryMatch.Success) {
        Write-Host ("[selftest] summary: " + $summaryMatch.Value)
        if ([int]$summaryMatch.Groups[3].Value -ne 0) { $failures += "selftest reported failed checks" }
    } else {
        Write-Host "[selftest] note: summary line is only printed to the console (selftest.log has no summary)"
    }
} else {
    $failures += "selftest.log missing"
}

# Leftover test windows: the self test launches MultiDesk.exe --test-window processes.
# Only report (and never kill) processes that are direct children of THIS selftest run,
# so tests started by another agent are never disturbed.
Start-Sleep -Milliseconds 800
$testWindowProcesses = @()
$allProcesses = Get-CimInstance Win32_Process -Filter "Name like 'MultiDesk%'" -ErrorAction SilentlyContinue
foreach ($item in $allProcesses) {
    if ($item.CommandLine -and $item.CommandLine.Contains('--test-window') -and $item.ParentProcessId -eq $process.Id) {
        $testWindowProcesses += $item
    }
}
if ($testWindowProcesses.Count -gt 0) {
    $failures += ("leftover --test-window child process(es): " + (($testWindowProcesses | ForEach-Object { $_.ProcessId }) -join ',') + " (not killed automatically)")
} else {
    Write-Host "[selftest] no leftover --test-window processes from this run"
}

if (Test-Path $journalPath) {
    $journalText = [System.IO.File]::ReadAllText($journalPath, [System.Text.Encoding]::UTF8)
    if ($journalText -match '"hwnd"') {
        $failures += "session.json still contains hidden window records after selftest"
    } else {
        Write-Host "[selftest] session journal is clean"
    }
} else {
    Write-Host "[selftest] session journal not present (clean)"
}

# The canary window must still be visible: --selftest must not touch user windows.
if ($canarySeen -and $canary -and -not $canary.HasExited) {
    $canaryAfter = & $canaryScript -ProcessFilter 'notepad' -CountOnly
    if ([int]$canaryAfter -ge 1) {
        Write-Host "[selftest] safety canary window is still visible (user windows untouched)"
    } else {
        $failures += "safety canary window disappeared during --selftest"
    }
} elseif ($canarySeen) {
    $failures += "safety canary process exited during --selftest"
}

if ($canary) {
    try {
        if (-not $canary.HasExited) { Stop-Process -Id $canary.Id -Force -ErrorAction SilentlyContinue }
    } catch {
    }
}

Write-Host ""
Write-Host "----------------------------------------------------------"
if ($failures.Count -gt 0) {
    foreach ($failure in $failures) { Write-Host ("[FAIL] " + $failure) }
    Write-Host "SELFTEST RESULT: FAILED"
    exit 1
}
Write-Host "SELFTEST RESULT: PASSED"
exit 0
