# MultiDesk 1.2.0 Medium-integrity in-process E2E.
#
# Loads MultiDesk.exe inside a normal (Medium integrity) PowerShell process so that
# the full feature path can be verified. A LOW integrity process (e.g. started from
# a chat app) cannot touch explorer's desktop windows, and WPF ignores synthetic mouse
# messages, so the layer window's own menu/command methods are invoked through
# reflection instead of simulating mouse clicks.
#
# Verifies for 1.2.0:
#   * [icon-metrics] uses the real Windows desktop metrics (icon/cell/origin)
#   * self-drawn desktop function menu (screenshot) + 查看 icon size 32/64 effect,
#     自动排列 / 显示桌面名称 toggles, sort by name, new folder only in the virtual
#     desktop (real desktop folder untouched), 更多选项 -> native Windows menu
#   * desktop switch multi-frame capture: no blank/black intermediate frame
#   * settings window: switch-button state refresh after external switch, icon-size /
#     auto-arrange / match-Windows / restricted-prompt controls persist to config
#   * real desktop icons hidden while running and restored after normal exit (marker)
#
# REQUIREMENTS: STA PowerShell, NOT low integrity, e.g. a normal desktop PowerShell 5.1:
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-medium-app.ps1 -Fresh
#
# NOTE: AppController.Shutdown() calls Environment.Exit, so this script terminates the
# hosting PowerShell at the end; results are written to <data-dir>\medium-run-state.log
# and <data-dir>\post-exit-state.log for the parent/verifier to read.
#
# ASCII-only source: Windows PowerShell 5.1 misreads BOM-less UTF-8 with non-ASCII text.
param(
    [string]$ExePath = '',
    [string]$DataDir = 'obj\medium-120',
    [string]$OutDir = 'screenshots',
    [switch]$Fresh,
    [int]$SettleMs = 2600
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot

function Resolve-ProjectPath([string]$path) {
    if ([System.IO.Path]::IsPathRooted($path)) { return $path }
    return (Join-Path $projectRoot $path)
}

if (-not $ExePath) { $ExePath = Join-Path $projectRoot 'src\bin\Release\MultiDesk.exe' }
$ExePath = Resolve-ProjectPath $ExePath
if (-not (Test-Path $ExePath)) { throw "MultiDesk.exe not found: $ExePath (run build.ps1 -Release first)" }
$DataDir = Resolve-ProjectPath $DataDir
$OutDir = Resolve-ProjectPath $OutDir
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
if ($Fresh -and (Test-Path $DataDir)) { Remove-Item -Path $DataDir -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force -Path $DataDir | Out-Null

$statePath = Join-Path $DataDir 'medium-run-state.log'
$postExitPath = Join-Path $DataDir 'post-exit-state.log'
if (Test-Path $statePath) { Remove-Item $statePath -Force -ErrorAction SilentlyContinue }
if (Test-Path $postExitPath) { Remove-Item $postExitPath -Force -ErrorAction SilentlyContinue }
$script:appLogPath = Join-Path $DataDir 'app.log'
if (Test-Path $script:appLogPath) { Remove-Item $script:appLogPath -Force -ErrorAction SilentlyContinue }

function Log([string]$message) {
    $line = (Get-Date -Format 'HH:mm:ss.fff') + ' ' + $message
    Write-Host $line
    try { [System.IO.File]::AppendAllText($statePath, $line + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding($false))) } catch { }
}
function Read-ConfigJson {
    $text = [System.IO.File]::ReadAllText((Join-Path $DataDir 'config.json'), [System.Text.Encoding]::UTF8)
    return ConvertFrom-Json $text
}
function Wait-Dispatcher([int]$milliseconds) {
    if ($milliseconds -le 0) { return }
    $script:pumpFrame = New-Object System.Windows.Threading.DispatcherFrame
    $script:pumpTimer = New-Object System.Windows.Threading.DispatcherTimer
    $script:pumpTimer.Interval = [TimeSpan]::FromMilliseconds($milliseconds)
    $script:pumpTimer.Add_Tick({
            try { $script:pumpTimer.Stop() } catch { }
            try { $script:pumpFrame.Continue = $false } catch { }
        })
    $script:pumpTimer.Start()
    [System.Windows.Threading.Dispatcher]::PushFrame($script:pumpFrame)
}

$apartment = [System.Threading.Thread]::CurrentThread.GetApartmentState()
if ($apartment -ne [System.Threading.ApartmentState]::STA) { throw "This script must run in an STA PowerShell (current: $apartment)." }

try {
    Add-Type -Namespace MultiDeskVerify -Name DpiHelper -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool SetProcessDPIAware();
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, System.UIntPtr dwExtraInfo);
'@
    [MultiDeskVerify.DpiHelper]::SetProcessDPIAware() | Out-Null
} catch {
}

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

