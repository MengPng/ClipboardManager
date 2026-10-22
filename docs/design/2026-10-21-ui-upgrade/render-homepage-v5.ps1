param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase

$designDir = $PSScriptRoot
$xamlPath = Join-Path $designDir 'homepage-v5.xaml'
$outputPath = Join-Path $designDir 'homepage-v5.png'
$iconPath = (Join-Path $Root 'src\ClipboardManager.Windows\Assets\AppIcon.png').Replace('\', '/')

$rows = @(
    @('产品界面参考.png', '图片  4096 × 3072', '3 分钟前'),
    @('中国传统配色', 'colors.xiaoxiaodong.ai', '8 分钟前'),
    @('本周工作记录.xlsx', '文件  路径引用', '12 分钟前'),
    @('先把重要的事情做好。', '可以随时从历史里找回', '18 分钟前')
)

$rowXaml = for ($i = 0; $i -lt $rows.Count; $i++) {
    $border = if ($i -lt $rows.Count - 1) { '0,0,0,1' } else { '0' }
    $title = [Security.SecurityElement]::Escape($rows[$i][0])
    $preview = [Security.SecurityElement]::Escape($rows[$i][1])
    $time = [Security.SecurityElement]::Escape($rows[$i][2])
    @"
<Border Height="56" Margin="4,0" Padding="8,9,8,0" BorderBrush="#FFE6E9EE" BorderThickness="$border">
    <StackPanel>
        <Grid>
            <TextBlock Margin="0,0,72,0" Text="$title" TextTrimming="CharacterEllipsis" FontFamily="Microsoft YaHei UI" FontSize="13" FontWeight="Bold" Foreground="#FF1F2329" />
            <TextBlock HorizontalAlignment="Right" Text="$time" FontFamily="Microsoft YaHei UI" FontSize="10.5" Foreground="#FF6B7380" />
        </Grid>
        <TextBlock Margin="0,4,0,0" Text="$preview" TextTrimming="CharacterEllipsis" FontFamily="Microsoft YaHei UI" FontSize="12" Foreground="#FF4F5866" />
    </StackPanel>
</Border>
"@
}

$xaml = [IO.File]::ReadAllText($xamlPath).Replace('__ICON__', "file:///$iconPath").Replace('__ROWS__', ($rowXaml -join [Environment]::NewLine))
$surface = [Windows.Markup.XamlReader]::Parse($xaml)
$surface.Measure([Windows.Size]::new(340, 540))
$surface.Arrange([Windows.Rect]::new(0, 0, 340, 540))
$surface.UpdateLayout()

$bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new(680, 1080, 192, 192, [Windows.Media.PixelFormats]::Pbgra32)
$bitmap.Render($surface)
$encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
$encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
$stream = [IO.File]::Create($outputPath)
try { $encoder.Save($stream) } finally { $stream.Dispose() }

[pscustomobject]@{
    output = $outputPath
    logicalSize = '340x540'
    pixels = '680x1080'
    renderer = 'WPF RenderTargetBitmap'
} | ConvertTo-Json -Compress
