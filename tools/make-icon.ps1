# Draws the CouchShell logo and writes ui/CouchShell/Assets/app.ico. Run it only when the logo
# changes; the .ico is committed.
# Usage: powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$output = Join-Path (Split-Path -Parent $PSScriptRoot) 'ui\CouchShell\Assets\app.ico'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $output) | Out-Null

# The couch, on a 256 x 256 grid. The header logo in MainWindow.xaml uses the same shapes.
$logo = [Windows.Markup.XamlReader]::Parse(@'
<Canvas xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Width="256" Height="256">
  <Rectangle Width="256" Height="256" RadiusX="56" RadiusY="56">
    <Rectangle.Fill>
      <LinearGradientBrush StartPoint="0,0" EndPoint="1,1">
        <GradientStop Color="#6D7CFF" Offset="0" />
        <GradientStop Color="#3A2BB8" Offset="1" />
      </LinearGradientBrush>
    </Rectangle.Fill>
  </Rectangle>
  <Rectangle Canvas.Left="66" Canvas.Top="70" Width="124" Height="72" RadiusX="20" RadiusY="20" Fill="#C9D0FF" />
  <Rectangle Canvas.Left="72" Canvas.Top="130" Width="112" Height="48" RadiusX="10" RadiusY="10" Fill="White" />
  <Rectangle Canvas.Left="38" Canvas.Top="112" Width="46" Height="72" RadiusX="20" RadiusY="20" Fill="White" />
  <Rectangle Canvas.Left="172" Canvas.Top="112" Width="46" Height="72" RadiusX="20" RadiusY="20" Fill="White" />
  <Rectangle Canvas.Left="60" Canvas.Top="184" Width="18" Height="20" RadiusX="4" RadiusY="4" Fill="White" />
  <Rectangle Canvas.Left="178" Canvas.Top="184" Width="18" Height="20" RadiusX="4" RadiusY="4" Fill="White" />
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
