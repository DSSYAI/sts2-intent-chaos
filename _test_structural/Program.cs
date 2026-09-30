// IntentChaos「结构性招」判定的离线回归测试（不需要启动游戏）。
// 把游戏本体 dll 与 mod dll 加载进来，用反射造出几个和本体怪物同形状的 MoveState，
// 直接调用 IntentRerollPatch 的私有判定方法，验证 v0.3.1 的双向固定规则分类是否正确。
//
// 形状全部取自 _sts2_decomp 反编译的真实怪物招式：
//   DecimillipedeSegment（千足虫肢体，用户报的怪） DEAD_MOVE     = 零意图                      → 固定
//   DecimillipedeSegment                           REATTACH_MOVE = HealIntent + MustPerformOnce → 固定
//   DecimillipedeSegment                           WRITHE_MOVE   = MultiAttackIntent            → 可重掷
//   WaterfallGiant（瀑布巨兽）                      EXPLODE_MOVE  = DeathBlowIntent              → 固定
//   Architect / BigDummy                            NOTHING       = HiddenIntent                 → 固定
//   BygoneEffigy / SlumberingBeetle                 SLEEP_MOVE    = SleepIntent                  → 固定（黑名单对称）
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

var structuralM = patch.GetMethod("StructuralReason", F)!;
var blankM = patch.GetMethod("IsBlankMove", F)!;
var isStructuralM = patch.GetMethod("IsStructural", F)!;
var pinM = patch.GetMethod("PinReason", F)!;
var excludedM = patch.GetMethod("IsExcluded", F)!;

var cfgType = mod.GetType("IntentChaos.ModConfig")!;
var cfg = cfgType.GetProperty("Instance")!.GetValue(null)!;   // 用默认值，不调 Load()（那会打 GD.Print 需要引擎）
Console.WriteLine($"默认配置：pinStructuralMoves={cfgType.GetProperty("pinStructuralMoves")!.GetValue(cfg)}"
    + $" pinBlacklistedMoves={cfgType.GetProperty("pinBlacklistedMoves")!.GetValue(cfg)}"
    + $" blacklistDangerousMoves={cfgType.GetProperty("blacklistDangerousMoves")!.GetValue(cfg)}");
Console.WriteLine();

var heal = Intent("HealIntent");
var sleep = Intent("SleepIntent");
var hidden = Intent("HiddenIntent");
var attack = Intent("SingleAttackIntent", 8);
var multi = Intent("MultiAttackIntent", 5, 2);
var deathBlowType = GType("MegaCrit.Sts2.Core.MonsterMoves.Intents.DeathBlowIntent");
var deathBlow = Activator.CreateInstance(deathBlowType, new object[] { (Func<decimal>)(() => 30m) })!;

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
    bool blank = (bool)blankM.Invoke(null, new[] { c.State })!;
    string? sReason = (string?)structuralM.Invoke(null, new[] { c.State });
    string? pReason = (string?)pinM.Invoke(null, new[] { c.State, MoveId(c.State) });
    bool poolable = !(bool)isStructuralM.Invoke(null, new[] { c.State })
                 && !(bool)excludedM.Invoke(null, new[] { c.State, MoveId(c.State) });

    bool ok = (pReason != null) == c.ExpectPinned;
    if (!ok) fail++;
    string basis = sReason ?? pReason ?? "可重掷";
    if (basis.Length > 30) basis = basis.Substring(0, 30) + "…";
    Console.WriteLine($"{c.Label,-32} {(blank ? "是" : "否"),-4} {(sReason != null ? "是" : "否"),-6} "
        + $"{(pReason != null ? "是" : "否"),-5} {(poolable ? "可" : "排除"),-6} {(ok ? "[OK]" : "[BAD]")} {basis}");
}

Console.WriteLine();
// 交叉校验：本体的真实招式图（直接反射读千足虫的 GenerateMoveStateMachine 不可行，需要实例化怪物），
// 所以这里改为核对"规则集合是否覆盖了用户报的两条"
Console.WriteLine(fail == 0
    ? "[OK] 分类符合预期：DEAD_MOVE/NOTHING/REATTACH/RESPAWN/EXPLODE/SLEEP 全部双向固定；WRITHE/STOMP/MIX 仍可重掷"
    : $"[FAIL] {fail} 个用例分类不对");
return fail == 0 ? 0 : 1;

// 顶层语句会隐式生成 Program 类，所以辅助类型必须另起名字
public static class Host
{
    public static Task Noop(object targets) => Task.CompletedTask;
}
