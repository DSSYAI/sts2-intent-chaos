# 全库扫描：按 IntentChaos v0.3.1 的判定规则，给每只怪每个招式分类，看覆盖面与副作用。
# 纯静态解析 _sts2_decomp 的反编译源码（不加载游戏、不改任何文件）。
# 注意：仓库不附带本体反编译产物（版权 + 体积），需自备；用 -DecompDir 指向它的根目录。
# 用法：pwsh -File scan_moves.ps1 [-DecompDir <反编译树根>] [-OutDir <csv 落点>]
param(
    [string]$DecompDir = "",
    [string]$OutDir = ""
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot                    # 仓库根
if (-not $DecompDir) { $DecompDir = Join-Path $root '_sts2_decomp' }
if (-not $OutDir) { $OutDir = $PSScriptRoot }
$dir = Join-Path $DecompDir 'MegaCrit.Sts2.Core.Models.Monsters'
if (-not (Test-Path $dir)) {
    throw "找不到本体反编译源码：$dir （仓库不含反编译产物，请先用 ILSpy 解出本体，或用 -DecompDir 指定）"
}
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$intentRx = [regex]'new\s+([A-Za-z0-9_]+Intent)\s*\('
$idRx = [regex]'"([^"]+)"'

$rows = New-Object System.Collections.Generic.List[object]

foreach ($f in (Get-ChildItem $dir -Filter *.cs | Sort-Object Name)) {
    $t = [IO.File]::ReadAllText($f.FullName, [Text.Encoding]::UTF8)
    $monster = $f.BaseName
    $pos = 0
    while ($true) {
        $idx = $t.IndexOf('new MoveState(', $pos)
        if ($idx -lt 0) { break }
        $open = $idx + 'new MoveState'.Length          # 指向 '('
        $depth = 0; $j = $open; $close = -1
        while ($j -lt $t.Length) {
            $c = $t[$j]
            if ($c -eq '(') { $depth++ }
            elseif ($c -eq ')') { $depth--; if ($depth -eq 0) { $close = $j; break } }
            $j++
        }
        if ($close -lt 0) { break }
        $args = $t.Substring($open + 1, $close - $open - 1)
        # 对象初始化器 { MustPerformOnceBeforeTransitioning = true } 只认"紧跟本次构造"的那个：
        # 跳过空白后必须正好是 '{'. 否则会把下一条语句的初始化器误吞
        # （PRESSURE_UP_MOVE / PLOW_MOVE 就被"往后看 160 字符"的写法误标过）
        $k = $close + 1
        while ($k -lt $t.Length -and [char]::IsWhiteSpace($t[$k])) { $k++ }
        $locked = $false
        if ($k -lt $t.Length -and $t[$k] -eq '{') {
            $d2 = 0; $m = $k
            while ($m -lt $t.Length) {
                $c2 = $t[$m]
                if ($c2 -eq '{') { $d2++ }
                elseif ($c2 -eq '}') { $d2--; if ($d2 -eq 0) { break } }
                $m++
            }
            if ($m -lt $t.Length) {
                $locked = ($t.Substring($k, $m - $k + 1) -match 'MustPerformOnceBeforeTransitioning\s*=\s*true')
            }
        }
        $mId = $idRx.Match($args)
        $id = if ($mId.Success) { $mId.Groups[1].Value } else { '(无id)' }
        $intents = @($intentRx.Matches($args) | ForEach-Object { $_.Groups[1].Value })
        $n = $intents.Count
        $cat = if ($n -eq 0) { '空白(零意图)' }
               elseif (($intents | Where-Object { $_ -ne 'HiddenIntent' }).Count -eq 0) { '空白(全Hidden)' }
               elseif ($intents -contains 'DeathBlowIntent') { '亡语/自爆' }
               elseif ($intents -contains 'EscapeIntent') { '黑名单(逃跑)' }
               elseif ($intents -contains 'SleepIntent') { '黑名单(睡眠)' }
               elseif ($intents -contains 'StunIntent') { '黑名单(眩晕)' }
               else { '普通招' }
        # v0.3.0 的判定：只有亡语 + 黑名单意图（眩晕/睡眠/逃跑）被固定；空白招和锁定招当时是合法候选
        $oldPinned = ($intents -contains 'DeathBlowIntent') -or ($intents -contains 'EscapeIntent') `
                   -or ($intents -contains 'SleepIntent') -or ($intents -contains 'StunIntent')
        # v0.3.1 的判定：再加上"空白意图"与"本体锁定招"
        $newPinned = $oldPinned -or ($cat -like '空白*') -or $locked
        $pinned = ($cat -ne '普通招') -or $locked
        if ($locked -and $cat -eq '普通招') { $cat = '锁定招(必须执行一次)' }
        elseif ($locked -and $cat -notlike '锁定*') { $cat += ' + 锁定' }
        $rows.Add([pscustomobject]@{
                Monster = $monster; Move = $id; Intents = ($intents -join '/')
                Structural = $pinned; Category = $cat
                Pinned_v030 = $oldPinned; Pinned_v031 = $newPinned
                NewlyPinned = ((-not $oldPinned) -and $newPinned)
            })
        $pos = $close + 1
    }
}

# 有的文件里 MoveState 是跨行构造（FollowUpState = new MoveState(...)），上面的扫描已覆盖；
# 只统计"确实构造了招式"的行
$all = $rows
$fixedM = $all | Where-Object Structural
$normalM = $all | Where-Object { -not $_.Structural }

"扫描的怪物文件数 = {0}" -f (Get-ChildItem $dir -Filter *.cs).Count
"识别出的 MoveState 总数 = {0}" -f $all.Count
"  其中被双向固定（结构性）= {0}" -f $fixedM.Count
"  其中仍可随机/可换走      = {0}" -f $normalM.Count
""
"=== 按类别统计 ==="
$all | Group-Object Category | Sort-Object Count -Descending | ForEach-Object {
    "  {0,-24} {1,3} 个招" -f $_.Name, $_.Count
}
""
"=== 空白意图招（v0.3.1 新覆盖的那一类，全部列出来）==="
$all | Where-Object { $_.Category -like '空白*' } | ForEach-Object { "  {0,-26} {1,-22} {2}" -f $_.Monster, $_.Move, $_.Category }
""
"=== 本体锁定招（v0.3.1 起不进池）==="
$all | Where-Object { $_.Category -like '*锁定*' } | ForEach-Object { "  {0,-26} {1,-22} {2}" -f $_.Monster, $_.Move, $_.Category }
""
"=== 亡语/自爆招 ==="
$all | Where-Object { $_.Category -like '亡语*' } | ForEach-Object { "  {0,-26} {1,-22} {2}" -f $_.Monster, $_.Move, $_.Category }
""
"=== v0.3.1 实际改变了什么：新被固定的招（v0.3.0 时代它们是合法候选，会被随机发出去）==="
$all | Where-Object { $_.NewlyPinned } | ForEach-Object { "  {0,-26} {1,-22} {2}" -f $_.Monster, $_.Move, $_.Category }
""
"=== 副作用检查：可重掷招数量 v0.3.0 → v0.3.1 ==="
$per = $all | Group-Object Monster | ForEach-Object {
    $g = $_.Group
    [pscustomobject]@{
        Monster   = $_.Name
        Total     = $g.Count
        Pool_v030 = ($g | Where-Object { -not $_.Pinned_v030 }).Count
        Pool_v031 = ($g | Where-Object { -not $_.Pinned_v031 }).Count
    }
}
$shrunk = $per | Where-Object { $_.Pool_v031 -lt $_.Pool_v030 } | Sort-Object Pool_v030 -Descending
"  受影响的怪（{0} 只）：" -f $shrunk.Count
$shrunk | ForEach-Object { "    {0,-26} 共 {1,2} 招   可随机 {2} → {3}" -f $_.Monster, $_.Total, $_.Pool_v030, $_.Pool_v031 }
$nowZero = $per | Where-Object { $_.Pool_v031 -eq 0 }
""
"  可重掷招为 0 的怪（mod 对它们完全不生效）—— {0} 只：" -f $nowZero.Count
$nowZero | ForEach-Object { "    {0,-26} 共 {1} 招（v0.3.0 时代也是 {2} 个可用）" -f $_.Monster, $_.Total, $_.Pool_v030 }
$regressed = $nowZero | Where-Object { $_.Pool_v030 -gt 0 }
"  ↑ 其中因 v0.3.1 才归零的（真副作用）：{0}" -f $(if ($regressed) { ($regressed | ForEach-Object Monster) -join ', ' } else { '无' })
$oneOnly = $per | Where-Object { $_.Pool_v031 -eq 1 }
"  只剩 1 个可随机招的怪（只会在两招间来回，观感偏呆）：{0}" -f $(if ($oneOnly) { ($oneOnly | ForEach-Object Monster) -join ', ' } else { '无' })
""
"  可随机招 ≥2 的怪 = {0} 只；怪物文件总数 = {1} 只（含 Mock/Test 占位怪）" -f `
    (($per | Where-Object { $_.Pool_v031 -ge 2 }).Count), $per.Count
$all | Export-Csv -NoTypeInformation -Encoding UTF8 (Join-Path $outDir 'move_classification.csv')
"明细已写盘：$outDir\move_classification.csv"
