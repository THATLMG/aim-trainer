# Draws the logo and game badges used by the Discord status and the website (docs/rpc/).
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot 'docs\rpc'
New-Item -ItemType Directory -Force -Path $out | Out-Null

function RoundRect($g, $brush, $x, $y, $w, $h, $r) {
    $p = New-Object Drawing.Drawing2D.GraphicsPath
    $p.AddArc($x, $y, $r * 2, $r * 2, 180, 90); $p.AddArc($x + $w - $r * 2, $y, $r * 2, $r * 2, 270, 90)
    $p.AddArc($x + $w - $r * 2, $y + $h - $r * 2, $r * 2, $r * 2, 0, 90); $p.AddArc($x, $y + $h - $r * 2, $r * 2, $r * 2, 90, 90)
    $p.CloseFigure(); $g.FillPath($brush, $p); $p.Dispose()
}

# ----- logo: crosshair over a bot, "LMG" underneath -----
$S = 512
$bmp = New-Object Drawing.Bitmap $S, $S
$g = [Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'; $g.Clear([Drawing.Color]::Transparent)
$bg = New-Object Drawing.Drawing2D.LinearGradientBrush (New-Object Drawing.Point 0, 0), (New-Object Drawing.Point 0, $S), ([Drawing.Color]::FromArgb(22, 28, 40)), ([Drawing.Color]::FromArgb(8, 10, 15))
RoundRect $g $bg 0 0 $S $S 96
$cyan = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(25, 230, 232))
$g.FillEllipse($cyan, 216, 118, 80, 80)                    # bot head
RoundRect $g $cyan 196 206 120 170 60                       # bot body
$red = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(255, 70, 85)), 22
$g.DrawEllipse($red, 136, 78, 240, 240)                    # ring around the head
$white = New-Object Drawing.Pen ([Drawing.Color]::White), 16
$white.StartCap = 'Round'; $white.EndCap = 'Round'
$g.DrawLine($white, 256, 46, 256, 112); $g.DrawLine($white, 256, 204, 256, 250)
$g.DrawLine($white, 104, 158, 196, 158); $g.DrawLine($white, 316, 158, 408, 158)
$font = New-Object Drawing.Font 'Bahnschrift', 92, ([Drawing.FontStyle]::Bold), ([Drawing.GraphicsUnit]::Pixel)
$sf = New-Object Drawing.StringFormat; $sf.Alignment = 'Center'
$g.DrawString('LMG', $font, [Drawing.Brushes]::White, (New-Object Drawing.RectangleF 0, 382, $S, 110), $sf)
$bmp.Save((Join-Path $out 'logo.png'), [Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()

# ----- game badges -----
$games = @(
    @('valorant', 'VAL', 255, 84, 98), @('cs2', 'CS2', 240, 162, 59), @('fortnite', 'FN', 166, 102, 255), @('apex', 'APX', 222, 84, 68),
    @('overwatch', 'OW2', 249, 158, 26), @('minecraft', 'MC', 108, 178, 60), @('roblox', 'RBX', 40, 160, 255), @('custom', 'CUS', 60, 196, 176)
)
foreach ($x in $games) {
    $b = New-Object Drawing.Bitmap 128, 128
    $g = [Drawing.Graphics]::FromImage($b)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'; $g.Clear([Drawing.Color]::Transparent)
    $g.FillEllipse((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb($x[2], $x[3], $x[4]))), 2, 2, 124, 124)
    $f = New-Object Drawing.Font 'Bahnschrift', ($(if ($x[1].Length -gt 2) { 40 } else { 50 })), ([Drawing.FontStyle]::Bold), ([Drawing.GraphicsUnit]::Pixel)
    $sf2 = New-Object Drawing.StringFormat; $sf2.Alignment = 'Center'; $sf2.LineAlignment = 'Center'
    $g.DrawString($x[1], $f, [Drawing.Brushes]::White, (New-Object Drawing.RectangleF 0, 2, 128, 128), $sf2)
    $b.Save((Join-Path $out ($x[0] + '.png')), [Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $b.Dispose()
}
'Images written to docs\rpc'
