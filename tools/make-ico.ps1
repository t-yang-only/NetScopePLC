# Build multi-size app.ico with rounded corners and transparent background.
Add-Type -AssemblyName System.Drawing

$root = Split-Path $PSScriptRoot -Parent
$pngPath = Join-Path $root 'app-icon.png'
$icoPath = Join-Path $root 'app.ico'

function New-RoundedBitmap([System.Drawing.Bitmap]$source, [int]$size, [int]$radiusPercent = 22) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $radius = [Math]::Max(2, [int]($size * $radiusPercent / 100.0))
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($size - $d, 0, $d, $d, 270, 90)
    $path.AddArc($size - $d, $size - $d, $d, $d, 0, 90)
    $path.AddArc(0, $size - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $g.SetClip($path)
    $g.DrawImage($source, 0, 0, $size, $size)
    $g.Dispose()
    return $bmp
}

$src = [System.Drawing.Bitmap]::new($pngPath)
$flat = New-Object System.Drawing.Bitmap $src.Width, $src.Height, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
for ($y = 0; $y -lt $src.Height; $y++) {
    for ($x = 0; $x -lt $src.Width; $x++) {
        $c = $src.GetPixel($x, $y)
        if ($c.A -lt 16 -or ($c.R -lt 24 -and $c.G -lt 24 -and $c.B -lt 24)) {
            $flat.SetPixel($x, $y, [System.Drawing.Color]::FromArgb(0, 0, 0, 0))
        } else {
            $flat.SetPixel($x, $y, $c)
        }
    }
}
$src.Dispose()

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngStreams = New-Object System.Collections.Generic.List[byte[]]
foreach ($size in $sizes) {
    $bmp = New-RoundedBitmap $flat $size
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngStreams.Add($ms.ToArray())
    $ms.Dispose()
    $bmp.Dispose()
}
$flat.Dispose()

$count = $sizes.Count
$offset = 6 + (16 * $count)
$entries = @()
for ($i = 0; $i -lt $count; $i++) {
    $size = $sizes[$i]
    $data = $pngStreams[$i]
    $w = if ($size -ge 256) { 0 } else { $size }
    $h = if ($size -ge 256) { 0 } else { $size }
    $entries += [PSCustomObject]@{ W = $w; H = $h; Offset = $offset; Data = $data }
    $offset += $data.Length
}

$fs = [System.IO.File]::Open($icoPath, [System.IO.FileMode]::Create)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0)
$bw.Write([uint16]1)
$bw.Write([uint16]$count)
foreach ($e in $entries) {
    $bw.Write([byte]$e.W)
    $bw.Write([byte]$e.H)
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([uint16]1)
    $bw.Write([uint16]32)
    $bw.Write([uint32]$e.Data.Length)
    $bw.Write([uint32]$e.Offset)
}
foreach ($e in $entries) { $bw.Write($e.Data) }
$bw.Flush()
$bw.Dispose()
$fs.Dispose()

# Validate
$icon = [System.Drawing.Icon]::new($icoPath)
$icon.Dispose()
Write-Host "Wrote $icoPath ($((Get-Item $icoPath).Length) bytes)"
