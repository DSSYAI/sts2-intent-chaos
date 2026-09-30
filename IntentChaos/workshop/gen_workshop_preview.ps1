# 生成 IntentChaos 创意工坊封面图 preview.jpg（1024x576）
# 需要 UTF-8 BOM 运行：powershell -File gen_workshop_preview.ps1
[Console]::OutputEncoding = [Text.Encoding]::UTF8
Add-Type -AssemblyName System.Drawing

$out = Join-Path $PSScriptRoot 'preview.jpg'
$w = 1024; $h = 576
$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'
$g.TextRenderingHint = 'AntiAliasGridFit'

# 背景：深紫黑对角渐变
$rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
$brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    $rect,
    [System.Drawing.Color]::FromArgb(255, 24, 16, 34),
    [System.Drawing.Color]::FromArgb(255, 74, 44, 110),
    [System.Drawing.Drawing2D.LinearGradientMode]::ForwardDiagonal)
$g.FillRectangle($brush, $rect)

# 随机"乱流"斜线（紫白半透明，象征意图乱转）
$rng = New-Object System.Random(42)
$penThin = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(70, 200, 170, 255), 2)
for ($i = 0; $i -lt 46; $i++) {
    $x1 = $rng.Next(0, $w); $y1 = $rng.Next(0, $h)
    $len = $rng.Next(60, 220)
    $dx = if ($rng.Next(2) -eq 0) { $len } else { -$len }
    $dy = [int]($len * ($rng.NextDouble() - 0.35))
    $g.DrawLine($penThin, $x1, $y1, ($x1 + $dx), ($y1 + $dy))
}

# 中央意图气泡轮廓（问号元素）
$penBubble = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(230, 205, 160, 255), 5)
$g.DrawEllipse($penBubble, ($w/2 - 150), 70, 300, 190)
$g.DrawLine($penBubble, ($w/2 - 40), 258, ($w/2 + 10), 330)
$g.DrawLine($penBubble, ($w/2 + 10), 330, ($w/2 - 30), 340)
$fontBig = New-Object System.Drawing.Font('Microsoft YaHei', 44, [System.Drawing.FontStyle]::Bold)
$fontMid = New-Object System.Drawing.Font('Microsoft YaHei', 24, [System.Drawing.FontStyle]::Bold)
$fontSm  = New-Object System.Drawing.Font('Microsoft YaHei', 17, [System.Drawing.FontStyle]::Regular)
$white = [System.Drawing.Brushes]::White
$fmtC = New-Object System.Drawing.StringFormat
$fmtC.Alignment = 'Center'
$g.DrawString('?!', $fontBig, $white, ($w/2), 110, $fmtC)

# 标题与副标题
$title = '意图乱流  Intent Chaos'
$g.DrawString($title, $fontMid, $white, ($w/2), 366, $fmtC)
$sub = '怪物被你打掉血后，意图立刻随机换成其他招式'
$brushSub = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(225, 220, 205, 245))
$g.DrawString($sub, $fontSm, $brushSub, ($w/2), 428, $fmtC)
$sub2 = 'Monsters reroll their intent when hurt by your attacks'
$brushSub2 = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(160, 190, 170, 220))
$g.DrawString($sub2, $fontSm, $brushSub2, ($w/2), 468, $fmtC)
$brushFoot = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(140, 160, 140, 190))
$g.DrawString('Slay the Spire 2 Workshop Mod', $fontSm, $brushFoot, ($w/2), 522, $fmtC)

$g.Dispose()
$codec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq 'image/jpeg' }
$encParams = New-Object System.Drawing.Imaging.EncoderParameters(1)
$encParams.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter([System.Drawing.Imaging.Encoder]::Quality, [long]92)
$bmp.Save($out, $codec, $encParams)
$bmp.Dispose()
Write-Host "saved: $out"

