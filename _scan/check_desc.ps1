# 交付前自检：Description.md（工坊文案成品）+ VDF 一致性 + 文案承诺的 config 键真实存在
# 用法：powershell -File check_desc.ps1 [-Root <仓库根>]
param(
    [string]$Root = ""
)
if (-not $Root) { $Root = Split-Path -Parent $PSScriptRoot }   # 默认 = 仓库根
$root = $Root

$d = [IO.File]::ReadAllText((Join-Path $root 'Description.md'), [Text.Encoding]::UTF8)
$db = [IO.File]::ReadAllBytes((Join-Path $root 'Description.md'))

'=== Description.md ==='
'  UTF-8 无 BOM = {0}   U+FFFD = {1}   行数 = {2}   字节 = {3}' -f `
    (-not ($db[0] -eq 0xEF)), ([regex]::Matches($d, [string][char]0xFFFD)).Count, ($d -split "`n").Count, $db.Length
'  [h1] {0}/[/h1] {1} ; [h2] {2}/[/h2] {3} ; [list] {4}/[/list] {5} ; [b] {6}/[/b] {7}' -f `
    ([regex]::Matches($d, '\[h1\]')).Count, ([regex]::Matches($d, '\[/h1\]')).Count,
    ([regex]::Matches($d, '\[h2\]')).Count, ([regex]::Matches($d, '\[/h2\]')).Count,
    ([regex]::Matches($d, '\[list\]')).Count, ([regex]::Matches($d, '\[/list\]')).Count,
    ([regex]::Matches($d, '\[b\]')).Count, ([regex]::Matches($d, '\[/b\]')).Count
'  裸双引号个数（灌进 VDF 前必须为 0，脚本会转义但人眼看更好） = ' + ([regex]::Matches($d, '"')).Count
'  中英两段都在 = ' + ($d.Contains('[h1]Intent Chaos 意图乱流[/h1]') -and $d.Contains('[h1]意图乱流 Intent Chaos[/h1]'))
'  已提到 v0.3.1 = ' + $d.Contains('v0.3.0 / v0.3.1')

''
'=== 文案里承诺的 config 键，ModConfig.cs 里真的有吗？ ==='
$src = [IO.File]::ReadAllText((Join-Path $root 'IntentChaos\src\ModConfig.cs'), [Text.Encoding]::UTF8)
$known = @('blacklistDangerousMoves','pinBlacklistedMoves','pinStructuralMoves','repairStrandedDeathBlows',
    'excludeMoveIds','queenFixedOpeningMoves','queenFixedMoveIds','bossCurseMove','bossCurseCardId',
    'curseIntentIcon','curseIntentTitle','curseIntentDescription','logRerolls','chancePercent','maxRerollAttempts')
$miss = 0
foreach ($k in ($known | Where-Object { $d.Contains($_) } | Sort-Object -Unique)) {
    $has = $src -match "(?m)public\s+[^\s]+\s+$k\s"
    if (-not $has) { $miss++ }
    '  {0,-28} ModConfig 有该字段 = {1}' -f $k, $has
}
if ($miss -eq 0) { '  [OK] 文案没有承诺不存在的开关' } else { "  [BAD] $miss 个键不存在（别写进玩家说明）" }

''
'=== VDF ==='
$v = [IO.File]::ReadAllText((Join-Path $root 'IntentChaos\workshop\IntentChaos.vdf'), [Text.Encoding]::UTF8)
'  键 = ' + (([regex]::Matches($v, '(?m)^\s*"([a-z]+)"') | ForEach-Object { $_.Groups[1].Value }) -join ', ')
$cap = [regex]::Match($v, '"description"[ \t]*"((?:[^"\\]|\\.)*)"').Groups[1].Value
$un = [regex]::Replace($cap, '\\(?<c>[\\n"])', { param($m) if ($m.Groups['c'].Value -eq 'n') { "`n" } else { $m.Groups['c'].Value } })
'  VDF description 与 Description.md 逐字一致 = ' + ($un -eq $d.Trim("`n"))
'  publishedfileid = ' + [regex]::Match($v, '"publishedfileid"[ \t]*"([^"]*)"').Groups[1].Value
$cf = [regex]::Match($v, '"contentfolder"[ \t]*"([^"]*)"').Groups[1].Value.Replace('<REPO>', $root).Replace('/', '\')
$pf = [regex]::Match($v, '"previewfile"[ \t]*"([^"]*)"').Groups[1].Value.Replace('<REPO>', $root).Replace('/', '\')
'  contentfolder 存在 = ' + (Test-Path $cf) + "  ($cf)"
'  previewfile  存在 = ' + (Test-Path $pf) + "  ($pf)"
'  花括号配对 = ' + (([regex]::Matches($v, '\{')).Count -eq ([regex]::Matches($v, '\}')).Count)
'  visibility = ' + [regex]::Match($v, '"visibility"[ \t]*"([^"]*)"').Groups[1].Value + '   (0 公开 / 1 仅好友 / 2 私有)'
'  changenote 前 40 字 = ' + [regex]::Match($v, '"changenote"[ \t]*"([^"]*)"').Groups[1].Value.Substring(0, 40) + '…'
'  VDF 里除 description 外是否还有裸换行撑爆的行 = ' + (([regex]::Matches($v, '(?m)^.{0,400}?[^"\\]$')).Count -le 20)
