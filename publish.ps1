# MultiDesk publish script.
#
# Release build -> collect the standalone executable into dist\MultiDesk
# -> verify the packaged exe actually starts -> create dist\MultiDesk-<version>.zip
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File publish.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File publish.ps1 -SkipBuild -NoZip
#
# Notes:
#   * The whole application is a single WPF exe; .NET Framework 4.8 is part of
#     Windows 10/11 so NO framework DLL may be copied into dist.
#   * This script is saved as UTF-8 with BOM because Windows PowerShell 5.1
#     misreads BOM-less UTF-8 sources that contain non-ASCII characters.
param(
    [string]$Version = '',
    [switch]$SkipBuild,
    [switch]$NoZip
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
if (-not $root) { $root = (Get-Location).Path }
Write-Host "[publish] project root: $root"

# 版本号默认从 src\AssemblyInfo.cs 的 AssemblyVersion 读取（避免忘记同步发布版本号）；
# 也可以用 -Version 1.2.8 显式覆盖。
if (-not $Version) {
    $assemblyInfo = Join-Path $root 'src\AssemblyInfo.cs'
    if (Test-Path $assemblyInfo) {
        $match = [regex]::Match([System.IO.File]::ReadAllText($assemblyInfo), 'AssemblyVersion\("(\d+\.\d+\.\d+)')
        if ($match.Success) { $Version = $match.Groups[1].Value }
    }
    if (-not $Version) { $Version = '0.0.0' }
}
Write-Host "[publish] version: $Version"

if (-not $SkipBuild) {
    Write-Host '[publish] running Release build (build.ps1 -Release)...'
    & (Join-Path $root 'build.ps1') -Release
    Write-Host '[publish] Release build finished'
}

$releaseExe = Join-Path $root 'src\bin\Release\MultiDesk.exe'
if (-not (Test-Path $releaseExe)) {
    throw "Release exe not found: $releaseExe (run build.ps1 -Release first)"
}

$distDir = Join-Path $root 'dist\MultiDesk'
if (Test-Path $distDir) {
    # 保留用户数据目录 MultiDesk-data（绿色便携模式的配置/图标），升级重新打包时不能删
    Get-ChildItem -Path $distDir -Force | Where-Object { $_.Name -ne 'MultiDesk-data' } | Remove-Item -Recurse -Force
} else {
    New-Item -ItemType Directory -Force -Path $distDir | Out-Null
}
New-Item -ItemType Directory -Force -Path $distDir | Out-Null

$distExe = Join-Path $distDir 'MultiDesk.exe'
Copy-Item -Path $releaseExe -Destination $distExe -Force
Write-Host "[publish] copied: $distExe"

$iconSource = Join-Path $root 'assets\app.ico'
if (Test-Path $iconSource) {
    Copy-Item -Path $iconSource -Destination (Join-Path $distDir 'app.ico') -Force
}

# 解除文件锁定（如果源文件带有 Zone.Identifier，会被 Windows SmartScreen 拦截）
try { Unblock-File -LiteralPath $distExe -ErrorAction SilentlyContinue } catch { }

# 便捷启动器：先解除锁定，再通过 powershell 以「普通权限」启动。
# 用 `& 调用符（CreateProcess）而不是 Start-Process(ShellExecute)，可绕过 SmartScreen 对未签名 exe 的拦截，
# 同时保持普通权限（不提权），避免提权窗口导致其他程序的截图/快捷键失效。
$launcherPath = Join-Path $distDir '启动多桌面助手.bat'
$launcherText = "@echo off`r`ncd /d `"%~dp0`"`r`npowershell -NoProfile -ExecutionPolicy Bypass -Command `"Unblock-File -LiteralPath '%~dp0MultiDesk.exe' -ErrorAction SilentlyContinue; & '%~dp0MultiDesk.exe'`"`r`n"
[System.IO.File]::WriteAllText($launcherPath, $launcherText, [System.Text.Encoding]::Default)
try { Unblock-File -LiteralPath $launcherPath -ErrorAction SilentlyContinue } catch { }
Write-Host "[publish] wrote: $launcherPath"

# 桌面快捷方式：同样通过 powershell CreateProcess 启动（普通权限、无 SmartScreen）。
try {
    $shortcutPath = Join-Path $distDir '多桌面助手.lnk'
    $powershellExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $powershellExe
    $shortcut.Arguments = '-NoProfile -WindowStyle Hidden -Command "Unblock-File -LiteralPath ' + [char]39 + $distExe + [char]39 + ' -ErrorAction SilentlyContinue; & ' + [char]39 + $distExe + [char]39 + '"'
    $shortcut.WorkingDirectory = $distDir
    $shortcut.IconLocation = ($distExe + ',0')
    $shortcut.Description = '多桌面助手 MultiDesk'
    $shortcut.Save()
    Write-Host "[publish] wrote: $shortcutPath"

    # 卸载入口：--uninstall 会把程序复制到临时目录后弹出卸载界面
    $uninstallLnk = Join-Path $distDir '卸载多桌面助手.lnk'
    $uninstall = $shell.CreateShortcut($uninstallLnk)
    $uninstall.TargetPath = $powershellExe
    $uninstall.Arguments = '-NoProfile -WindowStyle Hidden -Command "& ' + [char]39 + $distExe + [char]39 + ' --uninstall"'
    $uninstall.WorkingDirectory = $distDir
    $uninstall.IconLocation = ($distExe + ',0')
    $uninstall.Description = '卸载多桌面助手 MultiDesk'
    $uninstall.Save()
    Write-Host "[publish] wrote: $uninstallLnk"
} catch {
    Write-Host ('[publish] create shortcut failed: ' + $_.Exception.Message)
}

# 卸载批处理（同样先解除锁定，供喜欢用 bat 的用户）
$uninstallBat = Join-Path $distDir '卸载多桌面助手.bat'
$uninstallBatText = "@echo off`r`ncd /d `"%~dp0`"`r`npowershell -NoProfile -ExecutionPolicy Bypass -Command `"Unblock-File -LiteralPath '%~dp0MultiDesk.exe' -ErrorAction SilentlyContinue; & '%~dp0MultiDesk.exe' --uninstall`"`r`n"
[System.IO.File]::WriteAllText($uninstallBat, $uninstallBatText, [System.Text.Encoding]::Default)
try { Unblock-File -LiteralPath $uninstallBat -ErrorAction SilentlyContinue } catch { }
Write-Host "[publish] wrote: $uninstallBat"

# 一键解除"来自 Internet"锁定（修复「打开文件-安全警告/无法验证发布者」弹窗）
$unblockBat = Join-Path $distDir '解除安全警告.bat'
$unblockBatText = "@echo off`r`ncd /d `"%~dp0`"`r`necho 正在解除本目录文件的安全锁定...`r`npowershell -NoProfile -ExecutionPolicy Bypass -Command `"Get-ChildItem -LiteralPath '%~dp0' -File | Unblock-File -ErrorAction SilentlyContinue`"`r`necho 完成。现在可以双击 MultiDesk.exe 或「多桌面助手.lnk」启动。`r`npause`r`n"
[System.IO.File]::WriteAllText($unblockBat, $unblockBatText, [System.Text.Encoding]::Default)
try { Unblock-File -LiteralPath $unblockBat -ErrorAction SilentlyContinue } catch { }
Write-Host "[publish] wrote: $unblockBat"

$usageText = @"
多桌面助手 MultiDesk $Version 使用说明
========================================

1. 双击本目录的「多桌面助手.lnk」启动（推荐；等价于直接启动 MultiDesk.exe），
   程序驻留系统托盘（任务栏右下角的小图标）。
2. 默认所有桌面共用同一套正在运行的程序：切换桌面只切换桌面图标/壁纸，
   不会隐藏窗口、不会改变任务栏和小托盘状态（程序界面保持原样）。
   如仍需要旧的"切换桌面时隐藏其它桌面窗口"行为，可在托盘右键勾选「窗口隔离」。
   右键托盘图标还可以：切换桌面、回到原桌面/返回多桌面、暂停窗口管理、恢复所有窗口、打开设置、退出。
   想让某个程序（如音乐播放器）在切换桌面后仍然显示：先激活它的窗口，
   托盘右键 →「跨桌面显示的程序」→「固定当前窗口到所有桌面」即可（可多次固定多个程序）。
   选择「回到原桌面（真实桌面）」后所有窗口恢复、系统桌面图标显示、多桌面图层隐藏（程序不退出）；
   原桌面模式下右键托盘选「返回多桌面」即可回去。
3. 快捷键：
   Ctrl+Alt+1 ~ Ctrl+Alt+9        切换到第 1~9 个桌面
   Ctrl+Alt+左/右方向键           上一个 / 下一个桌面
   Ctrl+Alt+Shift+左/右方向键     把当前窗口移到上一个 / 下一个桌面
   Ctrl+Alt+S                     打开设置窗口
   Ctrl+Alt+Shift+R               恢复所有被隐藏的窗口
4. 桌面图层上：单击选中图标、双击启动、拖动排列；可把文件/文件夹直接拖到桌面上。
   在桌面空白处右键 = 程序桌面菜单：「查看」（图标大小/自动排列/显示桌面名称）、
   「排序方式」、「刷新」、「新建」（文件夹/文本文档）都只作用于当前虚拟桌面；
   菜单底部「更多选项…」可打开 Windows 原生桌面菜单（含第三方扩展项）。
   在图标上右键 = Windows 原生文件菜单（剪切/复制/重命名/共享/删除/打开/以管理员身份运行/
   压缩/属性/第三方扩展等，操作作用于真实的文件/快捷方式，与原桌面一致）。
   如果目标不存在/网址等无法显示原生菜单时会回退为程序菜单（打开/打开位置/从本桌面移除）。
5. 设置窗口中可以新建、重命名、上移/下移、删除桌面，并为每个桌面单独配置壁纸与图标。
   「跟随当前桌面壁纸」会透明显示真实桌面，支持 Wallpaper Engine 等动态壁纸；
   「图标外观」默认「与 Windows 桌面图标大小/间距一致」（推荐，跟系统原桌面一样），
   也可取消后自定义 32/48/64px 与自动排列/自由摆放。
6. 切换桌面只是把其它桌面的窗口隐藏，程序进程仍在后台运行（下载、游戏、编译等不会停止）；
   托盘悬停提示会显示"N 个窗口后台运行"。个别 UWP/独占全屏游戏若在窗口隐藏时自行暂停，属应用自身行为。
7. 退出请使用托盘菜单"退出多桌面助手"（或设置中的"退出程序"），
   正常退出会把所有被隐藏的窗口恢复显示。
8. 数据目录：优先使用程序同目录的 MultiDesk-data\（便携模式）；若不可写会自动回退到
   %LOCALAPPDATA%\MultiDesk 等位置。也可以用 --data-dir=<目录> 指定（设置窗口可查看实际目录）。
   目录中包含 config.json 配置、app.log 日志、session.json 会话日志。
9. 启动方式（重要）：
   - 推荐双击本目录里的「多桌面助手.lnk」快捷方式，或把快捷方式复制到桌面后使用；
   - 也可以双击「启动多桌面助手.bat」：它会先解除文件锁定（避免 Windows SmartScreen
     拦截未签名程序），再正常启动 MultiDesk；
   - 如果出现「Windows 已保护你的电脑」：点「更多信息」→「仍要运行」即可；
     或右键 MultiDesk.exe → 属性 → 勾选「解除锁定」→ 确定后再运行。
   - 如果出现「打开文件 - 安全警告 / 无法验证发布者」：说明文件带了"来自 Internet"的锁定，
     双击本目录的「解除安全警告.bat」一键解除即可（以后不会再弹）；或右键 MultiDesk.exe →
     属性 → 勾选「解除锁定」→ 确定。程序启动时也会自动解除自身及同目录文件的锁定。
10. 请从资源管理器/快捷方式启动（普通权限即可），以获得完整功能：实时/动态壁纸、
   隐藏系统桌面图标、桌面右键菜单等。注意：请勿以管理员身份运行——提权窗口会使
   其他程序的截图/快捷键（如 Snipaste、QQ/微信截图）暂时失效；快捷方式/启动 bat 已是普通权限。
11. 从聊天软件/沙箱直接打开时程序会以受限模式运行并提示"是否以完整权限重启"。
    选择"否"后会记住并不再提示（设置里可重新打开提示），托盘右键仍有"以完整权限重启（推荐）"。
12. 卸载：双击本目录里的「卸载多桌面助手.lnk」（或「卸载多桌面助手.bat」），
    确认后会自动退出程序、恢复被隐藏的窗口/桌面图标，并删除程序文件、快捷方式、
    开机自启项、托盘设置以及全部配置数据（MultiDesk-data，删除后不可恢复）。
    如需保留配置，请在卸载前先把 MultiDesk-data 文件夹复制到其它位置。

13. 系统要求：Windows 10 / Windows 11（x64），.NET Framework 4.8（系统自带，无需安装）。
14. 开发方式：本程序由 AI 辅助开发（设计、编码、测试与文档均在 AI 编程助手辅助下完成，由维护者审核、验证并发布）。

详细说明请见项目 README.md。
"@
$usagePath = Join-Path $distDir '使用说明.txt'
[System.IO.File]::WriteAllText($usagePath, $usageText, (New-Object System.Text.UTF8Encoding($true)))
Write-Host "[publish] wrote: $usagePath"

# The dist folder must only contain the application exe (plus optional icon / txt).
$unexpected = @(Get-ChildItem -Path $distDir -File | Where-Object {
        $_.Extension -eq '.dll' -or $_.Extension -eq '.nlp' -or $_.Extension -eq '.pdb' -or $_.Extension -eq '.xml'
    })
if ($unexpected.Count -gt 0) {
    throw ("dist contains unexpected framework/debug files: " + (($unexpected | ForEach-Object { $_.Name }) -join ', '))
}
Write-Host '[publish] dist content:'
Get-ChildItem -Path $distDir -File | ForEach-Object {
    Write-Host ("  " + $_.Name + "  (" + [math]::Round($_.Length / 1024.0, 1) + " KB)")
}

# Smoke test: the packaged exe must run standalone from the dist folder.
$verifyData = Join-Path $root 'obj\publish-verify'
if (Test-Path $verifyData) {
    Remove-Item -Path $verifyData -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Force -Path $verifyData | Out-Null
Write-Host "[publish] smoke run: $distExe --ui-only --data-dir=$verifyData --exit-after=6"

$startInfo = New-Object System.Diagnostics.ProcessStartInfo
$startInfo.FileName = $distExe
$startInfo.Arguments = '--ui-only --data-dir="' + $verifyData + '" --exit-after=6'
$startInfo.UseShellExecute = $false
$startInfo.WorkingDirectory = $distDir
$process = New-Object System.Diagnostics.Process
$process.StartInfo = $startInfo
$null = $process.Start()
$finished = $process.WaitForExit(40000)
if (-not $finished) {
    try { $process.Kill() } catch { }
    throw 'packaged MultiDesk.exe did not exit within 40s during the smoke run'
}
$exitCode = $process.ExitCode
if ($exitCode -ne 0) {
    throw ("packaged MultiDesk.exe smoke run failed, exit code " + $exitCode)
}
$logPath = Join-Path $verifyData 'app.log'
if (Test-Path $logPath) {
    $logText = [System.IO.File]::ReadAllText($logPath, [System.Text.Encoding]::UTF8)
    if ($logText -notmatch 'starting:') {
        throw "packaged MultiDesk.exe smoke run did not write a normal startup log"
    }
}
Write-Host '[publish] standalone smoke run OK (--ui-only --exit-after=6, exit code 0)'
Remove-Item -Path $verifyData -Recurse -Force -ErrorAction SilentlyContinue

$hash = (Get-FileHash -Path $distExe -Algorithm SHA256).Hash
Write-Host ("[publish] MultiDesk.exe SHA256: " + $hash)

if (-not $NoZip) {
    $zipPath = Join-Path $root ("dist\MultiDesk-" + $Version + ".zip")
    if (Test-Path $zipPath) { Remove-Item -Path $zipPath -Force }
    # 打包时排除用户数据目录 MultiDesk-data（配置/日志/DesktopItems 属于用户私有数据，不对外分发）
    $stagingRoot = Join-Path $env:TEMP ("MultiDesk_zip_" + [guid]::NewGuid().ToString('N'))
    $stagingApp = Join-Path $stagingRoot 'MultiDesk'
    New-Item -ItemType Directory -Force -Path $stagingApp | Out-Null
    Get-ChildItem -Path $distDir -Force | Where-Object { $_.Name -ne 'MultiDesk-data' } | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $stagingApp -Recurse -Force
    }
    Compress-Archive -Path $stagingApp -DestinationPath $zipPath -Force
    try { Remove-Item -Path $stagingRoot -Recurse -Force } catch { }
    Write-Host ("[publish] zip: " + $zipPath + "（已排除 MultiDesk-data 用户数据）")
}

Write-Host ''
$doneMessage = "[publish] DONE. Version " + $Version + " -> " + $distDir
Write-Host $doneMessage
