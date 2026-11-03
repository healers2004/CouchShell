# Draws the CouchShell logo and writes ui/CouchShell/Assets/app.ico. Run it only when the logo
# changes; the .ico is committed.
# Usage: powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$output = Join-Path (Split-Path -Parent $PSScriptRoot) 'ui\CouchShell\Assets\app.ico'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $output) | Out-Null

# A TV with a green controller in it, on a 256 x 256 grid. The header logo in MainWindow.xaml uses the same shapes.
$logo = [Windows.Markup.XamlReader]::Parse(@'
<Canvas xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Width="256" Height="256">
  <Rectangle Width="256" Height="256" RadiusX="56" RadiusY="56" Fill="#15171C" />
  <Rectangle Canvas.Left="46" Canvas.Top="58" Width="164" Height="114" RadiusX="20" RadiusY="20" Stroke="White" StrokeThickness="14" />
  <Path Data="M100,200 H156" Stroke="White" StrokeThickness="14" StrokeStartLineCap="Round" StrokeEndLineCap="Round" />
  <Path Fill="#3DBA3D" Data="M100,98 H156 A26,26 0 0 1 181,131 L176,146 A11,11 0 0 1 157,149 L150,139 H106 L99,149 A11,11 0 0 1 80,146 L75,131 A26,26 0 0 1 100,98 Z" />
  <Rectangle Canvas.Left="92" Canvas.Top="114" Width="6" Height="18" RadiusX="2" RadiusY="2" Fill="#15171C" />
  <Rectangle Canvas.Left="86" Canvas.Top="120" Width="18" Height="6" RadiusX="2" RadiusY="2" Fill="#15171C" />
  <Ellipse Canvas.Left="155.5" Canvas.Top="111.5" Width="9" Height="9" Fill="#15171C" />
  <Ellipse Canvas.Left="165.5" Canvas.Top="121.5" Width="9" Height="9" Fill="#15171C" />
</Canvas>
'@)

$sizes = 16, 24, 32, 48, 64, 128, 256
$images = foreach ($size in $sizes) {
    $brush = New-Object Windows.Media.VisualBrush $logo
    $logo.Measure((New-Object Windows.Size 256, 256))
    $logo.Arrange((New-Object Windows.Rect 0, 0, 256, 256))
    $visual = New-Object Windows.Media.DrawingVisual
    $context = $visual.RenderOpen()
    $context.DrawRectangle($brush, $null, (New-Object Windows.Rect 0, 0, $size, $size))
    $context.Close()
    $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object IO.MemoryStream
    $encoder.Save($stream)
    , $stream.ToArray()
}

# An .ico file: a 6-byte header, a 16-byte entry per image, then the images (PNG is allowed).
$file = New-Object IO.MemoryStream
$writer = New-Object IO.BinaryWriter $file
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dimension = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }  # 0 means 256 in this format
    $writer.Write([byte]$dimension)
    $writer.Write([byte]$dimension)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]$images[$i].Length)
    $writer.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($image in $images) { $writer.Write([byte[]]$image) }
$writer.Flush()
[IO.File]::WriteAllBytes($output, $file.ToArray())
Write-Output "Wrote $output ($($file.Length) bytes)"
