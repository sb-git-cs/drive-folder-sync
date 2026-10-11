$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null

Add-Type -AssemblyName System.Drawing

function New-RoundRect([double]$x, [double]$y, [double]$w, [double]$h, [double]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [float]($r * 2)
    $path.AddArc([float]$x, [float]$y, $d, $d, 180, 90)
    $path.AddArc([float]($x + $w - $d), [float]$y, $d, $d, 270, 90)
    $path.AddArc([float]($x + $w - $d), [float]($y + $h - $d), $d, $d, 0, 90)
    $path.AddArc([float]$x, [float]($y + $h - $d), $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function Add-Arrow($g, $pen, $brush, [double]$cx, [double]$cy, [double]$radius, [double]$start, [double]$sweep, [double]$head) {
    $g.DrawArc($pen, [float]($cx - $radius), [float]($cy - $radius), [float]($radius * 2), [float]($radius * 2), [float]$start, [float]$sweep)
    $end = ($start + $sweep) * [Math]::PI / 180
    $px = $cx + $radius * [Math]::Cos($end)
    $py = $cy + $radius * [Math]::Sin($end)
    $tx = -[Math]::Sin($end)
    $ty = [Math]::Cos($end)
    $nx = [Math]::Cos($end)
    $ny = [Math]::Sin($end)
    $points = @(
        (New-Object System.Drawing.PointF ([float]($px + $tx * $head)), ([float]($py + $ty * $head))),
        (New-Object System.Drawing.PointF ([float]($px + $nx * $head * 0.8)), ([float]($py + $ny * $head * 0.8))),
        (New-Object System.Drawing.PointF ([float]($px - $nx * $head * 0.8)), ([float]($py - $ny * $head * 0.8)))
    )
    $g.FillPolygon($brush, [System.Drawing.PointF[]]$points)
}

# Blue rounded tile with a white cloud; sync arrows inside the cloud at larger sizes.
function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $s = [double]$size
    $m = [Math]::Max(0.5, $s * 0.03)
    $tile = New-RoundRect $m $m ($s - 2 * $m) ($s - 2 * $m) ($s * 0.22)
    $rect = New-Object System.Drawing.RectangleF ([float]0), ([float]0), ([float]$s), ([float]$s)
    $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(46, 145, 240)), ([System.Drawing.Color]::FromArgb(0, 84, 178)), 60
    $g.FillPath($fill, $tile)
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $scale = if ($size -le 24) { 1.08 } else { 1.0 }
    $cx = $s * 0.5
    $cy = $s * 0.54
    function P([double]$v) { return $v * $s * $scale }
    $base = New-RoundRect ($cx - (P 0.30)) ($cy - (P 0.02)) (P 0.60) (P 0.22) (P 0.11)
    $g.FillPath($white, $base)
    $g.FillEllipse($white, [float]($cx + (P 0.03) - (P 0.19)), [float]($cy - (P 0.10) - (P 0.19)), [float](P 0.38), [float](P 0.38))
    $g.FillEllipse($white, [float]($cx - (P 0.15) - (P 0.13)), [float]($cy + (P 0.02) - (P 0.13)), [float](P 0.26), [float](P 0.26))
    $g.FillEllipse($white, [float]($cx + (P 0.18) - (P 0.115)), [float]($cy + (P 0.05) - (P 0.115)), [float](P 0.23), [float](P 0.23))
    if ($size -ge 40) {
        $blue = [System.Drawing.Color]::FromArgb(0, 98, 196)
        $pen = New-Object System.Drawing.Pen $blue, ([float]($s * 0.042))
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $arrow = New-Object System.Drawing.SolidBrush $blue
        $ax = $cx + $s * 0.005
        $ay = $cy + $s * 0.035
        $radius = $s * 0.105
        Add-Arrow $g $pen $arrow $ax $ay $radius 195 125 ($s * 0.055)
        Add-Arrow $g $pen $arrow $ax $ay $radius 15 125 ($s * 0.055)
        $pen.Dispose()
        $arrow.Dispose()
    }
    $white.Dispose()
    $fill.Dispose()
    $g.Dispose()
    return $bmp
}

