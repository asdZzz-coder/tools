# 產生程式圖示 AppIcon.ico(多尺寸)與 AppIcon.png(256px)。
# 用 Windows PowerShell 5.1 執行(需要 WPF):powershell -File ZZZ\Assets\make-icon.ps1 [-Preview 預覽圖.png]
# 圖案:深色圓角底(同 Themes.xaml 的 HeroBrush)+ 白色帳頁,收入綠、支出紅兩列,右下角一枚綠色錢幣。
param([string]$Preview)

Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

function Brush([string]$hex) { [Windows.Media.SolidColorBrush]::new([Windows.Media.ColorConverter]::ConvertFromString($hex)) }

$ink    = [Windows.Media.LinearGradientBrush]::new(
            [Windows.Media.ColorConverter]::ConvertFromString('#2B3545'),
            [Windows.Media.ColorConverter]::ConvertFromString('#111827'), [Windows.Point]::new(0,0), [Windows.Point]::new(1,1))
$paper  = Brush '#FFFFFF'
$line   = Brush '#D5D9E0'
$inc    = Brush '#0E9F6E'
$exp    = Brush '#E5484D'
$cut    = Brush '#151C29'   # 錢幣外圈:和底色相近,把錢幣跟帳頁切開

# 以 256×256 的座標繪製;$size 是輸出像素,小尺寸省略細節以免糊成一團
function Draw([int]$size) {
    $dv = [Windows.Media.DrawingVisual]::new()
    $dc = $dv.RenderOpen()
    $s = $size / 256.0
    $dc.PushTransform([Windows.Media.ScaleTransform]::new($s, $s))
    $small = $size -lt 32

    # 底
    $dc.DrawRoundedRectangle($ink, $null, [Windows.Rect]::new(8, 8, 240, 240), 56, 56)

    # 帳頁
    $dc.DrawRoundedRectangle($paper, $null, [Windows.Rect]::new(52, 40, 124, 164), 20, 20)
    if ($small) {
        # 小圖示只留兩條色帶
        $dc.DrawRoundedRectangle($inc, $null, [Windows.Rect]::new(74, 70, 80, 22), 11, 11)
        $dc.DrawRoundedRectangle($exp, $null, [Windows.Rect]::new(74, 108, 56, 22), 11, 11)
    } else {
        # 收入、支出、一般三列:左邊圓點,右邊金額線
        $rows = @(@(76, $inc, 72), @(112, $exp, 60), @(148, $line, 36))
        foreach ($r in $rows) {
            $dc.DrawEllipse($r[1], $null, [Windows.Point]::new(82, $r[0]), 10, 10)
            $dc.DrawRoundedRectangle($line, $null, [Windows.Rect]::new(102, $r[0] - 7, $r[2], 14), 7, 7)
        }
    }

    # 錢幣
    $c = [Windows.Point]::new(180, 180)
    $dc.DrawEllipse($cut, $null, $c, 56, 56)
    $dc.DrawEllipse($inc, $null, $c, 46, 46)
    if (-not $small) { $dc.DrawEllipse($null, [Windows.Media.Pen]::new((Brush '#3DBE8F'), 5), $c, 36, 36) }

    # 錢幣上的 $(24px 以下只剩幾個像素,畫了反而髒)
    if ($size -lt 24) { $dc.Pop(); $dc.Close(); return Render $dv $size }
    $tf = [Windows.Media.Typeface]::new([Windows.Media.FontFamily]::new('Segoe UI'), [Windows.FontStyles]::Normal,
                                        [Windows.FontWeights]::Bold, [Windows.FontStretches]::Normal)
    $ft = [Windows.Media.FormattedText]::new('$', [Globalization.CultureInfo]::InvariantCulture,
                                             [Windows.FlowDirection]::LeftToRight, $tf, 64, $paper, 1.0)
    $g = $ft.BuildGeometry([Windows.Point]::new(0, 0))
    $b = $g.Bounds
    $g.Transform = [Windows.Media.TranslateTransform]::new($c.X - $b.X - $b.Width / 2, $c.Y - $b.Y - $b.Height / 2)
    $dc.DrawGeometry($paper, $null, $g)

    $dc.Pop()
    $dc.Close()
    Render $dv $size
}

function Render($visual, [int]$size) {
    $bmp = [Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($visual)
    $bmp
}

function PngBytes($bmp) {
    $enc = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = [IO.MemoryStream]::new()
    $enc.Save($ms)
    , $ms.ToArray()
}

# .ico:每個尺寸存成 PNG(Windows Vista 以後都支援)
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($sz in $sizes) { , (PngBytes (Draw $sz)) }
$ms = [IO.MemoryStream]::new()
$w = [IO.BinaryWriter]::new($ms)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $d = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$d); $w.Write([byte]$d); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Flush()
[IO.File]::WriteAllBytes((Join-Path $here 'AppIcon.ico'), $ms.ToArray())
[IO.File]::WriteAllBytes((Join-Path $here 'AppIcon.png'), $images[-1])
"AppIcon.ico / AppIcon.png 已輸出到 $here"

# 預覽:各尺寸並排(淺色與深色背景各一列)
if ($Preview) {
    $show = 256, 128, 64, 48, 32, 24, 16
    $width = ($show | Measure-Object -Sum).Sum + 24 * ($show.Count + 1)
    $dv = [Windows.Media.DrawingVisual]::new()
    $dc = $dv.RenderOpen()
    $dc.DrawRectangle((Brush '#F4F5F7'), $null, [Windows.Rect]::new(0, 0, $width, 304))
    $dc.DrawRectangle((Brush '#202020'), $null, [Windows.Rect]::new(0, 304, $width, 304))
    foreach ($row in 0, 304) {
        $x = 24
        foreach ($sz in $show) {
            $dc.DrawImage((Draw $sz), [Windows.Rect]::new($x, $row + 24 + (256 - $sz), $sz, $sz))
            $x += $sz + 24
        }
    }
    $dc.Close()
    $bmp = [Windows.Media.Imaging.RenderTargetBitmap]::new([int]$width, 608, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($dv)
    [IO.File]::WriteAllBytes($Preview, (PngBytes $bmp))
    "預覽:$Preview"
}
