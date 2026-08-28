# Export largest PNG frame from app.ico for visual check.
Add-Type -AssemblyName System.Drawing

$root = Split-Path $PSScriptRoot -Parent
$icoPath = Join-Path $root 'app.ico'
$bytes = [System.IO.File]::ReadAllBytes($icoPath)
$count = [BitConverter]::ToUInt16($bytes, 4)
$best = $null
$bestSize = 0
for ($i = 0; $i -lt $count; $i++) {
    $base = 6 + ($i * 16)
    $w = $bytes[$base]
    $h = $bytes[$base + 1]
    $size = if ($w -eq 0) { 256 } else { $w }
    if ($size -gt $bestSize) { $bestSize = $size; $best = $base }
}
$offset = [BitConverter]::ToUInt32($bytes, $best + 12)
$length = [BitConverter]::ToUInt32($bytes, $best + 8)
$png = New-Object byte[] $length
[Array]::Copy($bytes, $offset, $png, 0, $length)
$ms = New-Object System.IO.MemoryStream(,$png)
$bmp = [System.Drawing.Bitmap]::FromStream($ms)
$bmp.Save((Join-Path $PSScriptRoot 'ico-preview.png'), [System.Drawing.Imaging.ImageFormat]::Png)
Write-Host "Preview $($bmp.Width)x$($bmp.Height)"
$bmp.Dispose()
$ms.Dispose()
