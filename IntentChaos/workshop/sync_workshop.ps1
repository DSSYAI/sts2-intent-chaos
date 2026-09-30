# 把最新构建的 mod 文件同步到工坊上传目录（workshop/content/IntentChaos）。
# 用法：先 dotnet build IntentChaos 工程，再运行本脚本；之后用 SteamCMD 重新上传即可更新同一个工坊物品。
# powershell -NoProfile -ExecutionPolicy Bypass -File sync_workshop.ps1 [-Version v0.3.0]
#
# ⚠️ 编码铁律：这里的 json / vdf 必须是 **UTF-8 无 BOM**，而 Windows PowerShell 5.1 的
# Get-Content / Set-Content 默认按 ANSI(GBK) 读写。用它改写含中文的文件会把中文变成不可逆乱码
# （v0.3.0 同步时真踩过：破折号 U+2014 被吃成 '?'，整段中文全废）。所以一律用
# [IO.File]::ReadAllText / WriteAllText + UTF8Encoding($false)，禁止用 *-Content 改写文本文件。
param(
    [string]$Version = "",
    [string]$GameDir = "",
    [string]$SteamCmd = ""
)
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$Utf8NoBom = New-Object Text.UTF8Encoding($false)
function Read-Utf8([string]$p) { [IO.File]::ReadAllText($p, $Utf8NoBom) }
function Write-Utf8([string]$p, [string]$t) { [IO.File]::WriteAllText($p, $t, $Utf8NoBom) }

# GBK 误读 UTF-8 后写回 UTF-8 的固定产物（"意图乱流"→"鎰忓浘涔辨祦" 等）。
# 只列这次事故实测出现过的串，避免把正常中文误判成乱码。
# ⚠️ 别往这里写字面量替换符 U+FFFD：它会落地成空串，Contains("") 恒真 → 全部误报（实测踩过）。
#    替换符单独用 [char]0xFFFD 判断。
$MojibakeMarkers = @('鎰忓浘', '涔辨祦', '鏍稿績', '鏈哄埗', '鎷涘紡', '闅忔満', '鍏煎鎬', '鐗╁', '鈥?', '锟斤拷') |
    Where-Object { -not [string]::IsNullOrEmpty($_) }

$root = Split-Path -Parent $PSScriptRoot                     # IntentChaos/
$repoRoot = Split-Path -Parent $root                         # 仓库根
$content = Join-Path $PSScriptRoot 'content\IntentChaos'
$vdf = Join-Path $PSScriptRoot 'IntentChaos.vdf'
$srcJson = Join-Path $root 'IntentChaos.json'                # 清单权威来源（版本号以它为准，不回写）
$builtDll = Join-Path $root 'src\bin\Debug\IntentChaos.dll'

# 游戏目录：-GameDir > IntentChaos\local.props 的 <GameDir>（local.props 不入库）
if (-not $GameDir) {
    $props = Join-Path $root 'local.props'
    if (Test-Path $props) {
        $m = [regex]::Match((Read-Utf8 $props), '<GameDir>\s*([^<]+?)\s*</GameDir>')
        if ($m.Success) { $GameDir = $m.Groups[1].Value }
    }
}
if (-not $GameDir) {
    throw "未配置游戏目录：请把 IntentChaos\local.props.example 复制为 local.props 并填好 <GameDir>，或用 -GameDir 指定"
}
$deployed = Join-Path $GameDir 'mods\IntentChaos'

New-Item -ItemType Directory -Force -Path $content | Out-Null

# 只带 json + dll：config.json 由玩家首次运行自动生成，pdb 不发布。
# Copy-Item 是字节级复制，不改编码，安全。
Copy-Item -Force $srcJson (Join-Path $content 'IntentChaos.json')
if (Test-Path $builtDll) {
    Copy-Item -Force $builtDll (Join-Path $content 'IntentChaos.dll')
} else {
    Copy-Item -Force (Join-Path $deployed 'IntentChaos.dll') (Join-Path $content 'IntentChaos.dll')
}
Remove-Item -Force -ErrorAction SilentlyContinue (Join-Path $content 'IntentChaos.pdb'), (Join-Path $content 'config.json')

# VDF 的 contentfolder / previewfile 必须是本机绝对路径，每台机器不同 → 每次同步按当前仓库位置刷新，
# 别手改这两行（仓库里存的是 <REPO> 占位符，由本脚本替换成本机路径）。
$repoFwd = $repoRoot -replace '\\', '/'
$v = Read-Utf8 $vdf
$v = [regex]::Replace($v, '(?m)^([ \t]*"contentfolder"[ \t]*)".*"$',
    ('$1"' + $repoFwd + '/IntentChaos/workshop/content/IntentChaos"'))
