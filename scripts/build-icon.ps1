Add-Type -AssemblyName PresentationCore,PresentationFramework,WindowsBase
$resources=[Windows.Markup.XamlReader]::Load([Xml.XmlReader]::Create((Join-Path $PSScriptRoot '../src/Pathman.Gui/Styles/NavigationIcons.xaml')))
$icon=[IO.BinaryWriter]::new([IO.File]::Create((Join-Path $PSScriptRoot '../src/Pathman.Gui/Assets/PathMan.ico')))
$sizes=@(16,20,24,32,40,48,64,128,256)
$frames=foreach($size in $sizes){
 $visual=[Windows.Media.DrawingVisual]::new();$context=$visual.RenderOpen()
 $scale=$size/48.0;$context.PushTransform([Windows.Media.ScaleTransform]::new($scale,$scale))
 $background=[Windows.Media.LinearGradientBrush]::new([Windows.Media.ColorConverter]::ConvertFromString('#52B7FF'),[Windows.Media.ColorConverter]::ConvertFromString('#0860CB'),45)
 $context.DrawRoundedRectangle($background,$null,[Windows.Rect]::new(1,1,46,46),10,10)
 $context.DrawImage($resources['VariablesIcon'],[Windows.Rect]::new(9,11,30,28))
 $context.Pop();$context.Close()
 $bitmap=[Windows.Media.Imaging.RenderTargetBitmap]::new($size,$size,96,96,[Windows.Media.PixelFormats]::Pbgra32);$bitmap.Render($visual)
 $png=[Windows.Media.Imaging.PngBitmapEncoder]::new();$png.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
 $stream=[IO.MemoryStream]::new();$png.Save($stream)
 ,$stream.ToArray();$stream.Dispose()
}
try{
 $icon.Write([uint16]0);$icon.Write([uint16]1);$icon.Write([uint16]$sizes.Count)
 $offset=6+16*$sizes.Count
 for($index=0;$index -lt $sizes.Count;$index++){
  $side=if($sizes[$index] -eq 256){0}else{$sizes[$index]}
  $icon.Write([byte]$side);$icon.Write([byte]$side);$icon.Write([byte]0);$icon.Write([byte]0)
  $icon.Write([uint16]1);$icon.Write([uint16]32);$icon.Write([uint32]$frames[$index].Length);$icon.Write([uint32]$offset)
  $offset+=$frames[$index].Length
 }
 foreach($frame in $frames){$icon.Write([byte[]]$frame)}
}finally{$icon.Dispose()}