# Convert the transparent artwork into the standard Windows icon sizes.
# This only resizes/encodes the existing illustration; it does not redraw it.
param()
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$assetDirectory=Join-Path (Split-Path -Parent $PSScriptRoot) 'src\MadokaChanger.App\Assets'
$source=[System.Drawing.Image]::FromFile((Join-Path $assetDirectory 'MadokaChibi.png'))
try {
    $frames=@()
    foreach($size in @(16,20,24,32,40,48,64,96,128,256)) {
        $bitmap=[System.Drawing.Bitmap]::new($size,$size,[System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics=[System.Drawing.Graphics]::FromImage($bitmap)
        $stream=[System.IO.MemoryStream]::new()
        try {
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.CompositingMode=[System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.InterpolationMode=[System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode=[System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.DrawImage($source,[System.Drawing.Rectangle]::new(0,0,$size,$size))
            $bitmap.Save($stream,[System.Drawing.Imaging.ImageFormat]::Png)
            $frames+=[pscustomobject]@{size=$size;bytes=$stream.ToArray()}
        }
        finally {$graphics.Dispose();$bitmap.Dispose();$stream.Dispose()}
    }
    $output=[System.IO.File]::Create((Join-Path $assetDirectory 'MadokaChanger.ico'))
    $writer=[System.IO.BinaryWriter]::new($output)
    try {
        $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$frames.Count)
        $offset=6+16*$frames.Count
        foreach($frame in $frames) {
            $dimension=if($frame.size -eq 256){0}else{$frame.size}
            $writer.Write([byte]$dimension);$writer.Write([byte]$dimension)
            $writer.Write([byte]0);$writer.Write([byte]0)
            $writer.Write([uint16]1);$writer.Write([uint16]32)
            $writer.Write([uint32]$frame.bytes.Length);$writer.Write([uint32]$offset)
            $offset+=$frame.bytes.Length
        }
        foreach($frame in $frames){$writer.Write([byte[]]$frame.bytes)}
    }
    finally {$writer.Dispose();$output.Dispose()}
}
finally {$source.Dispose()}
Write-Output 'MadokaChanger.ico contains 10 transparent icon sizes, from 16 to 256 pixels.'
