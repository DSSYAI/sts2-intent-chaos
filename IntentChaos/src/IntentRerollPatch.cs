using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.ValueProps;

namespace IntentChaos;

/// <summary>
/// 核心补丁：Hook.AfterDamageReceived 的 postfix。
/// 触发条件（全部满足）：
///   1. 伤害接收方是怪物且存活；
///   2. 伤害来源是玩家方生物；
///   3. 直接攻击伤害：props 是"有力量加成的攻击"（Move 且非 Unpowered——官方语义里
///      Unpowered 就是遗物/药水/能力伤害标记），或 cardSource 是攻击卡（罕见跳力量真攻击）；
///   4. 真实掉血 result.UnblockedDamage > 0（满格挡不触发）；
///   5. 怪物当前不在出招中。
/// 效果：从该怪物状态机的全部招式池（States 里所有 MoveState，含一次性开场招）随机换成一个
/// 不同的招（排除黑名单），用本体 SetMoveImmediate 切换（锁定招式自动免疫、自带意图 UI 刷新）。
/// 特例：女王前三招固定（StateLog 计数门控）；boss 池内额外注入"塞诅咒牌"招。
/// 固定招（v0.3.0/v0.3.1）：三类"结构性招"<b>双向</b>不动——既不随机进池，也不会被换走：
/// 亡语/自爆（DeathBlowIntent，瀑布巨兽靠 EXPLODE_MOVE 自杀才死得掉）、
/// 空白意图招（零意图或全 HiddenIntent，千足虫 DEAD_MOVE 是重生链链头）、
/// 本体锁定招（MustPerformOnceBeforeTransitioning，REATTACH/RESPAWN/ABOUT_TO_BLOW）。
/// v0.3.2 再加一类：boss 的一次性开场招（状态机入度 0 的链头 + 意图含 StatusIntent），
/// 理由是换招会把状态机当前位置一起搬走、链条永久改道（沙虫的吞噬倒计时靠这一招发动）。
/// 另有黑名单招（逃跑/睡眠/眩晕 + excludeMoveIds）默认也双向固定。详见 StructuralReason / IsOneShotBossGrant。
/// </summary>
public static class IntentRerollPatch
{
    public const string CurseMoveId = "INTENT_CHAOS_CURSE_MOVE";

    private static bool _disabled;
    private static int _traceCount;

    // Compat.Resolve 成功后才由 ModEntry 置 true
    public static bool CompatReady { get; set; }

    public static void Postfix(object[] __args)
    {
        if (_disabled || !CompatReady)
        {
            return;
        }
        try
        {
            Handle(__args);
        }
        catch (Exception e)
        {
            // 本体 API 漂移等意外：只停用自己，绝不影响游戏运行
            _disabled = true;
            GD.PushError($"[IntentChaos] 补丁体异常，mod 已自动休眠：{e.Message}");
        }
    }

