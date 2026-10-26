# Render one specific window into a PNG with PrintWindow (works even when the window
# is covered by other windows - important because the MultiDesk desktop layer is
# deliberately kept at the bottom of the Z order).
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\capture-window.ps1 -Path screenshots\01-desktop-work.png -ProcessId 1234 -Pick largest
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\capture-window.ps1 -Path screenshots\05-settings.png -ProcessId 1234 -Pick smallest
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\capture-window.ps1 -Path out.png -Handle 0x123456
#
# ASCII-only source: Windows PowerShell 5.1 misreads BOM-less UTF-8 with non-ASCII text.
param(
    [Parameter(Mandatory = $true)][string]$Path,
    [int]$ProcessId = 0,
    [long]$Handle = 0,
    [ValidateSet('largest', 'smallest')][string]$Pick = 'largest',
    [int]$MinWidth = 200,
    [int]$MinHeight = 120,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

# DPI aware so GetWindowRect returns real device pixels on scaled displays.
try {
    Add-Type -Namespace MultiDeskScripts -Name DpiHelper -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool SetProcessDPIAware();
'@
    [MultiDeskScripts.DpiHelper]::SetProcessDPIAware() | Out-Null
} catch {
}

Add-Type -AssemblyName System.Drawing

try {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace MultiDeskScripts
{
    public class CaptureWindowInfo
    {
        public long Handle;
        public int ProcessId;
        public string Title;
        public string ClassName;
        public int Left;
        public int Top;
        public int Width;
        public int Height;
        public bool Visible;
    }

    public static class WindowCapture
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

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);

        public static List<CaptureWindowInfo> List()
        {
            List<CaptureWindowInfo> result = new List<CaptureWindowInfo>();
            EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                CaptureWindowInfo info = new CaptureWindowInfo();
                info.Handle = hWnd.ToInt64();
                int pid;
                GetWindowThreadProcessId(hWnd, out pid);
                info.ProcessId = pid;
                StringBuilder title = new StringBuilder(512);
                GetWindowText(hWnd, title, title.Capacity);
                info.Title = title.ToString();
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

        public static long GetHandleByPid(int pid, bool largest, int minWidth, int minHeight)
        {
            List<CaptureWindowInfo> list = List();
            long best = 0;
            long bestArea = 0;
            for (int i = 0; i < list.Count; i++)
            {
                CaptureWindowInfo info = list[i];
                if (info.ProcessId != pid) continue;
                if (info.Width < minWidth || info.Height < minHeight) continue;
                long area = (long)info.Width * (long)info.Height;
                if (best == 0) { best = info.Handle; bestArea = area; continue; }
                if (largest && area > bestArea) { best = info.Handle; bestArea = area; }
                if (!largest && area < bestArea) { best = info.Handle; bestArea = area; }
            }
            return best;
        }

        public static bool Render(long handle, IntPtr hdc, uint flags)
        {
            return PrintWindow(new IntPtr(handle), hdc, flags);
        }
    }
}
'@
} catch {
    # Duplicate type when the script is dot-sourced twice.
}

$target = $Path
if (-not [System.IO.Path]::IsPathRooted($target)) {
    $projectRoot = Split-Path -Parent $PSScriptRoot
    $target = Join-Path $projectRoot $target
}
$dir = Split-Path -Parent $target
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }

$windowHandle = $Handle
if ($windowHandle -eq 0) {
    if ($ProcessId -le 0) { throw "Either -Handle or -ProcessId is required" }
    $largest = $Pick -eq 'largest'
    $windowHandle = [MultiDeskScripts.WindowCapture]::GetHandleByPid($ProcessId, $largest, $MinWidth, $MinHeight)
    if ($windowHandle -eq 0) { throw ("No window found for process id " + $ProcessId) }
}

$infos = [MultiDeskScripts.WindowCapture]::List()
$info = $null
foreach ($candidate in $infos) {
    if ($candidate.Handle -eq $windowHandle) { $info = $candidate; break }
}
if ($info -eq $null) { throw ("Window handle not found: 0x" + $windowHandle.ToString('X')) }
if ($info.Width -le 0 -or $info.Height -le 0) { throw "Window has an empty size" }

$bitmap = New-Object System.Drawing.Bitmap($info.Width, $info.Height)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$rendered = $false
try {
    $hdc = $graphics.GetHdc()
    try {
        # 2 = PW_RENDERFULLCONTENT (captures DirectComposition / WPF content).
        $rendered = [MultiDeskScripts.WindowCapture]::Render($windowHandle, $hdc, 2)
    } finally {
        $graphics.ReleaseHdc($hdc)
    }
} finally {
    $graphics.Dispose()
}
try {
    $bitmap.Save($target, [System.Drawing.Imaging.ImageFormat]::Png)
} finally {
    $bitmap.Dispose()
}

if (-not $Quiet) {
    $title = $info.Title
    if ($title.Length -gt 60) { $title = $title.Substring(0, 60) + '...' }
    Write-Host ("[capture-window] " + $target + " (hwnd=0x" + $windowHandle.ToString('X') + " pid=" + $info.ProcessId +
        " " + $info.Width + "x" + $info.Height + " rendered=" + $rendered + " title=" + $title + ")")
}
if (-not $rendered) { exit 2 }
Write-Output $target
