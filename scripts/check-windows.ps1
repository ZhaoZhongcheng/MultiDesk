# Enumerate top-level windows (title / pid / process / class) for MultiDesk verification.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\check-windows.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\check-windows.ps1 -ProcessFilter MultiDesk
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\check-windows.ps1 -ProcessFilter MultiDesk -CountOnly
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\check-windows.ps1 -All -Json
#
# ASCII-only source: Windows PowerShell 5.1 misreads BOM-less UTF-8 with non-ASCII text.
param(
    [string]$TitleFilter = '',
    [string]$ProcessFilter = '',
    [int]$ProcessId = 0,
    [switch]$All,
    [switch]$Json,
    [switch]$CountOnly,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

try {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace MultiDeskScripts
{
    public class WindowEntry
    {
        public long Handle;
        public int ProcessId;
        public string Title;
        public string ClassName;
        public string ProcessName;
        public bool Visible;
        public bool Minimized;
    }

    public static class WindowEnumerator
    {
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        public static List<WindowEntry> List()
        {
            List<WindowEntry> result = new List<WindowEntry>();
            EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                WindowEntry entry = new WindowEntry();
                entry.Handle = hWnd.ToInt64();
                int pid;
                GetWindowThreadProcessId(hWnd, out pid);
                entry.ProcessId = pid;
                StringBuilder title = new StringBuilder(512);
                GetWindowText(hWnd, title, title.Capacity);
                entry.Title = title.ToString();
                StringBuilder cls = new StringBuilder(256);
                GetClassName(hWnd, cls, cls.Capacity);
                entry.ClassName = cls.ToString();
                entry.Visible = IsWindowVisible(hWnd);
                entry.Minimized = IsIconic(hWnd);
                result.Add(entry);
                return true;
            }, IntPtr.Zero);
            return result;
        }
    }
}
'@
} catch {
    # Add-Type reports a duplicate-type error when the script is dot-sourced twice.
}

$entries = [MultiDeskScripts.WindowEnumerator]::List()
$filtered = @()
foreach ($entry in $entries) {
    if (-not $All) {
        if (-not $entry.Visible) { continue }
        if ([string]::IsNullOrEmpty($entry.Title)) { continue }
    }
    $processName = ''
    try {
        $processName = (Get-Process -Id $entry.ProcessId -ErrorAction Stop).ProcessName
    } catch {
        $processName = ''
    }
    $entry.ProcessName = $processName
    if ($ProcessId -gt 0 -and $entry.ProcessId -ne $ProcessId) { continue }
    if ($ProcessFilter) {
        if ($processName -notlike $ProcessFilter -and $processName -notmatch $ProcessFilter) { continue }
    }
    if ($TitleFilter -and $entry.Title -notmatch $TitleFilter) { continue }
    $filtered += $entry
}

if ($CountOnly) {
    Write-Output $filtered.Count
    exit 0
}

if ($Json) {
    $objects = @()
    foreach ($entry in $filtered) {
        $objects += [pscustomobject]@{
            handle    = '0x' + $entry.Handle.ToString('X')
            pid       = $entry.ProcessId
            process   = $entry.ProcessName
            title     = $entry.Title
            class     = $entry.ClassName
            visible   = $entry.Visible
            minimized = $entry.Minimized
        }
    }
    $objects | ConvertTo-Json -Depth 3
    exit 0
}

foreach ($entry in $filtered) {
    $title = $entry.Title
    if ($title.Length -gt 80) { $title = $title.Substring(0, 80) + '...' }
    Write-Host (("{0,10} {1,7} {2,-20} {3}" -f ('0x' + $entry.Handle.ToString('X')), $entry.ProcessId, $entry.ProcessName, $title))
}
if (-not $Quiet) {
    Write-Host ("[windows] " + $filtered.Count + " window(s)")
}
