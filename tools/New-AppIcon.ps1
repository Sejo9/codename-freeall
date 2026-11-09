#Requires -Version 7
<#
.SYNOPSIS
    Builds FreeAll's Windows icon and header image from the source artwork.

.DESCRIPTION
    Reads assets/FreeAll-Icon.png (square, transparent background) and writes:
      - assets/FreeAll.ico: 16, 20, 24, 32, 40, 48, 64, 96 and 128 px as 32-bit bitmaps (readable by every
        part of Windows, including the resource compiler) and 256 px as PNG, which Windows uses for large
        icons in Explorer, Start and Settings.
      - assets/FreeAll-96.png: the image beside the app name in the navigation pane.
    Each size is scaled from the source in one high-quality pass. Run it again after changing the artwork
    and commit the results; builds use the generated files and don't need this script.
#>
[CmdletBinding()]
param(
    [string] $Source = (Join-Path $PSScriptRoot '..\assets\FreeAll-Icon.png'),
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..\assets')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase
$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256

$stream = [IO.File]::OpenRead((Resolve-Path $Source))
try {
    $frame = [System.Windows.Media.Imaging.BitmapDecoder]::Create($stream, 'PreservePixelFormat', 'OnLoad').Frames[0]
}
finally {
    $stream.Dispose()
}

if ($frame.PixelWidth -ne $frame.PixelHeight) { throw "The artwork must be square; it is $($frame.PixelWidth) x $($frame.PixelHeight)." }

# Scales the artwork to size x size, straight (not premultiplied) BGRA.
function Get-Scaled([int] $size) {
    $visual = [System.Windows.Media.DrawingVisual]::new()
    [System.Windows.Media.RenderOptions]::SetBitmapScalingMode($visual, [System.Windows.Media.BitmapScalingMode]::HighQuality)
    $context = $visual.RenderOpen()
    $context.DrawImage($frame, [System.Windows.Rect]::new(0, 0, $size, $size))
    $context.Close()
    $target = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $target.Render($visual)
    return [System.Windows.Media.Imaging.FormatConvertedBitmap]::new($target, [System.Windows.Media.PixelFormats]::Bgra32, $null, 0)
}

function Get-PngBytes($bitmap) {
    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $memory = [IO.MemoryStream]::new()
    $encoder.Save($memory)
    return , $memory.ToArray() # the comma keeps PowerShell from unrolling the array
}

# A 32-bit DIB as an icon stores it: header with doubled height, bottom-up rows, then an all-zero AND mask.
function Get-DibBytes($bitmap, [int] $size) {
    $stride = $size * 4
    $pixels = [byte[]]::new($stride * $size)
    $bitmap.CopyPixels($pixels, $stride, 0)
    $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
    $memory = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($memory)
    $writer.Write([int]40); $writer.Write([int]$size); $writer.Write([int]($size * 2))
    $writer.Write([int16]1); $writer.Write([int16]32); $writer.Write([int]0)
    $writer.Write([int]($stride * $size + $maskStride * $size))
    $writer.Write([int]0); $writer.Write([int]0); $writer.Write([int]0); $writer.Write([int]0)
    for ($row = $size - 1; $row -ge 0; $row--) { $writer.Write($pixels, $row * $stride, $stride) }
    $writer.Write([byte[]]::new($maskStride * $size))
    $writer.Flush()
    return , $memory.ToArray()
}

$images = foreach ($size in $sizes) {
    $scaled = Get-Scaled $size
    [pscustomobject]@{ Size = $size; Bytes = [byte[]]$(if ($size -ge 256) { Get-PngBytes $scaled } else { Get-DibBytes $scaled $size }) }
}

New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$ico = Join-Path $OutputDirectory 'FreeAll.ico'
$file = [IO.File]::Create($ico)
try {
    $writer = [IO.BinaryWriter]::new($file)
    $writer.Write([int16]0); $writer.Write([int16]1); $writer.Write([int16]$images.Count)
    $offset = 6 + 16 * $images.Count
    foreach ($image in $images) {
        $dimension = if ($image.Size -ge 256) { 0 } else { $image.Size }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([int16]1); $writer.Write([int16]32); $writer.Write([int]$image.Bytes.Length); $writer.Write([int]$offset)
        $offset += $image.Bytes.Length
    }

    foreach ($image in $images) { $writer.Write($image.Bytes) }
    $writer.Flush()
}
finally {
    $file.Dispose()
}

$header = Join-Path $OutputDirectory 'FreeAll-96.png'
[IO.File]::WriteAllBytes($header, [byte[]](Get-PngBytes (Get-Scaled 96)))
Write-Host "Wrote $ico ($((Get-Item $ico).Length) bytes, sizes $($sizes -join ', ')) and $header"
