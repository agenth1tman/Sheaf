# Draws the Sheaf icon (the two sheets from the title bar: a blue one behind, a white one in front) and writes a
# multi-size App.ico.
# Usage: powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
param([string]$Output = (Join-Path $PSScriptRoot '..\src\Sheaf\Assets\App.ico'))

Add-Type -AssemblyName System.Drawing

$accent  = [System.Drawing.Color]::FromArgb(255, 37, 99, 235)
$paper   = [System.Drawing.Color]::FromArgb(255, 244, 244, 245)

function New-RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

# The mark is drawn on the same 24-unit grid as the title bar, scaled so it fills the icon.
function Render-Bitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $scale = $size * 0.94 / 19.5
    $g.TranslateTransform($size / 2, $size / 2)
    $g.ScaleTransform($scale, $scale)
    $g.TranslateTransform(-11.3, -12.5)

    $back = New-RoundRect 3 5 12 16 2
    $turn = New-Object System.Drawing.Drawing2D.Matrix
    $turn.RotateAt(-12, (New-Object System.Drawing.PointF 9, 13))
    $back.Transform($turn)
    $blue = New-Object System.Drawing.SolidBrush $accent
    $g.FillPath($blue, $back)

    # Exactly the mark the app draws (Draw.AppMark): no outline, so the icon and the title bar match.
    $front = New-RoundRect 8 3 12 16 2
    $white = New-Object System.Drawing.SolidBrush $paper
    $g.FillPath($white, $front)

    $white.Dispose(); $blue.Dispose(); $turn.Dispose(); $front.Dispose(); $back.Dispose()
    $g.Dispose()
    return $bmp
}

# PNG for the 256 px frame, classic 32-bit DIB for the rest (older loaders only read DIB frames).
function Encode-Frame($bmp) {
    $ms = New-Object System.IO.MemoryStream
    if ($bmp.Width -ge 256) {
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        return ,$ms.ToArray()
    }
    $size = $bmp.Width
    $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
    $bw = New-Object System.IO.BinaryWriter $ms
    $bw.Write([UInt32]40); $bw.Write([Int32]$size); $bw.Write([Int32]($size * 2))
    $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]0)
    $bw.Write([UInt32]($size * $size * 4 + $maskStride * $size))
    $bw.Write([Int32]0); $bw.Write([Int32]0); $bw.Write([UInt32]0); $bw.Write([UInt32]0)
    $rect = New-Object System.Drawing.Rectangle 0, 0, $size, $size
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $row = New-Object byte[] ($size * 4)
    for ($y = $size - 1; $y -ge 0; $y--) {
        [System.Runtime.InteropServices.Marshal]::Copy([IntPtr]($data.Scan0.ToInt64() + $y * $data.Stride), $row, 0, $row.Length)
        $bw.Write($row)
    }
    $bmp.UnlockBits($data)
    $bw.Write((New-Object byte[] ($maskStride * $size)))
    $bw.Flush()
    return ,$ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($size in $sizes) { $bmp = Render-Bitmap $size; ,(Encode-Frame $bmp); $bmp.Dispose() }

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([Byte]$dim); $w.Write([Byte]$dim); $w.Write([Byte]0); $w.Write([Byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$images[$i].Length); $w.Write([UInt32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Flush()
New-Item -ItemType Directory -Force (Split-Path $Output) | Out-Null
[System.IO.File]::WriteAllBytes($Output, $out.ToArray())
Write-Output "Wrote $Output ($($out.Length) bytes)"
