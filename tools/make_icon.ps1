# Draws the Cube Radar icon (dark disc, blue rings, blue dot = you, green cube) in several sizes and packs them into
# assets\cuberadar.ico (PNG-compressed ICO entries, Windows Vista+). Run with Windows PowerShell 5.1.
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root "assets\cuberadar.ico"
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

function Draw-Icon([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $blue = [System.Drawing.Color]::FromArgb(255, 77, 163, 255)
    $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 14, 18, 23))
    $g.FillEllipse($bg, 0, 0, $s - 1, $s - 1)
    $w = [Math]::Max(1.3, $s / 15)
    $pen = New-Object System.Drawing.Pen $blue, $w
    $m = $s * 0.07
    $g.DrawEllipse($pen, $m, $m, $s - 1 - 2 * $m, $s - 1 - 2 * $m)
    if ($s -ge 24) {
        $i = $s * 0.30
        $thin = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(150, 77, 163, 255)), ([Math]::Max(1, $w * 0.7))
        $g.DrawEllipse($thin, $i, $i, $s - 1 - 2 * $i, $s - 1 - 2 * $i)
    }
    $c = ($s - 1) / 2
    $d = [Math]::Max(2, $s * 0.14)
    $me = New-Object System.Drawing.SolidBrush $blue
    $g.FillEllipse($me, $c - $d / 2, $c - $d / 2, $d, $d)
    $q = [Math]::Max(3, $s * 0.24)
    $cube = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 61, 220, 132))
    $g.FillRectangle($cube, $s * 0.58, $s * 0.17, $q, $q)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$images = @($sizes | ForEach-Object { , (Draw-Icon $_) })
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($k = 0; $k -lt $sizes.Count; $k++) {
    $s = $sizes[$k]; $len = $images[$k].Length
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$len); $bw.Write([UInt32]$offset)
    $offset += $len
}
foreach ($img in $images) { $bw.Write($img) }
$bw.Close()
"{0} ({1:N0} bytes, {2} sizes)" -f $out, (Get-Item $out).Length, $sizes.Count