    private static void Handle(object[] args)
    {
        bool trace = ModConfig.Instance.logRerolls;
        if (trace && ++_traceCount <= 30)
        {
            var t0 = Safe(args, Compat.IdxTarget);
            var d0 = Safe(args, Compat.IdxDealer);
            var c0 = Safe(args, Compat.IdxCardSource);
            GD.Print($"[IntentChaos][trace] postfix #{_traceCount}: target={Describe(t0)} dealer={Describe(d0)} cardSource={Describe(c0)} props={Safe(args, Compat.IdxProps)}");
        }

        if (args == null || args.Length <= Compat.IdxDealer)
        {
            return;
        }
        var target = args[Compat.IdxTarget] as Creature;
        var result = args[Compat.IdxResult] as DamageResult;
        var dealer = args[Compat.IdxDealer] as Creature;
        var cardSource = args[Compat.IdxCardSource] as CardModel;
        var props = args[Compat.IdxProps] as ValueProp? ?? default;

        if (target == null || result == null || dealer == null)
        {
            return;
        }
        if (Compat.GetSide(dealer) != CombatSide.Player)
        {
            return; // 只关心玩家方造成的伤害（荆棘等怪物来源自动排除）
        }
        // 直接攻击过滤：标准攻击卡/攻击招式（Move 且非 Unpowered），
        // 或来源是一张攻击卡（罕见"跳力量真攻击"如 Omnislice）。
        // 遗物/能力牌/回合结束/Burn 等被动伤害全部带 Unpowered 或非攻击卡来源 → 被排除。
        bool powered = Compat.IsPoweredAttack(props);
        bool fromAttackCard = cardSource != null && Compat.GetCardType(cardSource) == CardType.Attack;
        if (!powered && !fromAttackCard)
        {
            if (trace) GD.Print($"[IntentChaos][trace] 退出：非直接攻击（props={props}, cardSource={(cardSource == null ? "null" : Compat.GetCardType(cardSource).ToString())}）");
            return;
        }
        if (Compat.GetIsDead(target) || result.UnblockedDamage <= 0)
        {
            return; // 已死 / 没掉血（满格挡）不触发
        }
        var monster = Compat.GetMonster(target);
        if (monster == null || Compat.GetIsPerformingMove(monster))
        {
            return; // 不是怪物 / 正在出招
        }

        var combat = Compat.GetCombatState(target);
        if (combat == null)
        {
            return;
        }
        var machine = Compat.GetMoveStateMachine(monster);
        if (machine == null)
        {
            if (trace) GD.Print("[IntentChaos][trace] 退出：MoveStateMachine=null");
            return;
        }

        bool isBoss = IsBoss(combat);
        bool isQueen = isBoss && string.Equals(Compat.GetModelId(monster).Entry, "QUEEN", StringComparison.OrdinalIgnoreCase);

        var current = Compat.GetNextMove(monster);
        string currentId = Compat.GetMoveId(current);

        // 【结构性招固定】亡语/自爆、空白意图（零意图或全 HiddenIntent）、本体锁定招——永不换走。
        // 瀑布巨兽：血归零 → 999999999 血（血条显示无限）→ ABOUT_TO_BLOW（锁定眩晕）→
        //   EXPLODE_MOVE（亡语，自杀才是它唯一死法），换掉任何一环就永久打不死。
        // 千足虫：肢体死后 → DEAD_MOVE（零意图，画面上什么都不显示）→ REATTACH_MOVE（回血，锁定）
        //   → 复活。空白招发给活肢体＝一个"什么都不做、再打也不变"的意图（v0.3.1 用户报告）。
        // 沙虫（TheInsatiable，v0.3.2 用户报告）：首回合 LIQUIFY_GROUND_MOVE 是链头且无后继指向它，
        //   换走＝吞噬倒计时和 6 张慌乱逃离整场消失 → 见 IsOneShotBossGrant。
        string? pinned = PinReason(current, currentId, machine, isBoss);
        if (pinned != null)
        {
            if (trace) GD.Print($"[IntentChaos][trace] 退出：当前招 {currentId} {pinned}");
            return;
        }

        // 【救场】亡语招已被（旧版本）换走、链条断掉的场次：把自爆招强行放回去。
        if (ModConfig.Instance.repairStrandedDeathBlows
            && TryRescueStrandedDeathBlow(monster, machine, current, target, trace))
        {
            return;
        }

        // 女王特例：当前招属于开场招集合且未出满固定招数时不重掷。
        // 爪牙提前死时本体路由直接跳到攻击循环（不在开场集合），固定窗口即刻结束。
        if (isQueen && IsQueenOpeningFixed(machine, currentId))
        {
            if (trace) GD.Print($"[IntentChaos][trace] 退出：女王开场招 {currentId} 固定（StateLog={Compat.GetStateLog(machine).Count}）");
            return;
        }

        // boss：懒注入"塞诅咒牌"招（判重）
        if (isBoss && ModConfig.Instance.bossCurseMove && Compat.Curse.Available)
        {
            var states = Compat.GetStates(machine);
            if (!states.ContainsKey(CurseMoveId))
            {
                var curseState = BuildCurseMove(monster, currentId, trace);
                if (curseState != null)
                {
                    states[CurseMoveId] = curseState;
                    if (trace) GD.Print($"[IntentChaos][trace] 已向 boss 招式池注入 {CurseMoveId}（{ModConfig.Instance.bossCurseCardId}）");
                }
            }
        }

        // 候选池：状态机全部 MoveState − 当前招 − 黑名单（亡语/自爆招无条件不进池）
        var pool = new List<MoveState>();
        foreach (var kv in Compat.GetStates(machine))
        {
            if (kv.Value is not MoveState ms)
            {
                continue; // 条件/随机分支状态不是招式
            }
            string id = Compat.GetMoveId(ms);
            if (id == currentId)
            {
                continue;
            }
            if (IsStructural(ms))
            {
                // 亡语/自爆、空白意图（零意图或全 HiddenIntent）、本体锁定招：都不发。
                // 空白招发出去就是"怪头顶什么都不显示、这一回合什么都不做"；
                // 锁定招发出去就连本体自己都换不走，还会挡住本体的死亡/重生脚本。
                if (trace) GD.Print($"[IntentChaos][trace] 池排除：{id}（结构性招：{StructuralReason(ms)}）");
                continue;
            }
            if (IsOneShotBossGrant(ms, machine, isBoss))
            {
                // boss 一辈子只走到一次的开场招：发第二遍等于把开场演两遍
                // （沙虫＝同一个玩家身上挂两条独立的吞噬倒计时，慌乱逃离只回推其中一条）。
                if (trace) GD.Print($"[IntentChaos][trace] 池排除：{id}（boss 一次性开场招，本体链条不会回访）");
                continue;
            }
            if (IsExcluded(ms, id))
            {
                if (trace) GD.Print($"[IntentChaos][trace] 池排除：{id}（黑名单）");
                continue;
            }
            pool.Add(ms);
        }
        if (pool.Count == 0)
        {
            if (trace) GD.Print($"[IntentChaos][trace] 退出：候选池为空（当前 {currentId} 可能是唯一安全招）");
            return;
        }

        // 同步 RNG 抽取（联机两端一致）
        var runRng = Compat.GetRunRng(monster);
        if (runRng == null)
        {
            return;
        }
        int idx = Compat.RngNextInt(Compat.GetMonsterAi(runRng), pool.Count);
        var picked = pool[idx];

        // 本体标准切换：写 NextMove + ForceCurrentState + RefreshIntents；
        // 当前招锁定（眩晕/蓄力 MustPerformOnce）时静默不动作。
        Compat.SetMoveImmediate(monster, picked);
        string afterId = Compat.GetMoveId(Compat.GetNextMove(monster));
        if (afterId == currentId)
        {
            if (trace) GD.Print($"[IntentChaos][trace] 退出：当前招式锁定，维持 {currentId}");
            return;
        }
        GD.Print($"[IntentChaos] {Compat.GetName(target)}: {currentId} -> {afterId}（掉血 {result.UnblockedDamage}）");
    }

