$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetDir = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../assets'))
[IO.Directory]::CreateDirectory($assetDir) | Out-Null
$images = @()
foreach ($size in @(256, 64, 48, 32, 16)) {
    $bitmap = [Drawing.Bitmap]::new($size, $size)
    $g = [Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.ScaleTransform($size / 256.0, $size / 256.0)
    $path = [Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc(4, 4, 76, 76, 180, 90)
    $path.AddArc(176, 4, 76, 76, 270, 90)
    $path.AddArc(176, 176, 76, 76, 0, 90)
    $path.AddArc(4, 176, 76, 76, 90, 90)
    $path.CloseFigure()
    $fill = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#D5F77F'))
    $g.FillPath($fill, $path)
    $pen = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml('#1A2510'), 18)
    $pen.StartCap = [Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [Drawing.Drawing2D.LineJoin]::Round
    $g.DrawLines($pen, [Drawing.PointF[]]@([Drawing.PointF]::new(72,82),[Drawing.PointF]::new(128,166),[Drawing.PointF]::new(184,82)))
    $g.DrawLine($pen, 128, 78, 128, 166)
    $stream = [IO.MemoryStream]::new()
    $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
    $images += ,@{ Size = $size; Bytes = $stream.ToArray() }
    if ($size -eq 256) { $bitmap.Save((Join-Path $assetDir 'Vidl.png'), [Drawing.Imaging.ImageFormat]::Png) }
    $stream.Dispose(); $pen.Dispose(); $fill.Dispose(); $path.Dispose(); $g.Dispose(); $bitmap.Dispose()
}
$file = [IO.File]::Create((Join-Path $assetDir 'Vidl.ico'))
$writer = [IO.BinaryWriter]::new($file)
$writer.Write([UInt16]0); $writer.Write([UInt16]1); $writer.Write([UInt16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($image in $images) {
    $dimension = if ($image.Size -eq 256) { 0 } else { $image.Size }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
    $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([UInt16]1); $writer.Write([UInt16]32)
    $writer.Write([UInt32]$image.Bytes.Length); $writer.Write([UInt32]$offset)
    $offset += $image.Bytes.Length
}
foreach ($image in $images) { $writer.Write([byte[]]$image.Bytes) }
$writer.Dispose(); $file.Dispose()
