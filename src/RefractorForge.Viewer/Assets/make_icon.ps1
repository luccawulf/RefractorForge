# Regenerates RefractorForge.ico (+ a 256px preview PNG) from code — no external art assets.
# A sunlit terrain ridge on a dark "Battlecraft" chip: terrain + sun = the editor's core (terrain + sun-shadow bake).
# Run:  powershell -ExecutionPolicy Bypass -File make_icon.ps1
Add-Type -AssemblyName System.Drawing

function New-RoundRect([single]$x,[single]$y,[single]$w,[single]$h,[single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x,       $y,       $d, $d, 180, 90)
    $p.AddArc($x+$w-$d, $y,       $d, $d, 270, 90)
    $p.AddArc($x+$w-$d, $y+$h-$d, $d, $d,   0, 90)
    $p.AddArc($x,       $y+$h-$d, $d, $d,  90, 90)
    $p.CloseFigure()
    return $p
}
function C([int]$a,[int]$r,[int]$g,[int]$b) { return [System.Drawing.Color]::FromArgb($a,$r,$g,$b) }
function P([single]$x,[single]$y) { return New-Object System.Drawing.PointF($x,$y) }

# Draw the whole mark in a 256x256 coordinate space; the caller scales for each icon size.
function Draw-Mark([System.Drawing.Graphics]$g) {
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::Half

    # dark rounded chip, top-lit vertical gradient
    $bg = New-RoundRect 8 8 240 240 46
    $bgRect = New-Object System.Drawing.RectangleF(8, 8, 240, 240)
    $bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($bgRect, (C 255 44 51 62), (C 255 16 19 24), [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
    $g.FillPath($bgBrush, $bg)

    # sun glow + disc, upper-right (the ridge overlaps its lower shoulder)
    $scx = 188.0; $scy = 70.0; $sr = 30.0
    $glowPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $glowPath.AddEllipse(($scx-$sr*1.9), ($scy-$sr*1.9), ($sr*3.8), ($sr*3.8))
    $glow = New-Object System.Drawing.Drawing2D.PathGradientBrush($glowPath)
    $glow.CenterColor = (C 120 255 196 92)
    $glow.SurroundColors = @((C 0 255 196 92))
    $g.FillPath($glow, $glowPath)

    $sunPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $sunPath.AddEllipse(($scx-$sr), ($scy-$sr), ($sr*2), ($sr*2))
    $sun = New-Object System.Drawing.Drawing2D.PathGradientBrush($sunPath)
    $sun.CenterColor = (C 255 255 241 206)
    $sun.SurroundColors = @((C 255 255 176 71))
    $g.FillPath($sun, $sunPath)

    # terrain ridge (hero), cyan -> deep blue vertical gradient
    $ridge = [System.Drawing.PointF[]]@((P 40 190),(P 96 118),(P 128 146),(P 166 78),(P 216 190))
    $ridgeRect = New-Object System.Drawing.RectangleF(40, 78, 176, 114)
    $ridgeBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($ridgeRect, (C 255 98 205 251), (C 255 20 104 155), [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
    $g.FillPolygon($ridgeBrush, $ridge)

    # snow cap + sunlit ridge highlight on the main peak
    $cap = [System.Drawing.PointF[]]@((P 166 78),(P 150 100),(P 166 108),(P 182 96))
    $g.FillPolygon((New-Object System.Drawing.SolidBrush((C 255 228 244 255))), $cap)
    $hi = New-Object System.Drawing.Pen((C 220 190 232 255), 5)
    $hi.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $hi.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLines($hi, [System.Drawing.PointF[]]@((P 166 80),(P 200 184)))

    # small secondary cap
    $cap2 = [System.Drawing.PointF[]]@((P 96 118),(P 86 132),(P 98 136),(P 106 128))
    $g.FillPolygon((New-Object System.Drawing.SolidBrush((C 235 210 236 252))), $cap2)

    # faint baseline glow (the "ground")
    $basePen = New-Object System.Drawing.Pen((C 70 98 205 251), 3)
    $g.DrawLine($basePen, 44, 190, 212, 190)

    # inner bevel stroke to frame the chip
    $bevel = New-RoundRect 10 10 236 236 44
    $bevelPen = New-Object System.Drawing.Pen((C 130 64 75 92), 2)
    $g.DrawPath($bevelPen, $bevel)
}

function Render-Bitmap([int]$S) {
    $bmp = New-Object System.Drawing.Bitmap($S, $S, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.ScaleTransform(($S/256.0), ($S/256.0))
    Draw-Mark $g
    $g.Dispose()
    return $bmp
}

function Get-Png([System.Drawing.Bitmap]$bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return ,$ms.ToArray()
}

# A 32bpp BGRA DIB icon entry (BITMAPINFOHEADER + bottom-up colour rows + a 1bpp AND mask derived from alpha).
# Small sizes MUST be DIB, not PNG: GDI/older shells render PNG-in-ICO entries as garbage below 256px.
function Get-Dib([System.Drawing.Bitmap]$bmp) {
    $S = $bmp.Width
    $rect = New-Object System.Drawing.Rectangle(0, 0, $S, $S)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $data.Stride
    $buf = New-Object byte[] ($stride * $S)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $buf, 0, $buf.Length)
    $bmp.UnlockBits($data)

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([UInt32]40); $bw.Write([Int32]$S); $bw.Write([Int32]($S*2))   # biSize, biWidth, biHeight(=2x: colour+mask)
    $bw.Write([UInt16]1);  $bw.Write([UInt16]32)                            # biPlanes, biBitCount
    $bw.Write([UInt32]0);  $bw.Write([UInt32]($S*$S*4))                     # BI_RGB, biSizeImage
    $bw.Write([Int32]0); $bw.Write([Int32]0); $bw.Write([UInt32]0); $bw.Write([UInt32]0)
    for ($y = $S-1; $y -ge 0; $y--) { $bw.Write($buf, $y*$stride, $stride) }  # colour, bottom-up

    $maskStride = [int]([Math]::Floor(($S + 31) / 32) * 4)                  # 1bpp rows padded to 4 bytes
    for ($y = $S-1; $y -ge 0; $y--) {
        $row = New-Object byte[] $maskStride
        for ($x = 0; $x -lt $S; $x++) {
            $a = $buf[$y*$stride + $x*4 + 3]
            if ($a -lt 128) { $bi = ($x -shr 3); $row[$bi] = $row[$bi] -bor (0x80 -shr ($x -band 7)) }   # 1 = transparent
        }
        $bw.Write($row, 0, $maskStride)
    }
    $bw.Flush()
    return ,$ms.ToArray()
}

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$sizes = @(16,24,32,48,64,128,256)
$blobs = @{}; $preview = $null
foreach ($s in $sizes) {
    $bmp = Render-Bitmap $s
    if ($s -ge 256) { $blobs[$s] = Get-Png $bmp; $preview = Get-Png $bmp } else { $blobs[$s] = Get-Dib $bmp }
    $bmp.Dispose()
}

$icoPath = Join-Path $here 'RefractorForge.ico'
$fs = New-Object System.IO.FileStream($icoPath, [System.IO.FileMode]::Create)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)   # ICONDIR
$offset = 6 + 16 * $sizes.Count
foreach ($s in $sizes) {
    $dim = if ($s -ge 256) { 0 } else { $s }                   # 256 encodes as 0
    $bw.Write([Byte]$dim); $bw.Write([Byte]$dim)               # width, height
    $bw.Write([Byte]0); $bw.Write([Byte]0)                     # colorCount, reserved
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)                # planes, bitCount
    $bw.Write([UInt32]$blobs[$s].Length); $bw.Write([UInt32]$offset)
    $offset += $blobs[$s].Length
}
foreach ($s in $sizes) { $bw.Write($blobs[$s]) }
$bw.Flush(); $bw.Close(); $fs.Close()

[System.IO.File]::WriteAllBytes((Join-Path $here 'RefractorForge_icon_preview.png'), $preview)
Write-Output ("ICON-OK {0} sizes -> {1} bytes" -f $sizes.Count, (Get-Item $icoPath).Length)
