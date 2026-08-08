<#
.SYNOPSIS
  Generates pCUE's application icon (pCUE\small.ico) as a multi-resolution ICO.

.DESCRIPTION
  The icon is a fan rotor in the Cybenetics green over a dark disc. It is drawn
  procedurally so it can be re-generated or tweaked, rather than living in the repo only as
  an opaque binary.

  Sizes: 16, 20, 24, 32, 40, 48, 64, 128, 256. Each frame is stored as PNG inside the ICO
  (supported by Windows Vista and later), which keeps the file small and the edges clean.

  Detail is deliberately reduced at small sizes: below 32 px the hub ring and blade
  highlights are dropped, because at 16 px they turn into mud and the silhouette is all that
  survives.

.EXAMPLE
  pwsh tools\make-icon.ps1
  pwsh tools\make-icon.ps1 -OutFile pCUE\small.ico -Blades 7
#>
[CmdletBinding()]
param(
    [string]$OutFile = "$PSScriptRoot\..\pCUE\small.ico",
    [int]$Blades = 5,
    [switch]$AlsoPng
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

function New-FanBitmap {
    param([int]$S)

    $bmp = New-Object System.Drawing.Bitmap($S, $S, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = 'AntiAlias'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode   = 'HighQuality'

    $detailed = $S -ge 32
    $c = $S / 2.0
    $r = $S * 0.48

    # --- dark disc, lit slightly from the top-left so it does not look flat ---
    $discRect = New-Object System.Drawing.RectangleF(($c - $r), ($c - $r), (2 * $r), (2 * $r))
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddEllipse($discRect)
    $grad = New-Object System.Drawing.Drawing2D.PathGradientBrush($path)
    $grad.CenterPoint    = New-Object System.Drawing.PointF(($c - $r * 0.35), ($c - $r * 0.35))
    $grad.CenterColor    = [System.Drawing.Color]::FromArgb(255, 24, 64, 42)
    $grad.SurroundColors = @([System.Drawing.Color]::FromArgb(255, 6, 14, 10))
    $g.FillEllipse($grad, $discRect)
    $grad.Dispose(); $path.Dispose()

    # --- blades ---
    # Each blade is a swept wedge: an outer arc, pulled back to the hub along a curve. The
    # sweep gives the rotor a direction of rotation instead of a static pinwheel.
    $bladeOuter = $r * 0.88
    $bladeInner = $r * 0.20
    $sweep = 360.0 / $Blades

    # Each blade is swept by sampling along its span rather than by fitting beziers to corner
    # points: the centreline rotates as the radius grows (the sweep), and the half-width grows
    # from nothing at the hub to its maximum near the tip. Sampling both edges keeps the two
    # sides parallel around the curve, which is what makes it read as a rotor blade instead of
    # a flower petal.
    $steps = 18
    $leanRad = $sweep * 0.85 * [Math]::PI / 180      # how far the tip trails the root
    $maxHalfW = $sweep * 0.30 * [Math]::PI / 180     # widest half-angle, near the tip

    for ($i = 0; $i -lt $Blades; $i++) {
        $a0 = [Math]::PI * 2 * $i / $Blades
        $bp = New-Object System.Drawing.Drawing2D.GraphicsPath

        $lead = New-Object 'System.Collections.Generic.List[System.Drawing.PointF]'
        $trail = New-Object 'System.Collections.Generic.List[System.Drawing.PointF]'

        for ($k = 0; $k -le $steps; $k++) {
            $t = $k / [double]$steps
            $rr = $bladeInner + ($bladeOuter - $bladeInner) * $t
            # ease the sweep so most of the lean happens outboard, like a real swept blade
            $th = $a0 + $leanRad * ($t * $t)
            # width: zero at the root, peaking just before the tip, slightly rounded off at it
            $hw = $maxHalfW * [Math]::Sin([Math]::PI * [Math]::Min(1.0, $t * 1.12) * 0.62)

            $lead.Add((New-Object System.Drawing.PointF(($c + $rr * [Math]::Cos($th - $hw)), ($c + $rr * [Math]::Sin($th - $hw)))))
            $trail.Add((New-Object System.Drawing.PointF(($c + $rr * [Math]::Cos($th + $hw)), ($c + $rr * [Math]::Sin($th + $hw)))))
        }

        $trail.Reverse()
        $poly = New-Object 'System.Collections.Generic.List[System.Drawing.PointF]'
        $poly.AddRange($lead); $poly.AddRange($trail)
        $bp.AddPolygon($poly.ToArray())
        $bp.CloseFigure()

        $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            (New-Object System.Drawing.PointF(($c - $r), ($c - $r))),
            (New-Object System.Drawing.PointF(($c + $r), ($c + $r))),
            [System.Drawing.Color]::FromArgb(255, 96, 255, 170),
            [System.Drawing.Color]::FromArgb(255, 26, 168, 92))
        $g.FillPath($brush, $bp)
        $brush.Dispose(); $bp.Dispose()
    }

    # --- hub ---
    $hubR = $r * ((&{ if ($detailed) { 0.26 } else { 0.30 } }))
    $hubRect = New-Object System.Drawing.RectangleF(($c - $hubR), ($c - $hubR), (2 * $hubR), (2 * $hubR))
    $hub = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 10, 26, 18))
    $g.FillEllipse($hub, $hubRect); $hub.Dispose()

    if ($detailed) {
        $ringPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 120, 255, 185), [single]([Math]::Max(1, $S / 64)))
        $g.DrawEllipse($ringPen, $hubRect)
        # outer rim, so the disc reads as a fan housing rather than a plain circle
        $rimPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(190, 60, 220, 130), [single]([Math]::Max(1, $S / 48)))
        $g.DrawEllipse($rimPen, $discRect)
        $ringPen.Dispose(); $rimPen.Dispose()
    }

    $g.Dispose()
    return $bmp
}

# --- render every size, then pack them into one ICO -------------------------------------
$frames = @{}
foreach ($s in $sizes) {
    $bmp = New-FanBitmap -S $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames[$s] = $ms.ToArray()
    if ($AlsoPng) { $bmp.Save((Join-Path (Split-Path $OutFile -Parent) "pcue-$s.png"), [System.Drawing.Imaging.ImageFormat]::Png) }
    $ms.Dispose(); $bmp.Dispose()
}

# ICO container: 6-byte header, then one 16-byte directory entry per image, then the data.
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($out)
$bw.Write([UInt16]0)                 # reserved
$bw.Write([UInt16]1)                 # type 1 = icon
$bw.Write([UInt16]$sizes.Count)

$offset = 6 + (16 * $sizes.Count)
foreach ($s in $sizes) {
    $data = $frames[$s]
    # 256 is stored as 0 in the single width/height byte
    $bw.Write([Byte]$(if ($s -ge 256) { 0 } else { $s }))
    $bw.Write([Byte]$(if ($s -ge 256) { 0 } else { $s }))
    $bw.Write([Byte]0)               # palette count
    $bw.Write([Byte]0)               # reserved
    $bw.Write([UInt16]1)             # colour planes
    $bw.Write([UInt16]32)            # bits per pixel
    $bw.Write([UInt32]$data.Length)
    $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($s in $sizes) { $bw.Write($frames[$s]) }
$bw.Flush()

$full = [System.IO.Path]::GetFullPath($OutFile)
[System.IO.File]::WriteAllBytes($full, $out.ToArray())
$bw.Dispose(); $out.Dispose()

Write-Host ("Wrote {0} ({1:N0} bytes, {2} sizes: {3})" -f $full, (Get-Item $full).Length, $sizes.Count, ($sizes -join ', '))
