$ErrorActionPreference = 'Stop'
$AppDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$BinDir = Join-Path $AppDir 'bin'
if (-not (Test-Path $BinDir)) {
    New-Item -ItemType Directory -Path $BinDir | Out-Null
}

$Csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $Csc)) {
    throw "C# compiler not found at $Csc"
}

$IcoPath = Join-Path $BinDir 'DualWanBalancer.ico'
$ExePath = Join-Path $BinDir 'DualWanBalancer.exe'

# Generate crisp 32x32 ICO using System.Drawing
Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap 32, 32
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::Transparent)
$bg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(15, 23, 42))
$border = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(16, 185, 129), 2.5)
$g.FillEllipse($bg, 2, 2, 28, 28)
$g.DrawEllipse($border, 2, 2, 28, 28)
$downPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(52, 211, 153), 2.5)
$upPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(56, 189, 248), 2.5)
$g.DrawLine($downPen, 11, 8, 11, 22)
$g.DrawLine($downPen, 7, 18, 11, 22)
$g.DrawLine($downPen, 15, 18, 11, 22)
$g.DrawLine($upPen, 21, 23, 21, 9)
$g.DrawLine($upPen, 17, 13, 21, 9)
$g.DrawLine($upPen, 25, 13, 21, 9)
$g.Dispose()

# Save BMP as PNG inside standard Vista+ ICO container
$pngStream = New-Object System.IO.MemoryStream
$bmp.Save($pngStream, [System.Drawing.Imaging.ImageFormat]::Png)
$pngBytes = $pngStream.ToArray()
$pngStream.Dispose()
$bmp.Dispose()

$icoStream = New-Object System.IO.FileStream ($IcoPath, [System.IO.FileMode]::Create)
$bw = New-Object System.IO.BinaryWriter $icoStream
$bw.Write([uint16]0)        # Reserved
$bw.Write([uint16]1)        # Type = Icon
$bw.Write([uint16]1)        # Count = 1
$bw.Write([byte]32)         # Width
$bw.Write([byte]32)         # Height
$bw.Write([byte]0)          # Colors
$bw.Write([byte]0)          # Reserved
$bw.Write([uint16]1)        # Planes
$bw.Write([uint16]32)       # BitsPerPixel
$bw.Write([uint32]$pngBytes.Length)
$bw.Write([uint32]22)       # Offset
$bw.Write($pngBytes)
$bw.Close()
$icoStream.Close()

$Sources = @(
    (Join-Path $AppDir 'AssemblyInfo.cs'),
    (Join-Path $AppDir 'AppConfig.cs'),
    (Join-Path $AppDir 'NetworkDiscovery.cs'),
    (Join-Path $AppDir 'LatencyMonitor.cs'),
    (Join-Path $AppDir 'GameDetector.cs'),
    (Join-Path $AppDir 'NetworkOptimizer.cs'),
    (Join-Path $AppDir 'DualWanProxyEngine.cs'),
    (Join-Path $AppDir 'IconGenerator.cs'),
    (Join-Path $AppDir 'SpeedBarForm.cs'),
    (Join-Path $AppDir 'MainForm.cs'),
    (Join-Path $AppDir 'Program.cs')
)

& $Csc `
    /nologo `
    /target:winexe `
    /platform:x64 `
    /optimize+ `
    /win32icon:"$IcoPath" `
    /out:"$ExePath" `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    /reference:System.Web.Extensions.dll `
    @Sources

if ($LASTEXITCODE -ne 0) {
    throw "csc.exe compilation failed with exit code $LASTEXITCODE"
}

Write-Output "Built: $ExePath"
