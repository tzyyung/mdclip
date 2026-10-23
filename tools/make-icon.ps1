# Draws the MdClip icon (a rounded tile with the Markdown "M↓" mark) and writes MdClip\app.ico.
# Sizes up to 64 px are stored as 32-bit bitmaps, 256 px as PNG, which is what Windows expects.
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot '..\MdClip\app.ico'

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    # tile: rounded square, deep blue to teal
    $pad = [Math]::Max(0.5, $size * 0.03)
    $r = $size * 0.22
    $w = $size - 2 * $pad
    $tile = New-Object System.Drawing.Drawing2D.GraphicsPath
    $tile.AddArc($pad, $pad, 2 * $r, 2 * $r, 180, 90)
    $tile.AddArc($pad + $w - 2 * $r, $pad, 2 * $r, 2 * $r, 270, 90)
    $tile.AddArc($pad + $w - 2 * $r, $pad + $w - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $tile.AddArc($pad, $pad + $w - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $tile.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $size, $size), ([System.Drawing.Color]::FromArgb(255, 0x1c, 0x5b, 0xb8)), ([System.Drawing.Color]::FromArgb(255, 0x0f, 0x8a, 0x80))
    $g.FillPath($brush, $tile)

    # Markdown mark, from its 208 x 128 design grid, scaled to 76% of the tile width (more on tiny sizes)
    $fill = if ($size -le 24) { 0.92 } elseif ($size -le 48) { 0.82 } else { 0.76 }
    $scale = $size * $fill / 208
    $ox = ($size - 208 * $scale) / 2
    $oy = ($size - 128 * $scale) / 2 + $size * 0.01
    $P = { param($x, $y) New-Object System.Drawing.PointF ($ox + $x * $scale), ($oy + $y * $scale) }
    $m = New-Object System.Drawing.Drawing2D.GraphicsPath
    $m.AddPolygon([System.Drawing.PointF[]]@(
        (& $P 30 98), (& $P 30 30), (& $P 50 30), (& $P 70 55), (& $P 90 30), (& $P 110 30),
        (& $P 110 98), (& $P 90 98), (& $P 90 59), (& $P 70 84), (& $P 50 59), (& $P 50 98)))
    $m.AddPolygon([System.Drawing.PointF[]]@(
        (& $P 155 98), (& $P 125 65), (& $P 145 65), (& $P 145 30), (& $P 165 30), (& $P 165 65), (& $P 185 65)))
    $g.FillPath([System.Drawing.Brushes]::White, $m)
    $g.Dispose()
    return $bmp
}

# 32-bit DIB as stored inside .ico: bottom-up BGRA rows, height doubled, then an all-zero AND mask.
function Get-DibBytes($bmp) {
    $size = $bmp.Width
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms
    $bw.Write([int]40); $bw.Write([int]$size); $bw.Write([int]($size * 2))
    $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int]0); $bw.Write([int]($size * $size * 4))
    $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    for ($y = $size - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $size; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
        }
    }
    $maskRow = [int]([Math]::Ceiling($size / 32.0) * 4)
    $bw.Write((New-Object byte[] ($maskRow * $size)))
    $bw.Flush()
    return $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    if ($s -eq 256) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        , $ms.ToArray()
    } else {
        , (Get-DibBytes $bmp)
    }
    $bmp.Dispose()
}

$fs = [System.IO.File]::Create((Resolve-Path (Split-Path $out)).Path + '\app.ico')
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([int16]0); $bw.Write([int16]1); $bw.Write([int16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([int16]1); $bw.Write([int16]32)
    $bw.Write([int]$images[$i].Length); $bw.Write([int]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $bw.Write([byte[]]$img) }
$bw.Close()
Write-Host "Wrote $out"
