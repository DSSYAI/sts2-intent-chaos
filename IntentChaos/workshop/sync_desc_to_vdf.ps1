# 把 Description.md（工坊说明的权威源文件）灌进 IntentChaos.vdf 的 description 字段。
# 用法：改完 Description.md 后
#   pwsh -NoProfile -ExecutionPolicy Bypass -File IntentChaos\workshop\sync_desc_to_vdf.ps1
# 然后再跑 sync_workshop.ps1 自检 + SteamCMD 上传。
#
# ⚠️ 编码：本脚本只用 [IO.File]::ReadAllText/WriteAllText + UTF8Encoding($false)。
#    绝不能用 Get-Content/Set-Content —— PS 5.1 会按 GBK 读 UTF-8，中文写回去就不可逆地烂掉。
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$enc = New-Object Text.UTF8Encoding($false)
$root = $PSScriptRoot
$descMd = Join-Path (Split-Path -Parent (Split-Path -Parent $root)) 'Description.md'   # 工作区根/Description.md
$vdf = Join-Path $root 'IntentChaos.vdf'
if (-not (Test-Path $descMd)) { Write-Host "[FAIL] 找不到 $descMd"; exit 1 }

$md = [IO.File]::ReadAllText($descMd, [Text.Encoding]::UTF8)
# 统一换行、去掉每行行尾空白与文件末尾多余空行
$md = $md -replace "`r`n", "`n"
$md = $md.Trim("`n")

# VDF 字符串内：反斜杠、双引号要转义；换行写成字面量 \n
$escaped = $md.Replace('\', '\\').Replace('"', '\"').Replace("`n", '\n')

$vdfText = [IO.File]::ReadAllText($vdf, [Text.Encoding]::UTF8)
# ⚠️ 整行替换，绝对不要用 `[^"]*` 去"找闭合引号"：旧值里只要含 \" 转义引号，就会在第一个
#    转义引号处提前截断，把上一版文案的尾巴留在行尾（本脚本 v0.3.1 就这么把 VDF 从 9212 撑到 10475 字）。
$lineRx = [regex]'(?m)^([ \t]*)("description"[ \t]*").*$'
if (-not $lineRx.IsMatch($vdfText)) { Write-Host '[FAIL] VDF 里没找到 description 行（格式变了？别硬改，手工检查文件）'; exit 1 }
$build = { param($mm) $mm.Groups[1].Value + $mm.Groups[2].Value + $escaped + '"' }
$newVdf = $lineRx.Replace($vdfText, [System.Text.RegularExpressions.MatchEvaluator]$build)
[IO.File]::WriteAllText($vdf, $newVdf, $enc)

# ---- 自检：回读并还原成明文，必须与 Description.md 完全一致 ----
$fail = @()
$back = [IO.File]::ReadAllText($vdf, [Text.Encoding]::UTF8)
$cap = [regex]::Match($back, '"description"[ \t]*"((?:[^"\\]|\\.)*)"').Groups[1].Value
# 闭引号后必须正好是行尾，否则说明上一版有残留
$dline = [regex]::Match($back, '(?m)^[ \t]*"description".*$').Value
if ($dline.Length -eq 0) { $fail += '找不到 description 行' }
elseif (-not [regex]::IsMatch($dline, '^([ \t]*"description"[ \t]*")((?:[^"\\]|\\.)*)("$)')) {
    $fail += ('description 行闭引号之后还有内容（残留 ' + ($dline.Length - [regex]::Match($dline,'^((?:[ \t]*"description"[ \t]*")((?:[^"\\]|\\.)*)(")').Length) - '1 字）')
}
if ($cap.Length -eq 0) { $fail += '回读 description 失败' }
else {
    # 反转义要单次从左到右扫：\\ -> \ 、\" -> " 、\n -> 换行。
    # 分次 Replace 会串味（先把 \\ 换成 \ 就把 \" 的前缀吃了），
    # 而且在 PS 单引号里写 '\\n' 其实是"两个反斜杠+n"，永远匹配不到 VDF 里的 \n（本脚本第一版就栽在这）
    $unescape = { param($m)
        $c = $m.Groups['c'].Value
        if ($c -eq 'n') { "`n" } else { $c }
    }
    $plain = [regex]::Replace($cap, '\\(?<c>[\\n"])', [System.Text.RegularExpressions.MatchEvaluator]$unescape)
    if ($plain -ne $md) { $fail += ('还原后与 Description.md 不一致（明文 {0} 字 vs 还原 {1} 字）' -f $md.Length, $plain.Length) }
}
$b = [IO.File]::ReadAllBytes($vdf)
if ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF) { $fail += 'VDF 被写上了 BOM' }
if ($back.IndexOf([char]0xFFFD) -ge 0) { $fail += 'VDF 含替换符 U+FFFD' }
foreach ($mk in @('鎰忓浘', '涔辨祦', '鏍稿績', '鏈哄埗', '鈥?', '锟斤拷')) { if ($back.Contains($mk)) { $fail += "VDF 命中乱码特征串 [$mk]"; break } }
foreach ($need in @('[h1]Intent Chaos 意图乱流[/h1]', '[h1]意图乱流 Intent Chaos[/h1]', '[/list]')) {
    if (-not $back.Contains($need)) { $fail += "VDF 缺少应有片段 [$need]" }
}
# description 必须是单行（换行只能以字面量 \n 存在）
$descLine = ([regex]::Matches($back, '(?m)^.*"description".*$')).Count
if ($descLine -ne 1) { $fail += "description 不是单行（找到 $descLine 行）" }

$secs = ($md -split "`n" | Where-Object { $_ -match '^\[h1\]|^\[h2\]' }).Count
"Description.md -> VDF 灌入完成：{0} 段标题，明文 {1} 字，VDF {2} 字节" -f $secs, $md.Length, $b.Length
if ($fail.Count -gt 0) {
    Write-Host '[FAIL] 自检未通过：'
    $fail | ForEach-Object { Write-Host ('  - ' + $_) }
    exit 1
}
Write-Host '[OK] 自检通过：VDF 内 description 与 Description.md 逐字一致，无 BOM、无乱码、单行存储'
Write-Host ''
Write-Host '下一步：'
Write-Host ('  pwsh -NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $root 'sync_workshop.ps1') + '"')