$v = [regex]::Replace($v, '(?m)^([ \t]*"previewfile"[ \t]*)".*"$',
    ('$1"' + $repoFwd + '/IntentChaos/workshop/preview.jpg"'))

# 只改 VDF 的 changenote（不再往 json 里回写版本号——正是那一步把整份清单的中文写坏的）
if ($Version -ne "") {
    $note = "$Version 更新"
    $v = [regex]::Replace($v, '"changenote"\s*"[^"]*"', ('"changenote" "' + $note + '"'))
    Write-Host "changenote -> $note"
}
Write-Utf8 $vdf $v

# ---- 同步后自检 ----
$fail = @()
$jsonPath = Join-Path $content 'IntentChaos.json'
$dllPath = Join-Path $content 'IntentChaos.dll'
$j = Read-Utf8 $jsonPath
$jb = [IO.File]::ReadAllBytes($jsonPath)
$ver = $null
try { $ver = ($j | ConvertFrom-Json).version } catch { $fail += "content json 解析失败：$($_.Exception.Message)" }
if ($jb.Length -ge 3 -and $jb[0] -eq 0xEF -and $jb[1] -eq 0xBB -and $jb[2] -eq 0xBF) { $fail += "content json 带了 BOM" }
$vb = [IO.File]::ReadAllBytes($vdf)
if ($vb.Length -ge 3 -and $vb[0] -eq 0xEF -and $vb[1] -eq 0xBB -and $vb[2] -eq 0xBF) { $fail += "VDF 带了 BOM" }
if ($j -notmatch '"name":\s*"意图乱流') { $fail += "content json 标题不是'意图乱流'（中文已损坏？）" }
$v = Read-Utf8 $vdf
if ($v -notmatch '"title"\s*"意图乱流') { $fail += "VDF 标题不是'意图乱流'（中文已损坏？）" }
foreach ($pair in @(@('content json', $j), @('VDF', $v))) {
    if ($pair[1].IndexOf([char]0xFFFD) -ge 0) { $fail += ("{0} 含替换符 U+FFFD（编码已被破坏）" -f $pair[0]) }
    foreach ($m in $MojibakeMarkers) {
        if ($pair[1].Contains($m)) { $fail += ("{0} 命中乱码特征串 [{1}]" -f $pair[0], $m); break }
    }
}
if (Test-Path $dllPath) {
    if ((Get-Item $dllPath).Length -lt 1000) { $fail += "content dll 小得可疑" }
    if (-not ([Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($dllPath)).Contains('IntentRerollPatch'))) {
        $fail += "content dll 里没有 IntentRerollPatch（不是本 mod 的构建？）"
    }
    if (Test-Path (Join-Path $deployed 'IntentChaos.dll')) {
        if ((Get-FileHash (Join-Path $deployed 'IntentChaos.dll')).Hash -ne (Get-FileHash $dllPath).Hash) {
            $fail += "content dll 与游戏 mods 目录里那份不一致（先 dotnet build 再同步）"
        }
    }
} else { $fail += "content dll 不存在" }
# content 目录会被整包上传，多余文件必须提醒
foreach ($extra in (Get-ChildItem $content | Where-Object { $_.Name -notin @('IntentChaos.json', 'IntentChaos.dll') })) {
    $fail += ("content 目录里有无关文件 {0}，会一起上传到工坊" -f $extra.Name)
}

Write-Host "workshop content synced（清单版本 $ver）:"
Get-ChildItem $content | ForEach-Object { Write-Host ("  " + $_.Name + "  " + $_.Length + " B") }
Write-Host ""
if ($fail.Count -gt 0) {
    Write-Host "[FAIL] 同步后自检未通过："
    $fail | ForEach-Object { Write-Host ("  - " + $_) }
    Write-Host "  修法：content/IntentChaos.json 用 IntentChaos/IntentChaos.json 覆盖即可；"
    Write-Host "        VDF 的中文段落不可逆，只能整段重写（见 workshop/IntentChaos.vdf.mojibake.bak 的教训）"
    exit 1
}
Write-Host "[OK] 自检通过：json/vdf 均 UTF-8 无 BOM 且中文完好，dll 与游戏目录那份一致，content 无多余文件"
Write-Host ""
Write-Host "上传命令（SteamCMD 首次登录会要 Steam Guard 验证码）："
$steamCmdPath = if ($SteamCmd) { $SteamCmd } else { Join-Path $repoRoot 'tools\SteamCMD\steamcmd.exe' }
if (-not (Test-Path $steamCmdPath)) { $steamCmdPath = '<SteamCMD>\steamcmd.exe' }
Write-Host ('  "' + $steamCmdPath + '" +login <你的Steam用户名> +workshop_build_item "' + $vdf + '" +quit')
