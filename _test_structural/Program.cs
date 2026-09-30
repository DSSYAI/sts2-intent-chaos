// IntentChaos「结构性招 / boss 一次性开场招」判定的离线回归测试（不需要启动游戏）。
// 把游戏本体 dll 与 mod dll 加载进来，用反射造出几个和本体怪物同形状的 MoveState，
// 直接调用 IntentRerollPatch 的私有判定方法，验证双向固定规则分类是否正确。
//
// 形状全部取自 _sts2_decomp 反编译的真实怪物招式：
//   DecimillipedeSegment（千足虫肢体）  DEAD_MOVE     = 零意图                      → 固定
//   DecimillipedeSegment                REATTACH_MOVE = HealIntent + MustPerformOnce → 固定
//   DecimillipedeSegment                WRITHE_MOVE   = MultiAttackIntent            → 可重掷
//   WaterfallGiant（瀑布巨兽）           EXPLODE_MOVE  = DeathBlowIntent              → 固定
//   Architect / BigDummy                 NOTHING       = HiddenIntent                 → 固定
//   BygoneEffigy / SlumberingBeetle      SLEEP_MOVE    = SleepIntent                  → 固定（黑名单对称）
//   TheInsatiable（沙虫，v0.3.2 用户报） LIQUIFY_GROUND_MOVE = BuffIntent + StatusIntent，
//                                        且状态机里没有任何后继指向它（入度 0）        → 固定
//   DampCultist（潮湿邪教徒）            INCANTATION_MOVE = BuffIntent（纯自 buff）    → 仍可随机
//
// 跑法：cd _test_structural; dotnet run
using System;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;

// 路径不写死：本机配置统一来自 IntentChaos\local.props（见 _shared\LocalPaths.cs）
var gameLib = LocalPaths.GameLibDir;
var modDll = LocalPaths.ModDll;

AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
{
    var p = Path.Combine(gameLib, new AssemblyName(e.Name).Name + ".dll");
    return File.Exists(p) ? Assembly.LoadFrom(p) : null;
};

var game = Assembly.LoadFrom(Path.Combine(gameLib, "sts2.dll"));
var mod = Assembly.LoadFrom(modDll);

Type GType(string full) => game.GetType(full, throwOnError: true)!;
string MoveId(object ms) => (string)ms.GetType().GetProperty("Id")!.GetValue(ms)!;

var creatureType = GType("MegaCrit.Sts2.Core.Entities.Creatures.Creature");
var moveStateType = GType("MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.MoveState");
var monsterStateType = GType("MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.MonsterState");
var machineType = GType("MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.MonsterMoveStateMachine");
var intentBaseType = GType("MegaCrit.Sts2.Core.MonsterMoves.Intents.AbstractIntent");
var targetsType = typeof(System.Collections.Generic.IReadOnlyList<>).MakeGenericType(creatureType);
var fnType = typeof(Func<,>).MakeGenericType(targetsType, typeof(Task));

// onPerform 用表达式树包一个 Host.Noop(object)，避免编译期绑定本体类型
var noop = typeof(Host).GetMethod("Noop")!;
var arg = Expression.Parameter(targetsType, "t");
var noopFn = Expression.Lambda(fnType, Expression.Call(noop, Expression.Convert(arg, typeof(object))), arg).Compile();

object Intent(string typeName, params object[] ctorArgs)
{
    var t = GType("MegaCrit.Sts2.Core.MonsterMoves.Intents." + typeName);
    return ctorArgs.Length == 0 ? Activator.CreateInstance(t)! : Activator.CreateInstance(t, ctorArgs)!;
}

object Make(string id, bool mustPerformOnce, params object[] intents)
{
    var arr = Array.CreateInstance(intentBaseType, intents.Length);
    for (int i = 0; i < intents.Length; i++) arr.SetValue(intents[i], i);
    var ms = Activator.CreateInstance(moveStateType, new object[] { id, noopFn, arr })!;
    if (mustPerformOnce)
    {
        moveStateType.GetProperty("MustPerformOnceBeforeTransitioning")!.SetValue(ms, true);
    }
    return ms;
}

var followUpProp = moveStateType.GetProperty("FollowUpState")!;
var followUpIdProp = moveStateType.GetProperty("FollowUpStateId")!;

void Link(object from, object? to) => followUpProp.SetValue(from, to);
void LinkById(object from, string toId) => followUpIdProp.SetValue(from, toId);

