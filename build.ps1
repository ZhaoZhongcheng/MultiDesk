# MultiDesk build script.
# Builds with the in-box .NET Framework 4.8 MSBuild (no extra SDK required).
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1            # Debug build
#   powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 -Release   # Release build
#   powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 -Tests     # build + run unit tests
#
# 说明：
#   * 构建成功后会把 exe 复制到 src\bin\Run\MultiDesk.exe（运行副本），
#     开发/测试请运行该副本，避免占用编译输出导致下次构建失败。
#   * 如果编译输出仍被运行中的开发实例占用，脚本会等待最多 12 秒，
#     然后只结束「正在运行该输出文件」的 MultiDesk 进程。
param(
    [switch]$Release,
    [switch]$Tests,
    [switch]$NoBomFix
)

$ErrorActionPreference = 'Stop'
$env:VSLANG = '1033'

$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
if (-not $root) { $root = (Get-Location).Path }
$msbuild = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\MSBuild.exe'
if (-not (Test-Path $msbuild)) { throw "MSBuild not found: $msbuild" }

function Test-FileLocked([string]$path) {
    if (-not (Test-Path $path)) { return $false }
    try {
        $stream = [System.IO.File]::Open($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
        $stream.Close()
        return $false
    } catch {
        return $true
    }
}

function Clear-LockedOutput([string]$targetExe) {
    if (-not (Test-FileLocked $targetExe)) { return }
    Write-Host "[build] output exe is locked, waiting for running dev instances..."
    $deadline = (Get-Date).AddSeconds(12)
    while ((Get-Date) -lt $deadline -and (Test-FileLocked $targetExe)) { Start-Sleep -Milliseconds 400 }
    if (-not (Test-FileLocked $targetExe)) { return }
    Write-Host "[build] stopping dev MultiDesk instances that lock the output..."
    Get-Process -Name MultiDesk -ErrorAction SilentlyContinue | ForEach-Object {
        try {
            if ($_.Path -eq $targetExe) { $_.Kill() }
        } catch { }
    }
    Start-Sleep -Milliseconds 800
}

function Publish-RunCopy([string]$sourceExe, [string]$runDir) {
    New-Item -ItemType Directory -Force -Path $runDir | Out-Null
    $runExe = Join-Path $runDir 'MultiDesk.exe'
    try {
        Copy-Item -Path $sourceExe -Destination $runExe -Force -ErrorAction Stop
        Write-Host "[build] run copy: $runExe"
        return $runExe
    } catch {
        $altExe = Join-Path $runDir ("MultiDesk-" + (Get-Date -Format 'HHmmss') + ".exe")
        Copy-Item -Path $sourceExe -Destination $altExe -Force
        Write-Host "[build] run copy (locked, use this): $altExe"
        return $altExe
    }
}

# Serialize concurrent builds from multiple agents/terminals.
$mutex = New-Object System.Threading.Mutex($false, 'Local\MultiDesk.Build')
$locked = $false
try {
    try { $locked = $mutex.WaitOne(900000) } catch [System.Threading.AbandonedMutexException] { $locked = $true }
    if (-not $locked) { throw 'Timed out waiting for build lock' }

    # Ensure .cs files are UTF-8 with BOM so the old C# 5 compiler reads Chinese text correctly.
    if (-not $NoBomFix) {
        $utf8Bom = New-Object System.Text.UTF8Encoding($true)
        $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
        Get-ChildItem -Path (Join-Path $root 'src'), (Join-Path $root 'tests') -Recurse -Filter *.cs -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '\\obj\\' -and $_.FullName -notmatch '\\bin\\' } |
            ForEach-Object {
                $bytes = [System.IO.File]::ReadAllBytes($_.FullName)
                $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
                if (-not $hasBom) {
                    $text = [System.IO.File]::ReadAllText($_.FullName, $utf8NoBom)
                    [System.IO.File]::WriteAllText($_.FullName, $text, $utf8Bom)
                }
            }
    }

    $config = if ($Release) { 'Release' } else { 'Debug' }

    function Invoke-MsBuild([string]$projectFile, [string]$configuration) {
        $logDir = Join-Path $root 'obj'
        New-Item -ItemType Directory -Force -Path $logDir | Out-Null
        $outFile = Join-Path $logDir ("msbuild-" + $configuration + "-out.log")
        $errFile = Join-Path $logDir ("msbuild-" + $configuration + "-err.log")
        $stdArgs = @($projectFile, '/nologo', '/v:minimal', "/p:Configuration=$configuration", '/t:Rebuild')
        $proc = Start-Process -FilePath $msbuild -ArgumentList $stdArgs -Wait -PassThru -NoNewWindow `
            -RedirectStandardOutput $outFile -RedirectStandardError $errFile
        $lines = @()
        if (Test-Path $outFile) { $lines += [System.IO.File]::ReadAllLines($outFile) }
        if (Test-Path $errFile) { $lines += [System.IO.File]::ReadAllLines($errFile) }
        foreach ($line in $lines) {
            if ($line -match 'warning MSB3644' -or $line -match 'warning MSB3270' -or
                $line -match 'ToolsVersion="15\.0"') { continue }
            Write-Host $line
        }
        return $proc.ExitCode
    }

    $proj = Join-Path $root 'src\MultiDesk.csproj'
    $targetExe = Join-Path $root "src\bin\$config\MultiDesk.exe"
    Clear-LockedOutput $targetExe
    Write-Host "[build] $config -> $proj"
    $msbExit = Invoke-MsBuild $proj $config
    if ($msbExit -ne 0) { throw "Build failed with exit code $msbExit" }

    $exe = Join-Path $root "src\bin\$config\MultiDesk.exe"
    if (-not (Test-Path $exe)) { throw "Build output missing: $exe" }
    Write-Host "[build] OK: $exe"
    Publish-RunCopy $exe (Join-Path $root 'src\bin\Run') | Out-Null

    if ($Tests) {
        $testProj = Join-Path $root 'tests\TestRunner.csproj'
        if (Test-Path $testProj) {
            Write-Host '[build] building tests...'
            $testTarget = Join-Path $root "tests\bin\$config\TestRunner.exe"
            Clear-LockedOutput $testTarget
            $testExit = Invoke-MsBuild $testProj $config
            if ($testExit -ne 0) { throw "Test build failed with exit code $testExit" }
            $testExe = Join-Path $root "tests\bin\$config\TestRunner.exe"
            if (-not (Test-Path $testExe)) { throw "Test output missing: $testExe" }
            $testRunDir = Join-Path $root 'tests\bin\Run'
            New-Item -ItemType Directory -Force -Path $testRunDir | Out-Null
            $testRunExe = Join-Path $testRunDir 'TestRunner.exe'
            try { Copy-Item -Path $testExe -Destination $testRunExe -Force -ErrorAction Stop }
            catch { $testRunExe = $testExe }
            Write-Host "[test] running: $testRunExe"
            $testProc = Start-Process -FilePath $testRunExe -Wait -PassThru -NoNewWindow
            if ($testProc.ExitCode -ne 0) { throw "Tests failed with exit code $($testProc.ExitCode)" }
            Write-Host '[test] OK'
        } else {
            Write-Host '[build] tests project not found, skipped'
        }
    }
}
finally {
    if ($locked) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
