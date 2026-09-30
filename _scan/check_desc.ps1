# 交付前自检：Description.md（工坊文案成品）+ VDF 一致性 + 文案承诺的 config 键真实存在
# 用法：pwsh -File check_desc.ps1 [-Root <仓库根>]
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

''
'=== 玩家文案里不许出现 config 键名（AGENTS §2）==='
$v = [IO.File]::ReadAllText((Join-Path $root 'IntentChaos\workshop\IntentChaos.vdf'), [Text.Encoding]::UTF8)
# 键名清单直接从 ModConfig.cs 现取，避免这里手写一份过期名单
$src = [IO.File]::ReadAllText((Join-Path $root 'IntentChaos\src\ModConfig.cs'), [Text.Encoding]::UTF8)
$known = @([regex]::Matches($src, '(?m)^\s*public\s+(?:bool|int|string|List<string>)\s+(\w+)\s*\{\s*get') |
    ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
'  从 ModConfig.cs 读到的键（{0} 个）：{1}' -f $known.Count, ($known -join ' ')
if ($known.Count -lt 5) { '  [BAD] 几乎没读到键，正则漂了，别信下面的结论' }
# 文案 = Description.md + VDF 的 changenote（更新日志也是给玩家看的）
$note = [regex]::Match($v, '(?m)^[ \t]*"changenote"[ \t]*"(.*)"$').Groups[1].Value
$hit = @($known | Where-Object { $d.Contains($_) -or $note.Contains($_) })
if ($hit.Count -eq 0) { '  [OK] 文案与更新日志里都没有键名' } else { '  [BAD] 出现了键名：' + ($hit -join ', ') }
# 顺带：文案不该教人"关掉某个保护会更疯"
foreach ($phrase in @('关掉', '关闭该', '想要更疯', '在 config 里', 'disable the')) {
    if ($d.Contains($phrase) -or $note.Contains($phrase)) { '  [WARN] 出现操作指引措辞：' + $phrase }
}

''
'=== VDF ==='
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
'  changenote = ' + $(if ($note.Length -gt 40) { $note.Substring(0, 40) + '…（共 ' + $note.Length + ' 字）' } else { $note }) + ''
# 合法的 VDF 行只以 " / { / } 结尾；出现别的收尾说明某行被截断或写坏了
$badLines = @((($v -split "`r?`n") | Where-Object { $_ -ne '' -and $_ -notmatch '[}"{]\s*$' -and $_ -notmatch '^\s*//' }))
'  结构异常的行（应当为 0）= ' + $badLines.Count
$badLines | Select-Object -First 3 | ForEach-Object { '    → ' + $_.Substring(0, [Math]::Min(90, $_.Length)) }

''
'=== 仓库泄漏面（本机路径 / 反编译派生物）==='
$leak = 0
if ($v -match '(?m)"contentfolder"[ \t]*"<REPO>/') { '  [OK] 模板 VDF 的 contentfolder 是 <REPO> 占位符' }
else { '  [BAD] 模板 VDF 的 contentfolder 不是占位符 —— 提交就把本机路径推上公开仓库'; $leak++ }
if ($v -match '[A-Za-z]:[/\\](Users|agent|SteamLibrary|dev)[/\\]') { '  [BAD] 模板 VDF 里出现本机绝对路径'; $leak++ }
$up = Join-Path $root 'IntentChaos\workshop\IntentChaos.upload.vdf'
if (Test-Path $up) {
    '  上传副本存在（生成物，带本机路径）= True'
    if (Get-Command git -ErrorAction SilentlyContinue) {
        Push-Location $root
        git check-ignore -q -- 'IntentChaos/workshop/IntentChaos.upload.vdf' 2>$null | Out-Null
        $ig = ($LASTEXITCODE -eq 0)
        Pop-Location
        if ($ig) { '  [OK] 它被 .gitignore 忽略' } else { '  [BAD] 它没被 .gitignore 忽略'; $leak++ }
    }
} else { '  上传副本还没生成（跑一次 sync_workshop.ps1 即可）' }
if (Get-Command git -ErrorAction SilentlyContinue) {
    Push-Location $root
    $tracked = @(git ls-files '_scan' 2>$null)
    $dirty = @(git ls-files 2>$null | Where-Object { $_ -like '*.csv' })
    Pop-Location
    '  _scan 下被跟踪的文件 = ' + ($tracked -join ', ')
    if ($dirty.Count -gt 0) { '  [BAD] 被跟踪的 csv（反编译派生数据集，不该公开）：' + ($dirty -join ', '); $leak++ }
    else { '  [OK] 没有任何扫描产物 csv 被跟踪' }
}
if ($leak -eq 0) { '  [OK] 泄漏面干净' } else { "  共 $leak 处要处理" }
