# Simulate the MultiDesk hotkeys / a mouse right click for UI verification.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\send-keys.ps1 -Hotkey Ctrl+Alt+2
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\send-keys.ps1 -Hotkey Ctrl+Alt+Left
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\send-keys.ps1 -RightClick -X 900 -Y 500
#
# Supported tokens: CTRL, ALT, SHIFT, digits 1-9, A-Z, LEFT, RIGHT, UP, DOWN, ESC, ENTER.
# ASCII-only source: Windows PowerShell 5.1 misreads BOM-less UTF-8 with non-ASCII text.
param(
    [string]$Hotkey = 'Ctrl+Alt+2',
    [switch]$RightClick,
    [int]$X = -1,
    [int]$Y = -1,
    [int]$DelayMs = 150
)

$ErrorActionPreference = 'Stop'

try {
    Add-Type -Namespace MultiDeskScripts -Name Keys -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, System.UIntPtr dwExtraInfo);
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool SetCursorPos(int X, int Y);
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, System.UIntPtr dwExtraInfo);
'@
} catch {
}

$KEYUP = 0x0002
$MOUSE_RIGHTDOWN = 0x0008
$MOUSE_RIGHTUP = 0x0010
$ZERO = [System.UIntPtr]::Zero

function Get-VirtualKey([string]$token) {
    $t = $token.Trim().ToUpperInvariant()
    switch ($t) {
        'CTRL' { return 0x11 }
        'CONTROL' { return 0x11 }
        'ALT' { return 0x12 }
        'MENU' { return 0x12 }
        'SHIFT' { return 0x10 }
        'LEFT' { return 0x25 }
        'RIGHT' { return 0x27 }
        'UP' { return 0x26 }
        'DOWN' { return 0x28 }
        'ESC' { return 0x1B }
        'ESCAPE' { return 0x1B }
        'ENTER' { return 0x0D }
        default {
            if ($t.Length -eq 1 -and $t -ge '0' -and $t -le '9') {
                return 0x30 + ([int]$t[0] - [int][char]'0')
            }
            if ($t.Length -eq 1 -and [char]::IsLetter($t[0])) {
                return [int][char]::ToUpperInvariant($t[0])
            }
            throw "Unknown key token: $token"
        }
    }
}

if ($RightClick) {
    if ($X -ge 0 -and $Y -ge 0) {
        [MultiDeskScripts.Keys]::SetCursorPos($X, $Y) | Out-Null
    }
    Start-Sleep -Milliseconds $DelayMs
    [MultiDeskScripts.Keys]::mouse_event($MOUSE_RIGHTDOWN, 0, 0, 0, $ZERO)
    Start-Sleep -Milliseconds 80
    [MultiDeskScripts.Keys]::mouse_event($MOUSE_RIGHTUP, 0, 0, 0, $ZERO)
    Write-Host ("[keys] right click at " + $X + "," + $Y)
    exit 0
}

$tokens = $Hotkey -split '\+'
$virtualKeys = @()
foreach ($token in $tokens) {
    if ([string]::IsNullOrWhiteSpace($token)) { continue }
    $virtualKeys += (Get-VirtualKey $token)
}
if ($virtualKeys.Count -eq 0) {
    throw "Empty hotkey specification"
}

foreach ($vk in $virtualKeys) {
    [MultiDeskScripts.Keys]::keybd_event([byte]$vk, 0, 0, $ZERO)
    Start-Sleep -Milliseconds 25
}
Start-Sleep -Milliseconds $DelayMs
for ($i = $virtualKeys.Count - 1; $i -ge 0; $i--) {
    [MultiDeskScripts.Keys]::keybd_event([byte]$virtualKeys[$i], 0, $KEYUP, $ZERO)
    Start-Sleep -Milliseconds 25
}
Write-Host ("[keys] sent " + $Hotkey)
