$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$asset = Join-Path $root 'assets'
$svg = [xml](Get-Content -LiteralPath (Join-Path $asset 'remsup.svg') -Raw)
$sizes = @(16,24,32,48,64,128,256)
$frames = @()
foreach ($size in $sizes) {
 $large = New-Object System.Drawing.Bitmap ($size * 4),($size * 4)
 $g = [System.Drawing.Graphics]::FromImage($large)
 try {
  $g.SmoothingMode = 'AntiAlias'
  $g.ScaleTransform(($size * 4 / 256.0),($size * 4 / 256.0))
  foreach ($rect in $svg.svg.rect) {
   $x=[float]$rect.x;$y=[float]$rect.y;$w=[float]$rect.width;$h=[float]$rect.height;$d=[float]$rect.rx*2
   $path=New-Object System.Drawing.Drawing2D.GraphicsPath
   try {
    $path.AddArc($x,$y,$d,$d,180,90);$path.AddArc(($x+$w-$d),$y,$d,$d,270,90)
    $path.AddArc(($x+$w-$d),($y+$h-$d),$d,$d,0,90);$path.AddArc($x,($y+$h-$d),$d,$d,90,90);$path.CloseFigure()
    if ($rect.fill -ne 'none') { $brush=New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml($rect.fill));try {$g.FillPath($brush,$path)}finally{$brush.Dispose()} }
    else { $pen=New-Object System.Drawing.Pen ([System.Drawing.ColorTranslator]::FromHtml($rect.stroke)),([float]$rect.'stroke-width');try {$g.DrawPath($pen,$path)}finally{$pen.Dispose()} }
   } finally {$path.Dispose()}
  }
  $bitmap=New-Object System.Drawing.Bitmap $size,$size
  $small=[System.Drawing.Graphics]::FromImage($bitmap)
  $stream=New-Object System.IO.MemoryStream
  try {
   $small.InterpolationMode='HighQualityBicubic';$small.DrawImage($large,0,0,$size,$size)
   $bitmap.Save($stream,[System.Drawing.Imaging.ImageFormat]::Png)
   if ($size -eq 256) { $frames += ,$stream.ToArray() }
   else {
    # Framework Icon.ToBitmap cannot reliably decode PNG-backed small frames.
    # Use native 32-bit DIB frames plus AND mask; keep the 256px frame as PNG.
    $dibStream=New-Object System.IO.MemoryStream
    try {
     $bitmap.Save($dibStream,[System.Drawing.Imaging.ImageFormat]::Bmp)
     $bmp=$dibStream.ToArray();$maskLength=[int]([Math]::Ceiling($size/32.0)*4*$size)
     $dib=New-Object byte[] ($bmp.Length-14+$maskLength)
     [Array]::Copy($bmp,14,$dib,0,$bmp.Length-14)
     [BitConverter]::GetBytes([int]($size*2)).CopyTo($dib,8)
     $frames += ,$dib
    } finally {$dibStream.Dispose()}
   }
   $bitmap.Save((Join-Path $asset ("remsup-"+$size+".png")),[System.Drawing.Imaging.ImageFormat]::Png)
  } finally {$stream.Dispose();$small.Dispose();$bitmap.Dispose()}
 } finally {$g.Dispose();$large.Dispose()}
}
$file=[System.IO.File]::Create((Join-Path $asset 'remsup.ico'))
$writer=New-Object System.IO.BinaryWriter $file
try {
 $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$sizes.Count)
 $offset=6+16*$sizes.Count
 for($i=0;$i -lt $sizes.Count;$i++) {
  $dimension=if($sizes[$i] -eq 256){0}else{$sizes[$i]}
  $writer.Write([byte]$dimension);$writer.Write([byte]$dimension);$writer.Write([byte]0);$writer.Write([byte]0)
  $writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$frames[$i].Length);$writer.Write([uint32]$offset)
  $offset+=$frames[$i].Length
 }
 foreach($frame in $frames){$writer.Write([byte[]]$frame)}
} finally {$writer.Dispose();$file.Dispose()}
Write-Output 'Built original Remsup ICO: 16/24/32/48/64/128/256'
