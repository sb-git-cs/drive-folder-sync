$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null

Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap 32, 32
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.Clear([System.Drawing.Color]::Transparent)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$folder = New-Object System.Drawing.Drawing2D.GraphicsPath
$g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0, 90, 158))), 2, 8, 28, 20)
$g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0, 120, 190))), 2, 6, 12, 6)
$g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(22, 160, 60))), 6, 16, 8, 8)
$g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(200, 40, 40))), 18, 16, 8, 8)
$icon = [System.Drawing.Icon]::FromHandle($bmp.GetHicon())
$icoPath = Join-Path $root 'app.ico'
$stream = [System.IO.File]::Create($icoPath)
$icon.Save($stream)
$stream.Close()
$g.Dispose()
$bmp.Dispose()

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources = @(Get-ChildItem (Join-Path $root 'src\*.cs') | ForEach-Object { $_.FullName })
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('/nologo')
$lines.Add('/target:winexe')
$lines.Add('/optimize+')
$lines.Add('/platform:anycpu')
$lines.Add('/win32manifest:' + (Join-Path $root 'app.manifest'))
$lines.Add('/win32icon:' + $icoPath)
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