    private static bool IsBoss(ICombatState combat)
    {
        var encounter = Compat.GetEncounter(combat);
        return encounter != null && Compat.GetRoomType(encounter) == RoomType.Boss;
    }

    /// <summary>
    /// 女王开场固定窗口：当前招属于开场招集合 且 已完成招数 &lt; 固定招数。
    /// StateLog 首条是初始状态，其后每回合本体的正常重掷各记 1 条，
    /// 故已完成招数 = StateLog.Count - 1（眩晕等 SetMoveImmediate 不计数，可接受的近似）。
    /// 爪牙提前死时本体路由直接跳到攻击循环（当前招不在集合内），固定窗口即刻结束。
    /// </summary>
    private static bool IsQueenOpeningFixed(MonsterMoveStateMachine machine, string currentId)
    {
        var cfg = ModConfig.Instance;
        if (cfg.queenFixedMoveIds == null || cfg.queenFixedMoveIds.Count == 0)
        {
            return false;
        }
        bool inOpening = false;
        foreach (var id in cfg.queenFixedMoveIds)
        {
            if (string.Equals(id?.Trim(), currentId, StringComparison.OrdinalIgnoreCase))
            {
                inOpening = true;
                break;
            }
        }
        if (!inOpening)
        {
            return false;
        }
        int performed = Compat.GetStateLog(machine).Count - 1;
        return performed < cfg.queenFixedOpeningMoves;
    }