/// <summary>用反射建真·本体状态机（构造签名 IEnumerable&lt;MonsterState&gt; + 初始状态）。</summary>
object MakeMachine(object[] states, object initial)
{
    var listType = typeof(System.Collections.Generic.List<>).MakeGenericType(monsterStateType);
    var list = Activator.CreateInstance(listType)!;
    var add = listType.GetMethod("Add")!;
    foreach (var s in states) add.Invoke(list, new[] { s });
    return Activator.CreateInstance(machineType, new object[] { list, initial })!;
}

var patch = mod.GetType("IntentChaos.IntentRerollPatch")!;
const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Static;

// 必须先跑适配层：所有本体成员访问都是反射解析出来的委托，没解析就是 null → 调用直接 NRE
var compatType = mod.GetType("IntentChaos.Compat")!;
var err = (string?)compatType.GetMethod("Resolve", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null);
if (err != null)
{
    Console.WriteLine($"[FAIL] 本体 API 解析失败：{err}");
    return 2;
}
Console.WriteLine("[OK] Compat.Resolve() 通过（本测试会真的调用 mod 的判定委托）");

// 适配层新加的两个读取器先自检：FollowUpState（对象引用）和 FollowUpStateId（字符串）都要读得出后继
var succOf = compatType.GetField("GetSuccessorIds", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
var succFn = succOf.GetType().GetMethod("Invoke")!;
string[]? Succ(object st) => (string[]?)succFn.Invoke(succOf, new[] { st });

var a1 = Make("A_MOVE", false, Intent("SingleAttackIntent", 8));
var a2 = Make("B_MOVE", false, Intent("SingleAttackIntent", 8));
Link(a1, a2);
LinkById(a2, "A_MOVE");
if (!(Succ(a1) ?? Array.Empty<string>()).Contains("B_MOVE") || !(Succ(a2) ?? Array.Empty<string>()).Contains("A_MOVE"))
{
    Console.WriteLine($"[FAIL] GetSuccessorIds 读不出后继：A=[{string.Join(",", Succ(a1) ?? new string[0])}] B=[{string.Join(",", Succ(a2) ?? new string[0])}]");
    return 2;
}
Console.WriteLine("[OK] Compat.GetSuccessorIds：FollowUpState 与 FollowUpStateId 两种写法都读得到");

var structuralM = patch.GetMethod("StructuralReason", F)!;
var blankM = patch.GetMethod("IsBlankMove", F)!;
var isStructuralM = patch.GetMethod("IsStructural", F)!;
var pinM = patch.GetMethod("PinReason", F)!;
var excludedM = patch.GetMethod("IsExcluded", F)!;
var oneShotM = patch.GetMethod("IsOneShotBossGrant", F)!;
var strandedM = patch.GetMethod("IsStrandedInChain", F)!;

var cfgType = mod.GetType("IntentChaos.ModConfig")!;
var cfg = cfgType.GetProperty("Instance")!.GetValue(null)!;   // 用默认值，不调 Load()（那会打 GD.Print 需要引擎）
Console.WriteLine($"默认配置：pinStructuralMoves={cfgType.GetProperty("pinStructuralMoves")!.GetValue(cfg)}"
    + $" pinBlacklistedMoves={cfgType.GetProperty("pinBlacklistedMoves")!.GetValue(cfg)}"
    + $" blacklistDangerousMoves={cfgType.GetProperty("blacklistDangerousMoves")!.GetValue(cfg)}"
    + $" preserveBossOpenings={cfgType.GetProperty("preserveBossOpenings")!.GetValue(cfg)}");
Console.WriteLine();

var heal = Intent("HealIntent");
var sleep = Intent("SleepIntent");
var hidden = Intent("HiddenIntent");
var attack = Intent("SingleAttackIntent", 8);
var multi = Intent("MultiAttackIntent", 5, 2);
var buff = Intent("BuffIntent");
var status6 = Intent("StatusIntent", 6);
var cardDebuff = Intent("CardDebuffIntent");
var deathBlowType = GType("MegaCrit.Sts2.Core.MonsterMoves.Intents.DeathBlowIntent");
var deathBlow = Activator.CreateInstance(deathBlowType, new object[] { (Func<decimal>)(() => 30m) })!;

// 我们自己的诅咒招意图（在 mod 程序集里），用来确认它不会被新判据误伤
var curseIntentType = mod.GetType("IntentChaos.CurseIntent", throwOnError: true)!;
var curseIntent = Activator.CreateInstance(curseIntentType)!;

// ---- 老规则用例（与 boss 一次性招判据无关，isBoss 传 false）----
var cases = new (string Label, object State, bool ExpectPinned, string Why)[]
{
    ("千足虫 DEAD_MOVE（零意图）", Make("DEAD_MOVE", false), true, "空白招"),
    ("建筑师 NOTHING（HiddenIntent）", Make("NOTHING", false, hidden), true, "空白招"),
    ("隐藏+攻击混合", Make("MIX", false, hidden, attack), false, "有可见意图 → 普通招"),
    ("千足虫 REATTACH（治疗+锁定）", Make("REATTACH_MOVE", true, heal), true, "本体锁定招"),
    ("实验体 RESPAWN（治疗+锁定）", Make("RESPAWN_MOVE", true, heal), true, "本体锁定招"),
    ("瀑布巨兽 EXPLODE（亡语）", Make("EXPLODE_MOVE", false, deathBlow), true, "亡语招"),
    ("千足虫 WRITHE（多重攻击）", Make("WRITHE_MOVE", false, multi), false, "普通攻击招"),
    ("巨兽 STOMP（攻击+减益+增益）", Make("STOMP_MOVE", false, attack, heal), false, "普通攻击招"),
    ("睡眠招 SLEEP_MOVE", Make("SLEEP_MOVE", false, sleep), true, "黑名单对称固定"),
};

int fail = 0;
Console.WriteLine($"{"招式",-32} {"空白",-4} {"结构性",-6} {"不换走",-5} {"可进池",-6} 依据");
Console.WriteLine(new string('-', 108));
foreach (var c in cases)
{
    var solo = MakeMachine(new[] { c.State }, c.State);
    bool blank = (bool)blankM.Invoke(null, new[] { c.State })!;
    string? sReason = (string?)structuralM.Invoke(null, new[] { c.State });
    string? pReason = (string?)pinM.Invoke(null, new object?[] { c.State, MoveId(c.State), solo, false });
    bool poolable = !(bool)isStructuralM.Invoke(null, new[] { c.State })
                 && !(bool)excludedM.Invoke(null, new[] { c.State, MoveId(c.State) });

    bool ok = (pReason != null) == c.ExpectPinned;
    if (!ok) fail++;
    string basis = sReason ?? pReason ?? "可重掷";
    if (basis.Length > 30) basis = basis.Substring(0, 30) + "…";
    Console.WriteLine($"{c.Label,-32} {(blank ? "是" : "否"),-4} {(sReason != null ? "是" : "否"),-6} "
        + $"{(pReason != null ? "是" : "否"),-5} {(poolable ? "可" : "排除"),-6} {(ok ? "[OK]" : "[BAD]")} {basis}");
}

// ---- 沙虫 TheInsatiable：真实链形 LIQUIFY → THRASH → BITE → SALIVATE → THRASH_2 → THRASH（闭环）----
// 关键点：换招时本体 SetMoveImmediate 会连 _currentState 一起搬走，所以"链头入度 0"= 换走就永久消失。
Func<bool, (object Head, object Machine)> Insatiable = loopBack =>
{
    var liquify = Make("LIQUIFY_GROUND_MOVE", false, buff, status6);
    var thrash = Make("THRASH_MOVE", false, multi);
    var bite = Make("LUNGING_BITE_MOVE", false, Intent("SingleAttackIntent", 28));
    var salivate = Make("SALIVATE_MOVE", false, buff);
    var thrash2 = Make("THRASH_MOVE_2", false, multi);
    Link(liquify, thrash);
    Link(thrash, bite);
    Link(bite, salivate);
    Link(salivate, thrash2);
    Link(thrash2, loopBack ? liquify : thrash);
    var machine = MakeMachine(new[] { liquify, bite, thrash, thrash2, salivate }, liquify);
    return (liquify, machine);
};

Console.WriteLine();
Console.WriteLine("=== boss 一次性开场招判据（IsOneShotBossGrant / IsStrandedInChain）===");
Console.WriteLine($"{"用例",-44} {"入度0",-6} {"命中",-5} {"不换走",-6} {"不进池",-6} 备注");
Console.WriteLine(new string('-', 118));

void GrantCase(string label, object head, object machine, bool isBoss, bool expectGrant, string note)
{
    bool stranded = (bool)strandedM.Invoke(null, new object?[] { machine, MoveId(head) })!;
    bool grant = (bool)oneShotM.Invoke(null, new object?[] { head, machine, isBoss })!;
    // 换走侧走的是另一条代码路径（PinReason 要自己去调这个判据），所以这里才算真的对称性检查
    string? pReason = (string?)pinM.Invoke(null, new object?[] { head, MoveId(head), machine, isBoss });
    bool pinnedAway = pReason != null;
    bool poolable = !grant;   // 池侧就是 grant 本身：命中即 continue，不进池
    bool ok = grant == expectGrant && pinnedAway == expectGrant && poolable != expectGrant;
    if (!ok) fail++;
    Console.WriteLine($"{label,-44} {(stranded ? "是" : "否"),-6} {(grant ? "固定" : "随机"),-5} "
        + $"{(pinnedAway ? "是" : "否"),-6} {(poolable ? "可" : "排除"),-6} {(ok ? "[OK]" : "[BAD]")} {note}");
}

// 1) 沙虫本体形状：boss 房 + 入度 0 + StatusIntent → 双向固定
var real = Insatiable(false);
GrantCase("沙虫 LIQUIFY（boss 房，链头入度 0）", real.Head, real.Machine,
    true, true, "吞噬倒计时+6 张慌乱逃离的唯一来源");
// 2) 同形状但普通怪：保留随机（用户要的是"boss 机制别丢"，不是"所有开场都别动"）
GrantCase("沙虫 LIQUIFY（当普通怪处理）", real.Head, real.Machine,
    false, false, "非 boss 房 → 照旧可换走、可进池");
// 3) 链条能回访（THRASH_2 指回链头）→ 不是"一次性"，不该被钉
var revisited = Insatiable(true);
GrantCase("沙虫 LIQUIFY（链尾指回链头）", revisited.Head, revisited.Machine,
    true, false, "入度>0 → 本体自己会再走到，无须固定");
// 4) 潮湿邪教徒 INCANTATION：入度 0 的链头，但意图只有 BuffIntent → 用户点名要能随机
{
    var incant = Make("INCANTATION_MOVE", false, buff);
    var dark = Make("DARK_STRIKE_MOVE", false, Intent("SingleAttackIntent", 7));
    Link(incant, dark);
    Link(dark, dark);
    var m = MakeMachine(new object[] { incant, dark }, incant);
    GrantCase("潮湿邪教徒 INCANTATION（纯自 buff）", incant, m, true, false,
        "就算在 boss 房也不钉：判据第二条（塞牌意图）不命中");
}
// 5) 女王 PUPPET_STRINGS：CardDebuffIntent 链头（本规则只管塞牌类，女王由专门特例负责）
{
    var puppet = Make("PUPPET_STRINGS_MOVE", false, cardDebuff);
    var other = Make("YOU_ARE_MINE_MOVE", false, cardDebuff);
    Link(puppet, other);
    Link(other, other);
    var m = MakeMachine(new object[] { puppet, other }, puppet);
    GrantCase("女王 PUPPET_STRINGS（CardDebuff 链头）", puppet, m, true, false,
        "不由本规则接管（另有女王开场特例）");
}
// 6) 我们自己注入的诅咒招：CurseIntent 不是 StatusIntent，绝不能被新判据钉死
{
    var head = Make("LIQ2_MOVE", false, buff, status6);
    var curse = Make("INTENT_CHAOS_CURSE_MOVE", false, curseIntent);
    var second = Make("OTHER_MOVE", false, multi);
    Link(head, second);
    LinkById(curse, "OTHER_MOVE");
    var m = MakeMachine(new object[] { head, curse, second }, head);
    GrantCase("我方诅咒招（链头，CurseIntent）", curse, m, true, false,
        "否则注入的诅咒招会被自己钉住不放");
    // 同一台机器里，真正的沙虫形状链头仍然固定（诅咒状态的存在不影响入度判定）
    GrantCase("同机器里的 LIQ2（StatusIntent 链头）", head, m, true, true, "诅咒招不影响判定");
}
// 7) 保守路径：States 里出现一个读不出后继的状态（这里是 null）→ 一律当"能回访"
{
    var (head, m) = Insatiable(false);
    var getStates = compatType.GetField("GetStates", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
    var d = getStates.GetType().GetMethod("Invoke")!.Invoke(getStates, new object[] { m })!;
    d.GetType().GetMethod("Add")!.Invoke(d, new object?[] { "GHOST", null });
    GrantCase("沙虫 LIQUIFY（池中混入读不出的状态）", head, m, true, false,
        "读不出后继就保守放行，绝不误钉");
}

Console.WriteLine();
Console.WriteLine(fail == 0
    ? "[OK] 分类符合预期：DEAD_MOVE/NOTHING/REATTACH/RESPAWN/EXPLODE/SLEEP 双向固定；WRITHE/STOMP/MIX 仍可重掷；"
        + "沙虫 LIQUIFY 在 boss 房双向固定、非 boss 房/可回访/纯自 buff/诅咒招/读不出后继 五种情况都不误钉"
    : $"[FAIL] {fail} 个用例分类不对");
return fail == 0 ? 0 : 1;

// 顶层语句会隐式生成 Program 类，所以辅助类型必须另起名字
public static class Host
{
    public static Task Noop(object targets) => Task.CompletedTask;
}
