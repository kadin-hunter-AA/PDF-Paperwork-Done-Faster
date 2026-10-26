param(
    [string]$Source = (Join-Path $PSScriptRoot '..\selected-pdf-quill-2026-10-22.png'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\..\..\development\App.Desktop\Assets'),
    [string]$ProductionMaster = (Join-Path $PSScriptRoot 'production-master-2026-10-22.png'),
    [double]$ArtworkScale = 1.0,
    [byte]$AlphaSpeckCutoff = 8,
    [int]$MaxIsolatedSpeckPixels = 64
)

$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
    throw 'Run this script in Windows PowerShell with -STA.'
}
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

$sourcePath = [IO.Path]::GetFullPath($Source)
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
$masterPath = [IO.Path]::GetFullPath($ProductionMaster)
if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) { throw "Missing selected master: $sourcePath" }
if ($ArtworkScale -lt 0.80 -or $ArtworkScale -gt 1.00) { throw 'ArtworkScale must be between 0.80 and 1.00.' }
if ($MaxIsolatedSpeckPixels -lt 1 -or $MaxIsolatedSpeckPixels -gt 1024) { throw 'MaxIsolatedSpeckPixels must be between 1 and 1024.' }
New-Item -ItemType Directory -Force -Path $outputPath | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $masterPath) | Out-Null

# Clear only small, disconnected low-alpha components. Keep antialiased edge components intact.
$inputStream = [IO.File]::OpenRead($sourcePath)
try {
    $decoder = [Windows.Media.Imaging.PngBitmapDecoder]::new($inputStream, [Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat, [Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
    $sourceBitmap = $decoder.Frames[0]
} finally { $inputStream.Dispose() }
if ($sourceBitmap.PixelWidth -ne $sourceBitmap.PixelHeight) { throw 'Selected master must be square.' }

$bgra = [Windows.Media.Imaging.FormatConvertedBitmap]::new($sourceBitmap, [Windows.Media.PixelFormats]::Bgra32, $null, 0)
$stride = $bgra.PixelWidth * 4
$pixels = [byte[]]::new($stride * $bgra.PixelHeight)
$bgra.CopyPixels($pixels, $stride, 0)
$cleanerSource = @'
using System;
using System.Collections.Generic;
public static class PdfIconAlphaCleanup {
    public static int ClearIsolatedSpecks(byte[] bgra, int width, int height, byte cutoff, int maxComponentPixels) {
        int pixelCount = width * height;
        var visited = new bool[pixelCount];
        var queue = new Queue<int>();
        var component = new List<int>();
        int removed = 0;
        for (int start = 0; start < pixelCount; start++) {
            byte initialAlpha = bgra[start * 4 + 3];
            if (visited[start] || initialAlpha == 0 || initialAlpha >= cutoff) continue;
            visited[start] = true;
            queue.Enqueue(start);
            component.Clear();
            bool touchesRetainedEdge = false;
            while (queue.Count > 0) {
                int current = queue.Dequeue();
                component.Add(current);
                int y = current / width, x = current - y * width;
                for (int ny = Math.Max(0, y - 1); ny <= Math.Min(height - 1, y + 1); ny++) {
                    for (int nx = Math.Max(0, x - 1); nx <= Math.Min(width - 1, x + 1); nx++) {
                        int neighbor = ny * width + nx;
                        byte alpha = bgra[neighbor * 4 + 3];
                        if (alpha >= cutoff) touchesRetainedEdge = true;
                        else if (alpha > 0 && !visited[neighbor]) {
                            visited[neighbor] = true;
                            queue.Enqueue(neighbor);
                        }
                    }
                }
            }
            if (!touchesRetainedEdge && component.Count <= maxComponentPixels) {
                foreach (int pixel in component) bgra[pixel * 4 + 3] = 0;
                removed += component.Count;
            }
        }
        return removed;
    }
}
'@
if (-not ("PdfIconAlphaCleanup" -as [type])) {
    Add-Type -TypeDefinition $cleanerSource
}
$removedSpecks = [PdfIconAlphaCleanup]::ClearIsolatedSpecks($pixels, $bgra.PixelWidth, $bgra.PixelHeight, $AlphaSpeckCutoff, $MaxIsolatedSpeckPixels)
$cleanMaster = [Windows.Media.Imaging.BitmapSource]::Create($bgra.PixelWidth, $bgra.PixelHeight, 96, 96, [Windows.Media.PixelFormats]::Bgra32, $null, $pixels, $stride)
$masterEncoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
$masterEncoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($cleanMaster))
$masterStream = [IO.File]::Create($masterPath)
try { $masterEncoder.Save($masterStream) } finally { $masterStream.Dispose() }

$sizes = @(16,20,24,30,32,36,40,48,60,64,72,80,96,128,150,256)
$entries = [Collections.Generic.List[object]]::new()
foreach ($size in $sizes) {
    $render = [Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $visual = [Windows.Media.DrawingVisual]::new()
    $context = $visual.RenderOpen()
    try {
        $context.PushGuidelineSet([Windows.Media.GuidelineSet]::new())
        $margin = ($size * (1.0 - $ArtworkScale)) / 2.0
        $rect = [Windows.Rect]::new($margin, $margin, $size * $ArtworkScale, $size * $ArtworkScale)
        $context.DrawImage($cleanMaster, $rect)
    } finally { $context.Dispose() }
    [Windows.Media.RenderOptions]::SetBitmapScalingMode($visual, [Windows.Media.BitmapScalingMode]::HighQuality)
    $render.Render($visual)
    $render.Freeze()
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($render))
    $file = Join-Path $outputPath "PdfEditor-$size.png"
    $out = [IO.File]::Create($file)
    try { $encoder.Save($out) } finally { $out.Dispose() }
    $png = [IO.File]::ReadAllBytes($file)
    $entries.Add([pscustomobject]@{ Size=$size; Png=$png })
}

# ICO directory with native-size PNG payloads, preserving alpha and avoiding shell resampling.
$icoPath = Join-Path $outputPath 'PdfEditor.ico'
$ico = [IO.File]::Create($icoPath)
$writer = [IO.BinaryWriter]::new($ico)
try {
    $icoSizes = @(16,20,24,32,40,48,64,128,256)
    $writer.Write([UInt16]0); $writer.Write([UInt16]1); $writer.Write([UInt16]$icoSizes.Count)
    $offset = 6 + (16 * $icoSizes.Count)
    foreach ($size in $icoSizes) {
        $entry = $entries | Where-Object Size -eq $size | Select-Object -First 1
        $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
        $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
        $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([UInt16]1); $writer.Write([UInt16]32)
        $writer.Write([UInt32]$entry.Png.Length); $writer.Write([UInt32]$offset)
        $offset += $entry.Png.Length
    }
    foreach ($size in $icoSizes) {
        $entry = $entries | Where-Object Size -eq $size | Select-Object -First 1
        $writer.Write([byte[]]$entry.Png)
    }
} finally { $writer.Dispose(); $ico.Dispose() }

Write-Output "Exported $($entries.Count) exact-size PNGs and nine ICO sizes from $sourcePath."
Write-Output "Production master: $masterPath; isolated alpha speck pixels cleared: $removedSpecks (components <= $MaxIsolatedSpeckPixels, alpha < $AlphaSpeckCutoff); edge-connected antialiasing retained; artwork scale: $ArtworkScale."
Write-Output "Output directory: $outputPath"