$asm = [System.Reflection.Assembly]::Load([System.IO.File]::ReadAllBytes($ExePath))
$integrityType = $asm.GetType('MultiDesk.Native.ProcessIntegrity')
$isLow = [bool]$integrityType.GetMethod('IsLowIntegrity').Invoke($null, @())
Log ("host: pid=" + $PID + " apartment=" + $apartment + " lowIntegrity=" + $isLow)
if ($isLow) { throw "Current PowerShell is LOW integrity: Medium-only features are unavailable. Start this script from a normal desktop PowerShell." }

try {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace MultiDeskVerify
{
    public class WinEntry
    {
        public long Handle;
        public int ProcessId;
        public string ClassName;
        public int Left;
        public int Top;
        public int Width;
        public int Height;
        public bool Visible;
    }

    public static class Probe
    {
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string windowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint flags);

        public static IntPtr FindDefView()
        {
            IntPtr progman = FindWindow("Progman", null);
            if (progman != IntPtr.Zero)
            {
                IntPtr defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (defView != IntPtr.Zero) return defView;
            }
            List<WinEntry> all = ListAll();
            for (int i = 0; i < all.Count; i++)
            {
                if (!string.Equals(all[i].ClassName, "WorkerW", StringComparison.Ordinal)) continue;
                IntPtr child = FindWindowEx(new IntPtr(all[i].Handle), IntPtr.Zero, "SHELLDLL_DefView", null);
                if (child != IntPtr.Zero) return child;
            }
            return IntPtr.Zero;
        }

        public static bool DesktopIconsVisible()
        {
            IntPtr defView = FindDefView();
            if (defView == IntPtr.Zero) return false;
            IntPtr listView = FindWindowEx(defView, IntPtr.Zero, "SysListView32", "FolderView");
            return listView != IntPtr.Zero && IsWindowVisible(listView);
        }

        public static List<WinEntry> ListAll()
        {
            List<WinEntry> result = new List<WinEntry>();
            EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                WinEntry info = new WinEntry();
                info.Handle = hWnd.ToInt64();
                int pid;
                GetWindowThreadProcessId(hWnd, out pid);
                info.ProcessId = pid;
                StringBuilder cls = new StringBuilder(256);
                GetClassName(hWnd, cls, cls.Capacity);
                info.ClassName = cls.ToString();
                RECT rect;
                if (GetWindowRect(hWnd, out rect))
                {
                    info.Left = rect.Left;
                    info.Top = rect.Top;
                    info.Width = rect.Right - rect.Left;
                    info.Height = rect.Bottom - rect.Top;
                }
                info.Visible = IsWindowVisible(hWnd);
                result.Add(info);
                return true;
            }, IntPtr.Zero);
            return result;
        }

        public static long FindLargestWindow(int pid)
        {
            long best = 0;
            long bestArea = 0;
            List<WinEntry> list = ListAll();
            for (int i = 0; i < list.Count; i++)
            {
                WinEntry info = list[i];
                if (info.ProcessId != pid || !info.Visible) continue;
                long area = (long)info.Width * (long)info.Height;
                if (area <= 0) continue;
                if (best == 0 || area > bestArea) { best = info.Handle; bestArea = area; }
            }
            return best;
        }

        public static long FindSecondLargestWindow(int pid, long exclude)
        {
            long best = 0;
            long bestArea = 0;
            List<WinEntry> list = ListAll();
            for (int i = 0; i < list.Count; i++)
            {
                WinEntry info = list[i];
                if (info.ProcessId != pid || !info.Visible) continue;
                if (info.Handle == exclude) continue;
                long area = (long)info.Width * (long)info.Height;
                if (area < 100 * 100) continue;
                if (best == 0 || area > bestArea) { best = info.Handle; bestArea = area; }
            }
            return best;
        }

        public static WinEntry Describe(long handle)
        {
            List<WinEntry> list = ListAll();
            for (int i = 0; i < list.Count; i++) { if (list[i].Handle == handle) return list[i]; }
            return null;
        }

        public static int[] GetRectOf(long handle)
        {
            RECT rect;
            if (!GetWindowRect(new IntPtr(handle), out rect)) return null;
            return new int[] { rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top };
        }

        public static bool Render(long handle, IntPtr hdc, uint flags)
        {
            return PrintWindow(new IntPtr(handle), hdc, flags);
        }
    }
}
'@
} catch {
    Log ("WARNING: Add-Type failed: " + $_.Exception.Message)
}

