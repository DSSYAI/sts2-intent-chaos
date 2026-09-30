# 全库扫描：状态机"链头一次性招"（initial state，入度 0 → 本体链条再也走不到它）。
# 背景：IntentChaos 用本体 SetMoveImmediate 换招，它会连 MoveStateMachine._currentState 一起搬走
# （MonsterModel.SetMoveImmediate → NextMove = state; MoveStateMachine.ForceCurrentState(state)），
# 于是"换走链头"= 把本体唯一的开场招从**脚本链**里永久摘掉；之后只有我们自己的骰子能把它发回来。
# 沙虫 TheInsatiable 的 LIQUIFY_GROUND_MOVE 就是这样：SandpitPower（吞噬倒计时）只在这招里施加。
#
# 用法：pwsh -File scan_openings.ps1 [-DecompDir <反编译树根>]
param([string]$DecompDir = "")
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $DecompDir) { $DecompDir = Join-Path $root '_sts2_decomp' }
$mdir = Join-Path $DecompDir 'MegaCrit.Sts2.Core.Models.Monsters'
$edir = Join-Path $DecompDir 'MegaCrit.Sts2.Core.Models.Encounters'
if (-not (Test-Path $mdir)) { throw "找不到反编译源码：$mdir" }

# ---- 工具：从 index 处 '(' 开始做括号配对，返回闭括号下标 ----
function Close-Paren([string]$s, [int]$open) {
    $d = 0
    for ($i = $open; $i -lt $s.Length; $i++) {
        $c = $s[$i]
        if ($c -eq '(') { $d++ }
        elseif ($c -eq ')') { $d--; if ($d -eq 0) { return $i } }
    }
    return -1
}
function Close-Brace([string]$s, [int]$open) {
    $d = 0
    for ($i = $open; $i -lt $s.Length; $i++) {
        $c = $s[$i]
        if ($c -eq '{') { $d++ }
        elseif ($c -eq '}') { $d--; if ($d -eq 0) { return $i } }
    }
    return -1
}
# ---- 工具：把一段代码按顶层 ';' 切句（忽略括号/字符串内的 ;）----
function Split-Statements([string]$s) {
    $out = New-Object System.Collections.Generic.List[string]
    $d = 0; $inStr = $false; $sb = New-Object System.Text.StringBuilder
    for ($i = 0; $i -lt $s.Length; $i++) {
        $c = $s[$i]
        if ($inStr) {
            [void]$sb.Append($c)
            if ($c -eq '\') { $i++; if ($i -lt $s.Length) { [void]$sb.Append($s[$i]) }; continue }
            if ($c -eq '"') { $inStr = $false }
            continue
        }
        if ($c -eq '"') { $inStr = $true; [void]$sb.Append($c); continue }
        if ($c -eq '(' -or $c -eq '{' -or $c -eq '[') { $d++ }
        elseif ($c -eq ')' -or $c -eq '}' -or $c -eq ']') { $d-- }
        if ($c -eq ';' -and $d -eq 0) {
            $t = $sb.ToString().Trim()
            if ($t) { [void]$out.Add($t) }
            [void]$sb.Clear(); continue
        }
        [void]$sb.Append($c)
    }
    $tail = $sb.ToString().Trim()
    if ($tail) { [void]$out.Add($tail) }
    return $out
}

$intentRx = [regex]'new\s+([A-Za-z0-9_]+Intent)\s*\('
$idRx     = [regex]'"([^"]+)"'

# ---- 先建立 boss / elite 名单：Encounter 的 RoomType + 它引用的怪物类名 ----
$roomOf = @{}
if (Test-Path $edir) {
    foreach ($ef in (Get-ChildItem $edir -Filter *.cs)) {
        $et = [IO.File]::ReadAllText($ef.FullName, [Text.Encoding]::UTF8)
        $rm = 'Monster'
        $mrm = [regex]::Match($et, 'override\s+RoomType\s+RoomType\s*=>\s*RoomType\.(\w+)')
        if ($mrm.Success) { $rm = $mrm.Groups[1].Value }
        foreach ($mm in [regex]::Matches($et, 'ModelDb\.Monster<(\w+)>')) {
            $mn = $mm.Groups[1].Value
            $prev = $roomOf[$mn]
            if (-not $prev) { $roomOf[$mn] = $rm }
            elseif ($prev -ne $rm) { $roomOf[$mn] = if ($rm -eq 'Boss') { 'Boss' } elseif ($rm -eq 'Elite' -and $prev -ne 'Boss') { 'Elite' } else { $prev } }
        }
    }
}

$rows = New-Object System.Collections.Generic.List[object]
$warn = New-Object System.Collections.Generic.List[string]

foreach ($f in (Get-ChildItem $mdir -Filter *.cs | Sort-Object Name)) {
    $monster = $f.BaseName
    $t = [IO.File]::ReadAllText($f.FullName, [Text.Encoding]::UTF8)
    $sig = [regex]::Match($t, 'MonsterMoveStateMachine\s+GenerateMoveStateMachine\s*\(\s*\)\s*\{')
    if (-not $sig.Success) { continue }   # 没有自定义状态机（占位怪等）
    $openBrace = $sig.Index + $sig.Length - 1
    $endBrace = Close-Brace $t $openBrace
    if ($endBrace -lt 0) { $warn.Add("$monster : 状态机方法括号不配"); continue }
    $body = $t.Substring($openBrace + 1, $endBrace - $openBrace - 1)

    $stmts = Split-Statements $body
    $varId   = @{}            # 变量名 -> state id
    $states  = @{}            # state id -> @{Intents;Locked;Kind}
    $edges   = New-Object System.Collections.Generic.List[object]  # @{From;To}
    $initialVar = ''

    # 记录一个 new MoveState(...) 片段：返回它的 id，并登记 intents/locked
    function Register-MoveState([string]$argsText, [string]$afterText) {
        $mid = $idRx.Match($argsText)
        if (-not $mid.Success) { return '' }
        $sid = $mid.Groups[1].Value
        $ints = @($script:intentRx.Matches($argsText) | ForEach-Object { $_.Groups[1].Value })
        $lock = ($afterText -match 'MustPerformOnceBeforeTransitioning\s*=\s*true')
        $script:states[$sid] = @{ Intents = $ints; Locked = $lock; Kind = 'Move' }
        return $sid
    }

    foreach ($st in $stmts) {
        # 1) 本句里所有内联 new MoveState / new RandomBranchState / new ConditionalBranchState
        $newIds = @()
        $p = 0
        while ($true) {
            $ix = $st.IndexOf('new MoveState(', $p)
            if ($ix -lt 0) { break }
            $op = $ix + 'new MoveState'.Length
            $cl = Close-Paren $st $op
            if ($cl -lt 0) { break }
            $argTxt = $st.Substring($op + 1, $cl - $op - 1)
            $after = if ($cl + 40 -lt $st.Length) { $st.Substring($cl + 1, 40) } else { $st.Substring($cl + 1) }
            $sid = Register-MoveState $argTxt $after
            if ($sid) { $newIds += $sid }
            $p = $cl + 1
        }
        # 2) 变量声明 / 赋值：var = ...new MoveState(...)  或  var = 另一个 var
        $md = [regex]::Match($st, '(?:^|\)\s*=)\s*(?:(?:MoveState|MonsterState|RandomBranchState|ConditionalBranchState|var)\s+)?(\w+)\s*=\s*(.+)$', 'Singleline')
        $declName = ''
        $dm = [regex]::Match($st, '(?:(MoveState|MonsterState|RandomBranchState|ConditionalBranchState|var))\s+(\w+)\s*=', 'Singleline')
        if ($dm.Success) { $declName = $dm.Groups[2].Value }
        elseif ($md.Success) { $declName = $md.Groups[1].Value }
        if ($declName) {
            if ($newIds.Count -gt 0) { $varId[$declName] = $newIds[-1] }
            else {
                $rm2 = [regex]::Match($st, "=\s*\((?:MoveState\))?\s*(\w+)\s*(?:;|$|\))", 'Singleline')
                if ($rm2.Success -and $varId.ContainsKey($rm2.Groups[1].Value)) { $varId[$declName] = $varId[$rm2.Groups[1].Value] }
            }
        }
        # 3) 边：X.FollowUpState = Y（Y 是变量或内联 new）
        foreach ($m in [regex]::Matches($st, '(\w+)\.FollowUpState\s*=\s*(?:\(MoveState\))?\s*(\w+|new MoveState)', 'Singleline')) {
            $from = $varId[$m.Groups[1].Value]
            if (-not $from) { $from = $m.Groups[1].Value }
            $r = $m.Groups[2].Value
            $to = ''
            if ($r -eq 'new') { $to = if ($newIds.Count -gt 0) { $newIds[-1] } else { '' } } else { $to = $varId[$r]; if (-not $to) { $to = $r } }
            if ($from -and $to) { [void]$edges.Add(@{ From = $from; To = $to; Kind = 'followup' }) }
        }
        # 4) 边：X.FollowUpStateId = "ID"
        foreach ($m in [regex]::Matches($st, '(\w+)\.FollowUpStateId\s*=\s*"([^"]+)"')) {
            $from = $varId[$m.Groups[1].Value]; if (-not $from) { $from = $m.Groups[1].Value }
            [void]$edges.Add(@{ From = $from; To = $m.Groups[2].Value; Kind = 'followupId' })
        }
        # 5) 边：分支状态 .AddBranch(Y, ...) / .AddState(Y, ...)
        foreach ($m in [regex]::Matches($st, '(\w+)\.(?:AddBranch|AddState)\s*\(\s*(\w+)', 'Singleline')) {
            $from = $varId[$m.Groups[1].Value]; if (-not $from) { $from = $m.Groups[1].Value }
            $to = $varId[$m.Groups[2].Value]; if (-not $to) { $to = $m.Groups[2].Value }
            [void]$edges.Add(@{ From = $from; To = $to; Kind = 'branch' })
        }
        # 6) 初始状态：new MonsterMoveStateMachine(list, X)
        $mi = [regex]::Match($body, 'new\s+MonsterMoveStateMachine\s*\([^,]+,\s*(\w+)\s*\)')
        if ($mi.Success) { $initialVar = $mi.Groups[1].Value }
        # 7) 条件/随机分支的"无后继 → 回初始状态"隐式边（MonsterMoveStateMachine.FindNextMoveState）
    }

    if (-not $initialVar) { $warn.Add("$monster : 没找到 MonsterMoveStateMachine(list, initial)"); continue }
    $head = $varId[$initialVar]
    if (-not $head) { $head = $initialVar }
    if (-not $states.ContainsKey($head)) {
        $warn.Add("$monster : 链头 $head 不是 MoveState（可能是分支状态）")
        continue
    }

    # 入度：有没有任何边的终点是链头
    $indeg = @($edges | Where-Object { $_.To -eq $head }).Count
    # 自指（X.FollowUpState = X）也算可回访
    $ints = $states[$head].Intents
    $kind = if ($indeg -eq 0) { '一次性链头' } else { '可回访链头' }

    # 链头方法体里到底做了什么（静态读私有方法体）：给玩家方上 power / 生成牌 / 给自己上 power
    $fn = [regex]::Match($t, 'new\s+MoveState\s*\(\s*"[^"]+"\s*,\s*(\w+)\s*[,)]')
    $bodyTxt = ''
    foreach ($mm2 in [regex]::Matches($t, 'new\s+MoveState\s*\(\s*"' + [regex]::Escape($head) + '"\s*,\s*(\w+)\s*[,)]')) {
        $fname = $mm2.Groups[1].Value
        $fsig = [regex]::Match($t, "Task\s+$fname\s*\(\s*IReadOnlyList<Creature>[^)]*\)\s*\{")
        if ($fsig.Success) {
            $ob = $fsig.Index + $fsig.Length - 1
            $cb = Close-Brace $t $ob
            if ($cb -gt $ob) { $bodyTxt = $t.Substring($ob + 1, $cb - $ob - 1) }
        }
    }
    $applyToTarget = ($bodyTxt -match 'PowerCmd\.Apply[^;]*\b(target|targets|player|Player)\b')
    $applyToSelf   = ($bodyTxt -match 'PowerCmd\.Apply[^;]*base\.Creature')
    $addsCards     = ($bodyTxt -match 'CardPileCmd\.Add|CreateCard<')
    $kills         = ($bodyTxt -match 'CreatureCmd\.Kill')
    $summons       = ($bodyTxt -match 'SpawnCmd|CreatureCmd\.Add|ModelDb\.Monster<')

    $rows.Add([pscustomobject]@{
            Monster      = $monster
            Room         = $(if ($roomOf.ContainsKey($monster)) { $roomOf[$monster] } else { '(无战斗引用)' })
            Head         = $head
            HeadIntents  = ($ints -join '/')
            States       = $states.Count
            InDegree     = $indeg
            HeadClass    = $kind
            HeadToPlayerPower = [bool]$applyToTarget
            HeadAddsCards     = [bool]$addsCards
            HeadSelfBuff      = [bool]$applyToSelf
            HeadKillOrSummon  = [bool]($kills -or $summons)
            ParsedMoves  = $states.Count
            Edges        = $edges.Count
        })
}

"扫描怪物文件 = {0}，成功解析状态机 = {1}" -f (Get-ChildItem $mdir -Filter *.cs).Count, $rows.Count
if ($warn.Count) { ""; "! 解析不完整/需人工看的 {0} 个：" -f $warn.Count; $warn | ForEach-Object { "    $_" } }

""
"=== 一次性链头（入度 0：本体链条走掉就永远回不来）总体 ==="
$one = $rows | Where-Object HeadClass -eq '一次性链头'
"  数量 = {0} / {1}（其余 {2} 个链头可被链条回访）" -f $one.Count, $rows.Count, ($rows.Count - $one.Count)
""
"=== ★ 危险面：一次性链头 + 它给「玩家方」施加 power（换走=机制整条消失）==="
$danger = $one | Where-Object HeadToPlayerPower
if (-not $danger) { "  （无：没有任何一次性链头往玩家方挂 power）" }
$danger | Sort-Object Room, Monster | ForEach-Object {
    "  {0,-22} [{1,-8}] {2,-24} 意图={3,-22} 招式数={4}" -f $_.Monster, $_.Room, $_.Head, $_.HeadIntents, $_.States
}
""
"=== 一次性链头 + 往玩家牌堆塞牌（换走=这场战斗少一批牌）==="
$cards = $one | Where-Object HeadAddsCards
if (-not $cards) { "  （无）" }
$cards | Sort-Object Room, Monster | ForEach-Object {
    "  {0,-22} [{1,-8}] {2,-24} 意图={3}" -f $_.Monster, $_.Room, $_.Head, $_.HeadIntents
}
""
"=== 一次性链头里 boss 战的全部清单（不管有没有塞牌/上 power）==="
$one | Where-Object Room -eq 'Boss' | Sort-Object Monster | ForEach-Object {
    "  {0,-22} {1,-26} 意图={2,-24} 给玩家power={3} 塞牌={4} 自buff={5} 召唤/击杀={6}" -f `
        $_.Monster, $_.Head, $_.HeadIntents, $_.HeadToPlayerPower, $_.HeadAddsCards, $_.HeadSelfBuff, $_.HeadKillOrSummon
}
""
"=== 一次性链头里 elite 战 ==="
$one | Where-Object Room -eq 'Elite' | Sort-Object Monster | ForEach-Object {
    "  {0,-22} {1,-26} 意图={2,-24} 给玩家power={3} 塞牌={4}" -f $_.Monster, $_.Head, $_.HeadIntents, $_.HeadToPlayerPower, $_.HeadAddsCards
}
""
"=== 普通怪的一次性链头（只列有玩家方 power 或塞牌的，其它无所谓）==="
$one | Where-Object { $_.Room -notin 'Boss','Elite' } | Where-Object { $_.HeadToPlayerPower -or $_.HeadAddsCards } | Sort-Object Monster | ForEach-Object {
    "  {0,-22} [{1,-12}] {2,-24} 意图={3,-20} 给玩家power={4} 塞牌={5}" -f $_.Monster, $_.Room, $_.Head, $_.HeadIntents, $_.HeadToPlayerPower, $_.HeadAddsCards
}
""
"=== 对照：链头可回访（入度>0）的 boss，说明这些 boss 的开场本来就设计成循环 ==="
$rows | Where-Object { $_.HeadClass -eq '可回访链头' -and $_.Room -eq 'Boss' } | ForEach-Object {
    "  {0,-22} 链头={1,-24} 入度={2}" -f $_.Monster, $_.Head, $_.InDegree
}
$rows | Export-Csv -NoTypeInformation -Encoding UTF8 (Join-Path $PSScriptRoot 'opening_head_classification.csv')
""
"明细：$PSScriptRoot\opening_head_classification.csv"