# Writes a Windows .ico with 32-bit DIB images and a PNG image for 256 px.
function Save-Icon([string]$path, [int[]]$sizes) {
    $images = New-Object System.Collections.Generic.List[byte[]]
    foreach ($size in $sizes) {
        $bmp = New-IconBitmap $size
        $ms = New-Object System.IO.MemoryStream
        if ($size -ge 256) {
            $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        } else {
            $data = $bmp.LockBits((New-Object System.Drawing.Rectangle 0, 0, $size, $size), [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
            $pixels = New-Object byte[] ($size * $size * 4)
            for ($row = 0; $row -lt $size; $row++) {
                [System.Runtime.InteropServices.Marshal]::Copy([IntPtr]($data.Scan0.ToInt64() + $row * $data.Stride), $pixels, $row * $size * 4, $size * 4)
            }
            $bmp.UnlockBits($data)
            $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
            $mask = New-Object byte[] ($maskStride * $size)
            $writer = New-Object System.IO.BinaryWriter $ms
            $writer.Write([int]40)
            $writer.Write([int]$size)
            $writer.Write([int]($size * 2))
            $writer.Write([int16]1)
            $writer.Write([int16]32)
            $writer.Write([int]0)
            $writer.Write([int]($pixels.Length + $mask.Length))
            $writer.Write([int]0)
            $writer.Write([int]0)
            $writer.Write([int]0)
            $writer.Write([int]0)
            for ($row = $size - 1; $row -ge 0; $row--) {
                $writer.Write($pixels, $row * $size * 4, $size * 4)
                for ($col = 0; $col -lt $size; $col++) {
                    if ($pixels[($row * $size + $col) * 4 + 3] -lt 128) {
                        $index = ($size - 1 - $row) * $maskStride + [Math]::Floor($col / 8)
                        $mask[$index] = $mask[$index] -bor (0x80 -shr ($col % 8))
                    }
                }
            }
            $writer.Write($mask)
            $writer.Flush()
        }
        $images.Add($ms.ToArray())
        $ms.Dispose()
        $bmp.Dispose()
    }
    $out = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $out
    $w.Write([int16]0)
    $w.Write([int16]1)
    $w.Write([int16]$sizes.Length)
    $offset = 6 + 16 * $sizes.Length
    for ($i = 0; $i -lt $sizes.Length; $i++) {
        $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
        $w.Write([byte]$dim)
        $w.Write([byte]$dim)
        $w.Write([byte]0)
        $w.Write([byte]0)
        $w.Write([int16]1)
        $w.Write([int16]32)
        $w.Write([int]$images[$i].Length)
        $w.Write([int]$offset)
        $offset += $images[$i].Length
    }
    foreach ($image in $images) { $w.Write($image) }
    $w.Flush()
    [System.IO.File]::WriteAllBytes($path, $out.ToArray())
    $w.Dispose()
}

$icoPath = Join-Path $root 'app.ico'
Save-Icon $icoPath @(16, 20, 24, 32, 40, 48, 64, 256)

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources = @(Get-ChildItem (Join-Path $root 'src\*.cs') | ForEach-Object { $_.FullName })
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('/nologo')
$lines.Add('/target:winexe')
$lines.Add('/optimize+')
$lines.Add('/platform:anycpu')
$lines.Add('/win32manifest:' + (Join-Path $root 'app.manifest'))
$lines.Add('/win32icon:' + $icoPath)
$lines.Add('/resource:' + $icoPath + ',DriveFolderSync.app.ico')
$lines.Add('/out:' + (Join-Path $dist 'DriveFolderSyncSetup.exe'))
$lines.Add('/reference:System.Windows.Forms.dll')
$lines.Add('/reference:System.Drawing.dll')
$lines.Add('/reference:System.Web.Extensions.dll')
foreach ($source in $sources) { $lines.Add($source) }
$response = Join-Path $root 'build.rsp'
[System.IO.File]::WriteAllLines($response, $lines.ToArray())
& $csc ('@' + $response)
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Remove-Item $response -Force
Write-Output ("built " + (Join-Path $dist 'DriveFolderSyncSetup.exe'))
