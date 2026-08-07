# ============================================================
# SafeGitPublisher application icon one-shot generator
# (System.Drawing only, no third-party dependency)
# Output: assets\SafeGitPublisher.ico (16/24/32/48/64/128/256)
#         assets\SafeGitPublisher-source.png (256 design source)
# Design: blue rounded square + white shield (Git safety) +
#         inner Git branch nodes + upward publish arrow
# ============================================================
param(
    [string]$OutputDir = (Join-Path $PSScriptRoot "..\assets")
)

Add-Type -AssemblyName System.Drawing

function New-IconImage([int]$size) {
    $bmp = [System.Drawing.Bitmap]::new($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = [float]$size
    $blue = [System.Drawing.Color]::FromArgb(255, 21, 101, 192)   # #1565C0 accent blue
    $white = [System.Drawing.Color]::White
    $blueBrush = [System.Drawing.SolidBrush]::new($blue)
    $whiteBrush = [System.Drawing.SolidBrush]::new($white)

    # ---- rounded square background ----
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $r = $s * 0.18
    $rect = [System.Drawing.RectangleF]::new(0, 0, $s, $s)
    $path.AddArc($rect.X, $rect.Y, 2*$r, 2*$r, 180, 90)
    $path.AddArc($rect.X + $rect.Width - 2*$r, $rect.Y, 2*$r, 2*$r, 270, 90)
    $path.AddArc($rect.X + $rect.Width - 2*$r, $rect.Y + $rect.Height - 2*$r, 2*$r, 2*$r, 0, 90)
    $path.AddArc($rect.X, $rect.Y + $rect.Height - 2*$r, 2*$r, 2*$r, 90, 90)
    $path.CloseFigure()
    $g.FillPath($blueBrush, $path)

    # ---- white shield (left) ----
    $shieldW = [Math]::Max(1.2, $s * 0.045)
    $shieldPen = [System.Drawing.Pen]::new($white, $shieldW)
    $pts = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new($s*0.16, $s*0.34),
        [System.Drawing.PointF]::new($s*0.40, $s*0.20),
        [System.Drawing.PointF]::new($s*0.64, $s*0.34),
        [System.Drawing.PointF]::new($s*0.64, $s*0.58),
        [System.Drawing.PointF]::new($s*0.40, $s*0.80),
        [System.Drawing.PointF]::new($s*0.16, $s*0.58)
    )
    $g.DrawLines($shieldPen, $pts)

    # ---- Git branch nodes (inside shield, blue) ----
    $dotPen = [System.Drawing.Pen]::new($blue, [Math]::Max(1.2, $s * 0.07))
    $dotPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $dotPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $d1 = [System.Drawing.PointF]::new($s*0.30, $s*0.44)
    $d2 = [System.Drawing.PointF]::new($s*0.50, $s*0.56)
    $dotR = $s * 0.055
    $g.DrawEllipse($dotPen, $d1.X - $dotR, $d1.Y - $dotR, 2*$dotR, 2*$dotR)
    $g.DrawEllipse($dotPen, $d2.X - $dotR, $d2.Y - $dotR, 2*$dotR, 2*$dotR)
    $g.DrawLine($dotPen, $d1, $d2)

    # ---- upward publish arrow (right) ----
    $arrowW = [Math]::Max(1.2, $s * 0.06)
    $arrowPen = [System.Drawing.Pen]::new($white, $arrowW)
    $arrowPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $arrowPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $ax = $s * 0.84
    $ay = $s * 0.62
    $g.DrawLine($arrowPen, $ax, $ay, $ax, $ay - $s*0.30)
    $g.DrawLine($arrowPen, $ax - $s*0.09, $ay - $s*0.21, $ax, $ay - $s*0.30)
    $g.DrawLine($arrowPen, $ax + $s*0.09, $ay - $s*0.21, $ax, $ay - $s*0.30)

    $g.Dispose()
    return $bmp
}

function Save-Ico([System.Drawing.Bitmap[]]$images, [string]$icoPath) {
    $pngs = @()
    foreach ($img in $images) {
        $ms = [System.IO.MemoryStream]::new()
        $img.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngs += ,$ms.ToArray()
        $ms.Dispose()
    }

    $fs = [System.IO.FileStream]::new($icoPath, [System.IO.FileMode]::Create)
    $w = [System.IO.BinaryWriter]::new($fs)
    $count = $pngs.Count
    $w.Write([UInt16]0)            # reserved
    $w.Write([UInt16]1)            # type: icon
    $w.Write([UInt16]$count)       # count
    $offset = 6 + 16 * $count
    for ($i = 0; $i -lt $count; $i++) {
        $img = $images[$i]
        $png = $pngs[$i]
        if ($img.Width -ge 256) { $w.Write([Byte]0) } else { $w.Write([Byte]$img.Width) }
        if ($img.Height -ge 256) { $w.Write([Byte]0) } else { $w.Write([Byte]$img.Height) }
        $w.Write([Byte]0)          # palette
        $w.Write([Byte]0)          # reserved
        $w.Write([UInt16]1)        # planes
        $w.Write([UInt16]32)       # bpp
        $w.Write([UInt32]$png.Length)
        $w.Write([UInt32]$offset)
        $offset += $png.Length
    }
    foreach ($png in $pngs) { $w.Write($png) }
    $w.Flush()
    $w.Close()
    $fs.Close()
}

$dir = [System.IO.Path]::GetFullPath($OutputDir)
New-Item -ItemType Directory -Path $dir -Force | Out-Null

$sizes = 16, 24, 32, 48, 64, 128, 256
$images = @($sizes | ForEach-Object { New-IconImage $_ })

Save-Ico -Images $images -IcoPath (Join-Path $dir "SafeGitPublisher.ico")

# keep 256 source png for design reference
$src = New-IconImage 256
$src.Save((Join-Path $dir "SafeGitPublisher-source.png"), [System.Drawing.Imaging.ImageFormat]::Png)
$src.Dispose()

foreach ($img in $images) { $img.Dispose() }

Write-Output "Generated:"
Write-Output "  $dir\SafeGitPublisher.ico (sizes: $($sizes -join '/'))"
Write-Output "  $dir\SafeGitPublisher-source.png"
