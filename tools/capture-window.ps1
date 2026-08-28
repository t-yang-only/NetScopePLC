# Capture a top-level window by title substring to PNG.
param(
    [string]$TitleContains = 'NetScope PLC',
    [string]$Output = (Join-Path (Split-Path $PSScriptRoot -Parent) 'docs\screenshot.png'),
    [int]$WaitSeconds = 15
)

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public struct WinRect { public int Left, Top, Right, Bottom; }
public static class WindowCapture {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out WinRect lpRect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, int nFlags);
}
"@

function Find-Window([string]$needle) {
    $deadline = (Get-Date).AddSeconds($WaitSeconds)
    while ((Get-Date) -lt $deadline) {
        $proc = Get-Process | Where-Object {
            $_.MainWindowHandle -ne [IntPtr]::Zero -and $_.MainWindowTitle -like "*$needle*"
        } | Select-Object -First 1
        if ($proc) { return $proc.MainWindowHandle }
        Start-Sleep -Milliseconds 400
    }
    throw "Window not found: *$needle*"
}

$hwnd = Find-Window $TitleContains
$rect = New-Object WinRect
[void][WindowCapture]::GetWindowRect($hwnd, [ref]$rect)
$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top
if ($width -le 0 -or $height -le 0) { throw "Invalid window size $width x $height" }

$bmp = New-Object System.Drawing.Bitmap $width, $height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
[void][WindowCapture]::PrintWindow($hwnd, $hdc, 2)
$g.ReleaseHdc($hdc)
$g.Dispose()

$dir = Split-Path $Output -Parent
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
$bmp.Save($Output, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host "Saved $Output ($width x $height)"