    private static bool IsExcluded(MoveState ms, string id)
    {
        var cfg = ModConfig.Instance;
        if (cfg.excludeMoveIds != null && cfg.excludeMoveIds.Any(x => string.Equals(x?.Trim(), id, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        if (cfg.blacklistDangerousMoves)
        {
            // 真灾难：逃跑（永久脱战）、自爆、睡眠/眩晕类
            foreach (var intent in Compat.GetIntents(ms))
            {
                if (intent is EscapeIntent or DeathBlowIntent or SleepIntent or StunIntent)
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>招式是否含"亡语/自爆"意图（本体 DeathBlowIntent）。</summary>
    private static bool HasDeathBlow(MoveState ms)
    {
        foreach (var intent in Compat.GetIntents(ms))
        {
            if (intent is DeathBlowIntent)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 当前招能不能被换走。返回不可换的原因；null 表示可以换。
    /// 结构性招（见 StructuralReason）绝对不换，黑名单招在 pinBlacklistedMoves 下也不换。
    /// </summary>
    private static string? PinReason(MoveState current, string currentId, MonsterMoveStateMachine machine, bool isBoss)
    {
        string? structural = StructuralReason(current);
        if (structural != null)
        {
            return structural;
        }
        if (IsOneShotBossGrant(current, machine, isBoss))
        {
            return "是 boss 的一次性开场招（往玩家牌堆塞牌，本体链条不会回访）：换走就整场消失";
        }
        if (ModConfig.Instance.pinBlacklistedMoves && IsExcluded(current, currentId))
        {
            return "在黑名单内（不进池也不换走，否则这招永久消失）";
        }
        return null;
    }

    /// <summary>
    /// boss 的"一次性开场招"：房间是 Boss + 状态机里没有任何后继指向这一招 + 意图含 StatusIntent
    /// （往玩家牌堆塞状态牌）。三条判据全是游戏内属性，不写怪名。
    /// <para>为什么必须双向固定（既不换走也不进池）：</para>
    /// 本体 <c>MonsterModel.SetMoveImmediate</c> 换招时是「NextMove = state; ForceCurrentState(state)」，
    /// 把状态机的当前位置一起搬走，所以换走链头之后<b>本体的脚本永久改道</b>，这招只能靠我们的骰子回来
    /// （每回合约 1/5）——不回来就整场消失；反过来把它随机发到中盘，又等于把"只发生一次"的开场演两遍。
    /// <para>沙虫 TheInsatiable 两头都坏：</para>
    /// 换走 → 没有 SandpitPower 吞噬倒计时（全库只有 LiquifyMove 施加它），也没有 6 张慌乱逃离，
    ///        boss 退化成纯打桩；<br/>
    /// 发两遍 → SandpitPower 是 PowerInstanceType.Instanced（"再次施加时新增实例，不叠加"），
    ///          同一玩家身上挂两条独立倒计时，而慌乱逃离只回推 FirstOrDefault 那一条。
    /// <para>影响面（v0.111.0，_scan\scan_openings.ps1）：全库 101 个状态机里 27 个一次性链头，
    /// 同时满足三条判据的只有 TheInsatiable.LIQUIFY_GROUND_MOVE。普通怪的同类开场（潮湿邪教徒的祷文、
    /// 幽魂骑士的咒术等）判据不命中，照旧全随机。</para>
    /// </summary>
    private static bool IsOneShotBossGrant(MoveState ms, MonsterMoveStateMachine machine, bool isBoss)
    {
        if (!isBoss || !ModConfig.Instance.preserveBossOpenings)
        {
            return false;
        }
        bool grantsCards = false;
        foreach (var i in Compat.GetIntents(ms))
        {
            if (i is StatusIntent)
            {
                grantsCards = true;
                break;
            }
        }
        if (!grantsCards)
        {
            return false;
        }
        return IsStrandedInChain(machine, Compat.GetMoveId(ms));
    }

    /// <summary>
    /// 状态机的链条还能不能回到这个状态：没有任何状态的后继指向它（含自指）→ 一次性状态。
    /// 只要有一个状态的后继读不出来（本体分支状态换了形状，见 Compat.GetSuccessorIds），
    /// 一律返回 false——保守当作"能回访"，宁可少固定一招，也不误删玩家的开场花样。
    /// </summary>
    private static bool IsStrandedInChain(MonsterMoveStateMachine machine, string id)
    {
        foreach (var kv in Compat.GetStates(machine))
        {
            var st = kv.Value;
            if (st == null)
            {
                return false;
            }
            var succ = Compat.GetSuccessorIds(st);
            if (succ == null)
            {
                return false;
            }
            foreach (var s in succ)
            {
                if (string.Equals(s, id, StringComparison.Ordinal))
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>
    /// "结构性招式"判定——同一份逻辑同时用于<b>当前招不换走</b>和<b>候选池不进</b>（双向对称）。
    /// 返回不可动的原因；null 表示可以动。三类，都会破坏本体脚本：
    ///  1) <b>亡语/自爆招</b>（DeathBlowIntent）——怪物死亡结算的唯一出口。瀑布巨兽血归零后被本体
    ///     设成 999999999 血（血条"无限"），只有 EXPLODE_MOVE 里 CreatureCmd.Kill 自己；换掉它就
    ///     永久打不死、战斗也结束不了（v0.3.0 事故）。进池则等于满血 boss 原地暴毙白送。绝对，无视 config。
    ///  2) <b>空白意图招</b>——零意图，或意图全是 HiddenIntent（本体该意图 SpritePath=null、
    ///     HasIntentTip=false，画面上什么都不显示）。千足虫（DecimillipedeSegment，三条肢体，
    ///     靠 ReattachPower 无限重生）的 DEAD_MOVE 正是这种：它自己什么都不做，却是重生链的链头
    ///     （DEAD_MOVE → REATTACH_MOVE 回血 → RAND）。随机发给一条活肢体，玩家就会看到
    ///     "一个不进行任何动作的意图"；而这条肢体死了以后不能再被选中（ShouldAllowHitting=false），
    ///     所以我们后续的伤害根本不会为它触发重掷 → 那个空意图看起来"再打也不变"（v0.3.1 用户报告）。
    ///  3) <b>本体锁定招</b>（MustPerformOnceBeforeTransitioning）——千足虫 REATTACH_MOVE、
    ///     实验体 RESPAWN_MOVE、瀑布巨兽 ABOUT_TO_BLOW_MOVE。换进来之后本体自己都换不走
    ///     （SetMoveImmediate 只在 CanTransitionAway 时生效），意图就钉死在怪头上；更糟的是它会
    ///     挡住死亡脚本：ReattachPower.AfterDeath 调的是不带 forceTransition 的
    ///     SetMoveImmediate(DeadState)，锁定期内静默失败，那条断肢的重生流程直接乱掉。
    /// </summary>
    private static string? StructuralReason(MoveState ms)
    {
        if (HasDeathBlow(ms))
        {
            return "是亡语/自爆招（怪物死亡结算的唯一出口），绝对固定";
        }
        if (ModConfig.Instance.pinStructuralMoves)
        {
            if (IsBlankMove(ms))
            {
                return "是空白意图招（零意图或全 HiddenIntent，本体拿它当占位/重生链链头），绝对固定";
            }
            if (Compat.GetMustPerformOnce(ms))
            {
                return "是本体锁定招（MustPerformOnceBeforeTransitioning，重生/复活/约爆类），绝对固定";
            }
        }
        if (!Compat.GetCanTransitionAway(ms))
        {
            return "被本体锁定（MustPerformOnce 还没执行完），本来就换不动";
        }
        return null;
    }

    /// <summary>这招在画面上是不是"什么都不显示"：没有意图，或者意图全是 HiddenIntent。</summary>
    private static bool IsBlankMove(MoveState ms)
    {
        var intents = Compat.GetIntents(ms);
        if (intents.Count == 0)
        {
            return true;
        }
        foreach (var i in intents)
        {
            if (i is not HiddenIntent)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>候选池侧的同一判断：结构性招既不换走也不进池。</summary>
    private static bool IsStructural(MoveState ms) => StructuralReason(ms) != null;

    /// <summary>
    /// 救场：本体用 HpDisplay.InfiniteWithoutNumbers 标记"死亡结算中"（瀑布巨兽 999999999 血）。
    /// 若此时它的亡语招已经不在当前招位置（说明链条被本 mod 早期版本换断过），
    /// 强行把自爆招放回 NextMove（forceTransition，与本体 TriggerAboutToBlowState 同款），
    /// 让这场战斗能正常结束。已存档的卡死场次在下次受伤时自动被救回。
    /// </summary>
    private static bool TryRescueStrandedDeathBlow(MonsterModel monster, MonsterMoveStateMachine machine, MoveState current, Creature target, bool trace)
    {
        if (Compat.GetHpDisplay(target) != HpDisplay.InfiniteWithoutNumbers)
        {
            return false;
        }
        var blow = FindDeathBlowMove(machine);
        if (blow == null || blow == current)
        {
            if (trace) GD.Print("[IntentChaos][trace] 无限血阶段但池内无亡语招可放回（不处理）");
            return false;
        }
        Compat.SetMoveImmediateForce(monster, blow);
        GD.Print($"[IntentChaos] 救场：{Compat.GetName(target)} 处于无限血阶段而亡语招已被换走，" +
                 $"强制放回 {Compat.GetMoveId(blow)}（否则它死不掉、战斗结束不了）");
        return true;
    }

    private static MoveState? FindDeathBlowMove(MonsterMoveStateMachine machine)
    {
        foreach (var kv in Compat.GetStates(machine))
        {
            if (kv.Value is MoveState ms && HasDeathBlow(ms))
            {
                return ms;
            }
        }
        return null;
    }

    /// <summary>
    /// 构造"塞诅咒牌"招：对每名玩家，永久加一张诅咒牌进卡组（Player.Deck 直接序列化，
    /// 战后不移除——寄生式），同时塞一张战斗副本进本战抽牌堆随机位（战后消失）。
    /// </summary>
    private static MoveState? BuildCurseMove(MonsterModel monster, string followUpId, bool trace)
    {
        try
        {
            var canonical = Compat.Curse.GetCanonicalCard(new ModelId("CARD", ModConfig.Instance.bossCurseCardId));
            if (canonical == null)
            {
                if (trace) GD.Print($"[IntentChaos][trace] 诅咒牌 {ModConfig.Instance.bossCurseCardId} 不存在，跳过注入");
                return null;
            }

            // 注入意图文案（LocString 缺 key 会抛异常，必须先写进本体 intents 表；每次合并幂等）
            Compat.Curse.InjectLocEntries("intents", new Dictionary<string, string>
            {
                { "INTENT_CHAOS_CURSE.title", ModConfig.Instance.curseIntentTitle },
                { "INTENT_CHAOS_CURSE.description", ModConfig.Instance.curseIntentDescription },
            });

            async Task Perform(IReadOnlyList<Creature> targets)
            {
                foreach (var t in targets)
                {
                    // 单个玩家失败绝不外抛——onPerform 的异常会杀掉本体回合循环（v0.2.0 事故）
                    try
                    {
                        var player = Compat.Curse.GetPlayer(t) ?? Compat.Curse.GetPetOwner(t);
                        if (player == null)
                        {
                            continue;
                        }
                        var combat = Compat.GetCombatState(t);
                        if (combat == null)
                        {
                            continue;
                        }
                        // 永久进卡组（战后保留）
                        await Compat.Curse.AddCursesToDeck(new[] { canonical }, player);
                        // 战斗副本：必须经 CombatState.CreateCard 注册进战斗状态
                        var copy = Compat.Curse.CreateCard(combat, canonical, player);
                        if (copy != null)
                        {
                            await Compat.Curse.AddGeneratedToCombat(copy, null);
                        }
                    }
                    catch (Exception e)
                    {
                        GD.PushWarning($"[IntentChaos] 塞诅咒牌失败（跳过该玩家，不影响战斗）：{e.Message}");
                    }
                }
            }
            var state = new MoveState(CurseMoveId, Perform, new CurseIntent());
            Compat.Curse.SetFollowUpStateId(state, followUpId);
            return state;
        }
        catch (Exception e)
        {
            GD.PushWarning($"[IntentChaos] 构造诅咒招失败（不影响核心玩法）：{e.Message}");
            return null;
        }
    }

    private static object? Safe(object[]? args, int idx)
    {
        return args != null && args.Length > idx ? args[idx] : null;
    }

    private static string Describe(object? o)
    {
        if (o == null)
        {
            return "null";
        }
        if (o is Creature c)
        {
            return Compat.GetIsDead(c) ? Compat.GetName(c) + "(死)" : Compat.GetName(c);
        }
        if (o is CardModel card)
        {
            return Compat.GetCardType(card).ToString();
        }
        return o.GetType().Name;
    }
}