$instanceFlags = [System.Reflection.BindingFlags]::InvokeMethod -bor [System.Reflection.BindingFlags]::NonPublic -bor [System.Reflection.BindingFlags]::Instance
$fieldFlags = [System.Reflection.BindingFlags]::NonPublic -bor [System.Reflection.BindingFlags]::Instance

function Invoke-Private($target, [string]$name, [object[]]$arguments) {
    $count = 0
    if ($null -ne $arguments) { $count = $arguments.Count }
    $argsArray = New-Object 'System.Object[]' ([Math]::Max(1, $count))
    if ($count -eq 0) { $argsArray = New-Object 'System.Object[]' 0 } else {
        for ($i = 0; $i -lt $count; $i++) { $argsArray[$i] = $arguments[$i] }
    }
    return $target.GetType().InvokeMember($name, $instanceFlags, $null, $target, $argsArray)
}
function Get-PrivateField($target, [string]$name) {
    return $target.GetType().GetField($name, $fieldFlags).GetValue($target)
}

function Save-FullScreen([string]$name) {
    $target = Join-Path $OutDir $name
    $bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $bitmap = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try { $graphics.CopyFromScreen($bounds.Left, $bounds.Top, 0, 0, $bitmap.Size) } finally { $graphics.Dispose() }
    try { $bitmap.Save($target, [System.Drawing.Imaging.ImageFormat]::Png) } finally { $bitmap.Dispose() }
    Log ("screenshot(full): " + $name + " (" + $bounds.Width + "x" + $bounds.Height + ")")
}
function Save-WindowShot([long]$handle, [string]$name) {
    if ($handle -eq 0) { Log ("screenshot(window): no handle for " + $name); return }
    $info = [MultiDeskVerify.Probe]::Describe($handle)
    if ($null -eq $info -or $info.Width -le 0 -or $info.Height -le 0) { Log ("screenshot(window): invalid handle for " + $name); return }
    $target = Join-Path $OutDir $name
    $bitmap = New-Object System.Drawing.Bitmap($info.Width, $info.Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $rendered = $false
    try { $hdc = $graphics.GetHdc(); try { $rendered = [MultiDeskVerify.Probe]::Render($handle, $hdc, 2) } finally { $graphics.ReleaseHdc($hdc) } } finally { $graphics.Dispose() }
    try { $bitmap.Save($target, [System.Drawing.Imaging.ImageFormat]::Png) } finally { $bitmap.Dispose() }
    Log ("screenshot(window): " + $name + " rendered=" + $rendered + " " + $info.Width + "x" + $info.Height)
}
function Save-DefViewShot([string]$name) {
    $handle = [MultiDeskVerify.Probe]::FindDefView()
    if ($handle -eq [IntPtr]::Zero) { Log 'screenshot(defview): defview not found'; return }
    $rect = [MultiDeskVerify.Probe]::GetRectOf($handle.ToInt64())
    if ($null -eq $rect -or $rect[2] -le 0 -or $rect[3] -le 0) { Log ('screenshot(defview): invalid rect for ' + $name); return }
    $target = Join-Path $OutDir $name
    $bitmap = New-Object System.Drawing.Bitmap($rect[2], $rect[3])
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $rendered = $false
    try { $hdc = $graphics.GetHdc(); try { $rendered = [MultiDeskVerify.Probe]::Render($handle.ToInt64(), $hdc, 2) } finally { $graphics.ReleaseHdc($hdc) } } finally { $graphics.Dispose() }
    try { $bitmap.Save($target, [System.Drawing.Imaging.ImageFormat]::Png) } finally { $bitmap.Dispose() }
    Log ("screenshot(defview): " + $name + " rendered=" + $rendered + " " + $rect[2] + "x" + $rect[3])
}
function Save-LayerVisual([string]$name) {
    try {
        $width = [int][Math]::Round($layer.ActualWidth)
        $height = [int][Math]::Round($layer.ActualHeight)
        if ($width -le 0 -or $height -le 0) { $width = 2560; $height = 1600 }
        $rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($width, $height, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
        $rtb.Render($layer)
        $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
        $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($rtb))
        $target = Join-Path $OutDir $name
        $stream = [System.IO.File]::Create($target)
        try { $encoder.Save($stream) } finally { $stream.Dispose() }
        Log ("screenshot(layer-visual): " + $name + " " + $width + "x" + $height)
    } catch {
        Log ("screenshot(layer-visual) failed: " + $_.Exception.Message)
    }
}
function Send-Escape() {
    try {
        [MultiDeskVerify.DpiHelper]::keybd_event(0x1B, 0, 0, [System.UIntPtr]::Zero)
        Start-Sleep -Milliseconds 60
        [MultiDeskVerify.DpiHelper]::keybd_event(0x1B, 0, 2, [System.UIntPtr]::Zero)
    } catch { }
}

function Write-TestConfig {
    $system32 = Join-Path $env:WINDIR 'System32'
    $workIcons = @(
        [ordered]@{ id = 'work-notepad'; name = 'notepad'; target = (Join-Path $system32 'notepad.exe'); args = ''; workdir = $system32; x = 0; y = 3.33 },
        [ordered]@{ id = 'work-explorer'; name = 'explorer'; target = (Join-Path $env:WINDIR 'explorer.exe'); args = ''; workdir = $env:WINDIR; x = 0; y = 101.33 }
    )
    $gameIcons = @(
        [ordered]@{ id = 'game-mspaint'; name = 'mspaint'; target = (Join-Path $system32 'mspaint.exe'); args = ''; workdir = $system32; x = 0; y = 3.33 },
        [ordered]@{ id = 'game-charmap'; name = 'charmap'; target = (Join-Path $system32 'charmap.exe'); args = ''; workdir = $system32; x = 0; y = 101.33 },
        [ordered]@{ id = 'game-control'; name = 'control'; target = (Join-Path $system32 'control.exe'); args = ''; workdir = $system32; x = 0; y = 199.33 }
    )
    $work = [ordered]@{ id = 'desk-work'; name = 'Work'; wallpaperMode = 0; wallpaperPath = ''; wallpaperFit = 0; backgroundColor = '#FF17324F'; showBadge = $true; icons = $workIcons }
    $game = [ordered]@{ id = 'desk-game'; name = 'Game'; wallpaperMode = 2; wallpaperPath = ''; wallpaperFit = 0; backgroundColor = '#FF4F1732'; showBadge = $true; icons = $gameIcons }
    $config = [ordered]@{
        version         = 3
        desktops        = @($work, $game)
        activeDesktopId = 'desk-work'
        options         = [ordered]@{
            startWithWindows        = $false
            showDesktopName         = $true
            firstRunDone            = $true
            iconSize                = 48
            autoArrangeIcons        = $true
            matchWindowsIconMetrics = $true
            skipRestrictedPrompt    = $false
        }
    }
    [System.IO.File]::WriteAllText((Join-Path $DataDir 'config.json'), ($config | ConvertTo-Json -Depth 8), (New-Object System.Text.UTF8Encoding($false)))
    Log 'test config written (version 3, 2 desktops, matchWindowsIconMetrics=true)'
}

function Register-PostExitVerification([string]$markerPath) {
    $global:MultiDeskPostExitMarker = $markerPath
    $global:MultiDeskPostExitStatePath = $postExitPath
    [AppDomain]::CurrentDomain.add_ProcessExit([EventHandler]{
            try {
                $visible = [bool]([MultiDeskVerify.Probe]::DesktopIconsVisible())
                $lines = @('iconsVisibleAfterExit=' + $visible, 'markerExistsAfterExit=' + (Test-Path $global:MultiDeskPostExitMarker))
                [System.IO.File]::WriteAllText($global:MultiDeskPostExitStatePath, ($lines -join [Environment]::NewLine), (New-Object System.Text.UTF8Encoding($false)))
            } catch { }
        })
}

# ---------------------------------------------------------------------------
# main
# ---------------------------------------------------------------------------
Write-TestConfig
$desktopFolder = [Environment]::GetFolderPath('Desktop')
$desktopEntriesBefore = 0
try { $desktopEntriesBefore = @([System.IO.Directory]::GetFileSystemEntries($desktopFolder)).Count } catch { }
Log ('real desktop folder entries BEFORE = ' + $desktopEntriesBefore)
Log ('desktop icons visible BEFORE = ' + ([MultiDeskVerify.Probe]::DesktopIconsVisible()))
Save-DefViewShot '12-real-desktop-icons.png'

$controllerType = $asm.GetType('MultiDesk.AppController')
$ctorArgs = New-Object 'System.Object[]' 7
$ctorArgs[0] = $DataDir; $ctorArgs[1] = $true; $ctorArgs[2] = $false; $ctorArgs[3] = 0
$ctorArgs[4] = $null; $ctorArgs[5] = $null; $ctorArgs[6] = $true
$controller = [Activator]::CreateInstance($controllerType, $ctorArgs)
$controller.Start()
Wait-Dispatcher $SettleMs

$integration = [bool]$controller.IsDesktopIntegrationAvailable
$layerHandle = [MultiDeskVerify.Probe]::FindLargestWindow($PID)
$metrics = $controller.GetIconMetrics()
Log ('IsDesktopIntegrationAvailable = ' + $integration)
Log ('desktop icons visible DURING = ' + ([MultiDeskVerify.Probe]::DesktopIconsVisible()))
Log ('marker exists DURING = ' + (Test-Path (Join-Path $DataDir 'desktop-icons-hidden.marker')))
Log ('icon metrics: icon=' + $metrics.IconSize + ' cell=' + $metrics.CellWidth.ToString('F2') + 'x' + $metrics.CellHeight.ToString('F2') + ' origin=' + $metrics.MarginX.ToString('F2') + ',' + $metrics.MarginY.ToString('F2'))
Log ('layer window handle = 0x' + $layerHandle.ToString('X'))
$config0 = Read-ConfigJson
Log ('config icons: work=' + $config0.desktops[0].icons.Count + ' game=' + $config0.desktops[1].icons.Count + ' (no auto-import of real desktop icons)')

$layer = Get-PrivateField $controller '_layer'
if ($null -eq $layer) { throw 'layer window is not available' }
Save-WindowShot $layerHandle '12-icon-match-layer.png'

try {
    $iconCanvas = $layer.FindName('IconCanvas')
    $index = 0
    foreach ($child in $iconCanvas.Children) {
        $x = [System.Windows.Controls.Canvas]::GetLeft($child)
        $y = [System.Windows.Controls.Canvas]::GetTop($child)
        Log ('canvas icon #' + $index + ' = (' + $x.ToString('F2') + ', ' + $y.ToString('F2') + ')')
        $index++
        if ($index -ge 4) { break }
    }
} catch { Log ('canvas position probe failed: ' + $_.Exception.Message) }

# --- self-drawn desktop function menu -------------------------------------
$point = New-Object System.Windows.Point(360, 300)
Invoke-Private $layer 'ShowDesktopFunctionMenu' @($point) | Out-Null
Wait-Dispatcher 500
$menu = Get-PrivateField $layer '_menu'
if ($null -ne $menu) { Log ('menu IsOpen = ' + [bool]$menu.IsOpen) }
Save-LayerVisual '12-desktop-menu.png'
Save-WindowShot $layerHandle '12-desktop-menu-printwindow.png'
Invoke-Private $layer 'CloseMenu' @() | Out-Null
Wait-Dispatcher 300
if ($null -ne $menu) { Log ('menu IsOpen after close = ' + [bool]$menu.IsOpen) }

# --- 查看 icon size: disable match-Windows first (same as settings checkbox) ---
$controller.Config.Options.MatchWindowsIconMetrics = $false
$controller.NotifyConfigChanged()
Wait-Dispatcher 700
Invoke-Private $layer 'SetOptionIconSize' @(64) | Out-Null
Wait-Dispatcher 800
$metrics64 = $controller.GetIconMetrics()
$config64 = Read-ConfigJson
Log ('after menu size=64: metrics.icon=' + $metrics64.IconSize + ' config.iconSize=' + $config64.options.iconSize + ' cell=' + $metrics64.CellWidth.ToString('F1') + 'x' + $metrics64.CellHeight.ToString('F1'))
Save-WindowShot $layerHandle '12-icon-size-64.png'
Invoke-Private $layer 'SetOptionIconSize' @(32) | Out-Null
Wait-Dispatcher 800
$metrics32 = $controller.GetIconMetrics()
$config32 = Read-ConfigJson
Log ('after menu size=32: metrics.icon=' + $metrics32.IconSize + ' config.iconSize=' + $config32.options.iconSize + ' cell=' + $metrics32.CellWidth.ToString('F1') + 'x' + $metrics32.CellHeight.ToString('F1'))
Save-WindowShot $layerHandle '12-icon-size-32.png'
Invoke-Private $layer 'SetOptionIconSize' @(48) | Out-Null
Wait-Dispatcher 500

# --- menu toggles: auto arrange / show desktop name ------------------------
Invoke-Private $layer 'ToggleAutoArrangeIcons' @() | Out-Null
Wait-Dispatcher 400
Invoke-Private $layer 'ToggleShowDesktopName' @() | Out-Null
Wait-Dispatcher 400
$configToggle = Read-ConfigJson
Log ('after menu toggles: autoArrange=' + $configToggle.options.autoArrangeIcons + ' showDesktopName=' + $configToggle.options.showDesktopName)
Save-WindowShot $layerHandle '12-menu-toggles.png'
Invoke-Private $layer 'ToggleAutoArrangeIcons' @() | Out-Null
Invoke-Private $layer 'ToggleShowDesktopName' @() | Out-Null
Wait-Dispatcher 400

# --- sort by name ----------------------------------------------------------
$beforeSort = (Read-ConfigJson).desktops[0].icons | ForEach-Object { $_.name }
Invoke-Private $layer 'SortCurrentDesktopIcons' @(0) | Out-Null
Wait-Dispatcher 400
$afterSort = (Read-ConfigJson).desktops[0].icons | ForEach-Object { $_.name }
Log ('sort by name: before=[' + ($beforeSort -join ',') + '] after=[' + ($afterSort -join ',') + ']')

# --- new folder: virtual desktop only -------------------------------------
$iconsBefore = (Read-ConfigJson).desktops[0].icons.Count
Invoke-Private $layer 'CreateNewDesktopItem' @($true) | Out-Null
Wait-Dispatcher 600
$configNew = Read-ConfigJson
$desktopEntriesAfter = 0
try { $desktopEntriesAfter = @([System.IO.Directory]::GetFileSystemEntries($desktopFolder)).Count } catch { }
$itemsDir = Join-Path $DataDir 'DesktopItems'
$itemsEntries = 'none'
if (Test-Path $itemsDir) { $itemsEntries = (@(Get-ChildItem $itemsDir | ForEach-Object { $_.Name }) -join ',') }
Log ('new folder: iconsBefore=' + $iconsBefore + ' iconsAfter=' + $configNew.desktops[0].icons.Count + ' realDesktopEntries=' + $desktopEntriesAfter + ' (was ' + $desktopEntriesBefore + ') DesktopItems=[' + $itemsEntries + ']')
Save-WindowShot $layerHandle '12-new-folder.png'

# --- menu item: more options -> native Windows menu -----------------------
Invoke-Private $layer 'ShowNativeDesktopMenuFromItem' @() | Out-Null
Wait-Dispatcher 900
Save-FullScreen '12-more-options-native.png'
Log ('native menu via controller returned ' + [bool]$controller.ShowDesktopContextMenu(1280, 800) + ' (expected True)')
Wait-Dispatcher 700
Send-Escape
Wait-Dispatcher 400

# --- switch multi-frame capture (no blank intermediate frames) ------------
# Use two clearly different solid wallpapers for the frame classification.
$controller.Config.Desktops[0].WallpaperMode = 2
$controller.Config.Desktops[0].BackgroundColor = '#FF17324F'
$controller.NotifyConfigChanged()
Wait-Dispatcher 800

function Get-FrameSignature($bitmap, [int]$step) {
    $sumR = 0.0; $sumG = 0.0; $sumB = 0.0; $dark = 0; $count = 0
    for ($y = 0; $y -lt $bitmap.Height; $y += $step) {
        for ($x = 0; $x -lt $bitmap.Width; $x += $step) {
            $color = $bitmap.GetPixel($x, $y)
            $sumR += $color.R; $sumG += $color.G; $sumB += $color.B
            if (($color.R + $color.G + $color.B) -lt 24) { $dark++ }
            $count++
        }
    }
    if ($count -eq 0) { $count = 1 }
    return @(($sumR / $count), ($sumG / $count), ($sumB / $count), ($dark / [double]$count))
}
function Capture-LayerFrame([int]$step) {
    $info = [MultiDeskVerify.Probe]::Describe($layerHandle)
    if ($null -eq $info -or $info.Width -le 0) { return $null }
    $bitmap = New-Object System.Drawing.Bitmap($info.Width, $info.Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try { $hdc = $graphics.GetHdc(); try { [MultiDeskVerify.Probe]::Render($layerHandle, $hdc, 2) | Out-Null } finally { $graphics.ReleaseHdc($hdc) } } finally { $graphics.Dispose() }
    $signature = Get-FrameSignature $bitmap $step
    $bitmap.Dispose()
    return $signature
}
$frameA = Capture-LayerFrame 64
$switchStart = [DateTime]::Now
$controller.RequestSwitchIndex(1)
$switchMs = ([DateTime]::Now - $switchStart).TotalMilliseconds
$frames = @()
for ($i = 0; $i -lt 8; $i++) {
    $frame = Capture-LayerFrame 64
    if ($null -ne $frame) { $frames += , $frame }
    Start-Sleep -Milliseconds 25
}
Wait-Dispatcher 700
$frameB = Capture-LayerFrame 64
function Frame-Distance($a, $b) {
    if ($null -eq $a -or $null -eq $b) { return 999 }
    return ([Math]::Abs($a[0] - $b[0]) + [Math]::Abs($a[1] - $b[1]) + [Math]::Abs($a[2] - $b[2]))
}
$blankFrames = 0
$matched = 0
$other = 0
foreach ($frame in $frames) {
    $blank = ($frame[3] -gt 0.97) -and (($frame[0] + $frame[1] + $frame[2]) -lt 30)
    if ($blank) { $blankFrames++; continue }
    $distance = [Math]::Min((Frame-Distance $frame $frameA), (Frame-Distance $frame $frameB))
    if ($distance -lt 20) { $matched++ } else { $other++ }
}
Log ('switch frames: count=' + $frames.Count + ' matchedAB=' + $matched + ' blank=' + $blankFrames + ' other=' + $other + ' switchCallMs=' + $switchMs.ToString('F1'))
Save-WindowShot $layerHandle '12-switch-desktop2.png'
$controller.RequestSwitchIndex(0)
Wait-Dispatcher 800
Save-WindowShot $layerHandle '12-switch-desktop1.png'

# --- settings window: button state + new checkboxes -----------------------
$controller.OpenSettings()
Wait-Dispatcher 1200
$settings = Get-PrivateField $controller '_settings'
if ($null -eq $settings) { throw 'settings window is not available' }
$window = [System.Windows.Window]$settings
$desktopList = $window.FindName('DesktopList')
$switchButton = $window.FindName('SwitchDesktopButton')
$matchCheck = $window.FindName('MatchWindowsIconsCheck')
$restrictedCheck = $window.FindName('RestrictedPromptCheck')
$autoArrangeCheck = $window.FindName('AutoArrangeCheck')

$desktopList.SelectedIndex = 1
Wait-Dispatcher 300
Log ('settings: after selecting desktop 2 -> switchButton content=[' + [string]$switchButton.Content + '] enabled=' + [bool]$switchButton.IsEnabled)
$controller.RequestSwitchIndex(1)
Wait-Dispatcher 700
Log ('settings: after external switch to desktop 2 -> switchButton content=[' + [string]$switchButton.Content + '] enabled=' + [bool]$switchButton.IsEnabled + ' currentIndex=' + $controller.CurrentDesktopIndex)
$desktopList.SelectedIndex = 0
Wait-Dispatcher 300
Log ('settings: after selecting desktop 1 -> switchButton content=[' + [string]$switchButton.Content + '] enabled=' + [bool]$switchButton.IsEnabled)
Invoke-Private $window 'RefreshGeneralPage' @() | Out-Null
Wait-Dispatcher 300
Log ('settings: general page -> restrictedPrompt=[' + [string]$restrictedCheck.IsChecked + '] (config skip=' + (Read-ConfigJson).options.skipRestrictedPrompt + ')')
Log ('settings: checkboxes before -> match=[' + [string]$matchCheck.IsChecked + '] autoArrange=[' + [string]$autoArrangeCheck.IsChecked + ']')
$restrictedCheck.IsChecked = $false
Wait-Dispatcher 400
Log ('settings: after unchecking restricted prompt -> config.skipRestrictedPrompt=' + (Read-ConfigJson).options.skipRestrictedPrompt + ' (expected True)')
$restrictedCheck.IsChecked = $true
Wait-Dispatcher 400
Log ('settings: after re-checking restricted prompt -> config.skipRestrictedPrompt=' + (Read-ConfigJson).options.skipRestrictedPrompt + ' (expected False)')
$matchCheck.IsChecked = $true
Wait-Dispatcher 400
$autoArrangeCheck.IsChecked = $false
Wait-Dispatcher 400
$configSettings = Read-ConfigJson
Log ('settings saved: matchWindowsIconMetrics=' + $configSettings.options.matchWindowsIconMetrics + ' skipRestrictedPrompt=' + $configSettings.options.skipRestrictedPrompt + ' autoArrangeIcons=' + $configSettings.options.autoArrangeIcons)
$settingsHandle = [MultiDeskVerify.Probe]::FindSecondLargestWindow($PID, $layerHandle)
Save-WindowShot $settingsHandle '12-settings-options.png'

$logLines = @()
if (Test-Path $script:appLogPath) { $logLines = [System.IO.File]::ReadAllLines($script:appLogPath, [System.Text.Encoding]::UTF8) }
foreach ($line in $logLines) {
    if ($line -match 'icon-metrics|error|failed|low integrity|switch|launch|desktop icons') { Log ('app.log: ' + $line) }
}
Log ('app.log lines = ' + $logLines.Count)

Register-PostExitVerification (Join-Path $DataDir 'desktop-icons-hidden.marker')
Log 'E2E medium scenario finished; calling Shutdown (Environment.Exit follows)'
$controller.Shutdown()
