param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "..\src\ProjectFileHub.App\Assets")
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

function New-IconPngBytes {
    param([int]$Size)

    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.Clear([System.Drawing.Color]::Transparent)

    # A dedicated small-size mark: a simple cyan disc and a geometric navy F.
    # Every frame is rendered at its native size so 16-32 px notification-area
    # icons stay readable instead of becoming a scaled-down illustration.
    $outerInset = [float][Math]::Max(0.75, $Size * 0.035)
    $outerDiameter = [float]($Size - 2 * $outerInset)
    $outlineBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 4, 11, 20))
    $graphics.FillEllipse($outlineBrush, $outerInset, $outerInset, $outerDiameter, $outerDiameter)

    $innerInset = [float][Math]::Max(1.35, $Size * 0.075)
    $innerDiameter = [float]($Size - 2 * $innerInset)
    $discBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 25, 181, 254))
    $graphics.FillEllipse($discBrush, $innerInset, $innerInset, $innerDiameter, $innerDiameter)

    $letterBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 5, 18, 31))
    $stemX = [float][Math]::Round($Size * 0.33, 1)
    $stemY = [float][Math]::Round($Size * 0.24, 1)
    $stemWidth = [float][Math]::Max(2.1, $Size * 0.145)
    $stemHeight = [float][Math]::Round($Size * 0.53, 1)
    $topWidth = [float][Math]::Round($Size * 0.36, 1)
    $barHeight = [float][Math]::Max(2.1, $Size * 0.145)
    $middleWidth = [float][Math]::Round($Size * 0.285, 1)
    $middleY = [float][Math]::Round($Size * 0.45, 1)
    $graphics.FillRectangle($letterBrush, $stemX, $stemY, $stemWidth, $stemHeight)
    $graphics.FillRectangle($letterBrush, $stemX, $stemY, $topWidth, $barHeight)
    $graphics.FillRectangle($letterBrush, $stemX, $middleY, $middleWidth, $barHeight)

    $memory = [System.IO.MemoryStream]::new()
    $bitmap.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $memory.ToArray()

    $memory.Dispose()
    $letterBrush.Dispose()
    $discBrush.Dispose()
    $outlineBrush.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()
    return $bytes
}

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
[System.IO.Directory]::CreateDirectory($resolvedOutput) | Out-Null
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = foreach ($size in $sizes) {
    [pscustomobject]@{ Size = $size; Bytes = (New-IconPngBytes $size) }
}

$iconPath = Join-Path $resolvedOutput "ProjectFileHub.ico"
$stream = [System.IO.File]::Create($iconPath)
$writer = [System.IO.BinaryWriter]::new($stream)
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]$frames.Count)

$offset = 6 + (16 * $frames.Count)
foreach ($frame in $frames) {
    $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
    $writer.Write([byte]$dimension)
    $writer.Write([byte]$dimension)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]$frame.Bytes.Length)
    $writer.Write([uint32]$offset)
    $offset += $frame.Bytes.Length
}

foreach ($frame in $frames) {
    $writer.Write([byte[]]$frame.Bytes)
}

$writer.Dispose()
$stream.Dispose()
[System.IO.File]::WriteAllBytes(
    (Join-Path $resolvedOutput "ProjectFileHub-256.png"),
    [byte[]]($frames | Where-Object Size -eq 256 | Select-Object -ExpandProperty Bytes))

Write-Host "Generated $iconPath"
