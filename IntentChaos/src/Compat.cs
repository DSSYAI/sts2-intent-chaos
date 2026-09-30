using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.ValueProps;

namespace IntentChaos;

/// <summary>
/// 版本适配层：对本体成员零编译期绑定。
/// 所有成员按"名称 + 参数形状"在启动时反射解析，并编译成表达式树委托（热路径零反射开销）。
/// 方法加参数/重载变化不影响；解析失败时 Resolve() 返回原因，mod 静默休眠。
/// 若未来版本改名/删除成员，需在 Resolve() 的成员清单里补上新名字。
/// </summary>
public static class Compat
{
    // ---- Hook.AfterDamageReceived 补丁点与参数索引（按参数名映射，形状漂移安全） ----
    public static MethodInfo? AfterDamageReceived;
    public static int IdxTarget = -1;
    public static int IdxResult = -1;
    public static int IdxProps = -1;
    public static int IdxDealer = -1;
    public static int IdxCardSource = -1;

    // ---- 触发过滤 ----
    public static Func<Creature, MonsterModel?> GetMonster = null!;
    public static Func<Creature, ICombatState?> GetCombatState = null!;
    public static Func<Creature, bool> GetIsDead = null!;
    public static Func<Creature, CombatSide> GetSide = null!;
    public static Func<Creature, string> GetName = null!;
    public static Func<MonsterModel, bool> GetIsPerformingMove = null!;
    public static Func<MonsterModel, MoveState> GetNextMove = null!;
    public static Func<MoveState, string> GetMoveId = null!;
    public static Func<Creature, HpDisplay> GetHpDisplay = null!;
    public static Func<CardModel, CardType> GetCardType = null!;
    public static Func<ValueProp, bool> IsPoweredAttack = null!;

    // ---- 池随机重掷 ----
    public static Func<MonsterModel, MonsterMoveStateMachine?> GetMoveStateMachine = null!;
    public static Func<MonsterMoveStateMachine, Dictionary<string, MonsterState>> GetStates = null!;
    public static Func<MonsterMoveStateMachine, List<MonsterState>> GetStateLog = null!;
    public static Action<MonsterModel, MoveState> SetMoveImmediate = null!;
    public static Action<MonsterModel, MoveState> SetMoveImmediateForce = null!;
    public static Func<MoveState, bool> GetCanTransitionAway = null!;
    public static Func<MoveState, bool> GetMustPerformOnce = null!;
    public static Func<MoveState, IReadOnlyList<AbstractIntent>> GetIntents = null!;
    public static Func<MonsterState, string> GetStateId = null!;

    /// <summary>某状态在链条上的后继（会走到哪些 state id）。
    /// 返回 <b>null</b> 表示"读不出来"（本体的分支状态换了形状）——调用方必须保守处理，
    /// 当作它可能指向任何状态，绝不能据此断定某招"再也回不来"。</summary>
    public static Func<MonsterState, string[]?> GetSuccessorIds = null!;

    public static Func<MonsterModel, ModelId> GetModelId = null!;
    public static Func<ICombatState, EncounterModel?> GetEncounter = null!;
    public static Func<EncounterModel, RoomType> GetRoomType = null!;
    public static Func<MonsterModel, RunRngSet?> GetRunRng = null!;
    public static Func<RunRngSet, Rng> GetMonsterAi = null!;
    public static Func<Rng, int, int> RngNextInt = null!;

    // ---- boss 诅咒招（可选功能，解析失败不影响核心） ----
    public static class Curse
    {
        public static bool Available;
        public static string FailReason = "";
        public static Func<ModelId, CardModel?> GetCanonicalCard = null!;
        public static Func<Creature, Player?> GetPlayer = null!;
        public static Func<Creature, Player?> GetPetOwner = null!;
        public static Func<ICombatState, CardModel, Player, CardModel?> CreateCard = null!;
        public static Func<IEnumerable<CardModel>, Player, Task> AddCursesToDeck = null!;
        public static Func<CardModel, Player?, Task> AddGeneratedToCombat = null!;
        public static Action<MoveState, string> SetFollowUpStateId = null!;
        public static Action<string, Dictionary<string, string>> InjectLocEntries = null!;
    }

    /// <summary>解析全部成员。成功返回 null；失败返回原因（mod 休眠）。</summary>
    public static string? Resolve()
    {
        try
        {
            ResolvePatchTarget();

            GetMonster = Getter<Creature, MonsterModel>(P(typeof(Creature), "Monster"));
            GetCombatState = Getter<Creature, ICombatState>(P(typeof(Creature), "CombatState"));
            GetIsDead = Getter<Creature, bool>(P(typeof(Creature), "IsDead"));
            GetSide = Getter<Creature, CombatSide>(P(typeof(Creature), "Side"));
            GetName = Getter<Creature, string>(P(typeof(Creature), "Name"));
            GetNextMove = Getter<MonsterModel, MoveState>(P(typeof(MonsterModel), "NextMove"));
            GetIsPerformingMove = Getter<MonsterModel, bool>(P(typeof(MonsterModel), "IsPerformingMove"));
            GetMoveId = Getter<MoveState, string>(P(typeof(MoveState), "Id"));
            GetHpDisplay = Getter<Creature, HpDisplay>(P(typeof(Creature), "HpDisplay"));
            GetCardType = Getter<CardModel, CardType>(P(typeof(CardModel), "Type"));
            IsPoweredAttack = ResolveIsPoweredAttack();

            GetMoveStateMachine = Getter<MonsterModel, MonsterMoveStateMachine>(P(typeof(MonsterModel), "MoveStateMachine"));
            GetStates = Getter<MonsterMoveStateMachine, Dictionary<string, MonsterState>>(P(typeof(MonsterMoveStateMachine), "States"));
            GetStateLog = Getter<MonsterMoveStateMachine, List<MonsterState>>(P(typeof(MonsterMoveStateMachine), "StateLog"));
            SetMoveImmediate = BuildSetMoveImmediate(forceTransition: false);
            SetMoveImmediateForce = BuildSetMoveImmediate(forceTransition: true);
            GetCanTransitionAway = Getter<MoveState, bool>(P(typeof(MonsterState), "CanTransitionAway"));
            GetMustPerformOnce = Getter<MoveState, bool>(P(typeof(MoveState), "MustPerformOnceBeforeTransitioning"));
            GetIntents = Getter<MoveState, IReadOnlyList<AbstractIntent>>(P(typeof(MoveState), "Intents"));
            GetStateId = Getter<MonsterState, string>(P(typeof(MonsterState), "Id"));
            GetSuccessorIds = BuildSuccessorIds();
            GetModelId = Getter<MonsterModel, ModelId>(P(typeof(MonsterModel), "Id"));
            GetEncounter = Getter<ICombatState, EncounterModel>(P(typeof(CombatState), "Encounter"));
            GetRoomType = Getter<EncounterModel, RoomType>(P(typeof(EncounterModel), "RoomType"));
            GetRunRng = Getter<MonsterModel, RunRngSet>(P(typeof(MonsterModel), "RunRng"));
            GetMonsterAi = Getter<RunRngSet, Rng>(P(typeof(RunRngSet), "MonsterAi"));
            RngNextInt = Invoke<Rng, int, int>(M(typeof(Rng), "NextInt", 1));

            ResolveCurse();
            return null;
        }
        catch (Exception e)
        {
            return e.Message;
        }
    }

    private static void ResolveCurse()
    {
        try
        {
            // ModelDb.GetByIdOrNull<CardModel>(ModelId) —— 泛型静态，按 id 安全取规范卡（不存在返回 null）
            var getById = typeof(ModelDb).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "GetByIdOrNull" && m.GetParameters().Length == 1)
                .FirstOrDefault(m => m.GetParameters()[0].ParameterType == typeof(ModelId))
                ?? throw new MissingMethodException(nameof(ModelDb), "GetByIdOrNull<CardModel>");
            Curse.GetCanonicalCard = StaticCall<ModelId, CardModel>(getById.MakeGenericMethod(typeof(CardModel)));

            Curse.GetPlayer = Getter<Creature, Player>(P(typeof(Creature), "Player"));
            Curse.GetPetOwner = Getter<Creature, Player>(P(typeof(Creature), "PetOwner"));
            // 战斗副本必须经 CombatState.CreateCard 注册进战斗状态——
            // RunState.CreateCard 的副本被抽到手里时会炸掉回合循环（v0.2.0 的事故）
            Curse.CreateCard = Invoke2<ICombatState, CardModel, Player, CardModel>(M(typeof(ICombatState), "CreateCard", 2));
            Curse.AddCursesToDeck = StaticCall<IEnumerable<CardModel>, Player, Task>(M(typeof(CardPileCmd), "AddCursesToDeck", 2));

            // CardPileCmd.AddGeneratedCardToCombat(card, PileType.Draw, creator, CardPilePosition.Random) —— 固定塞抽牌堆随机位
            var mi = M(typeof(CardPileCmd), "AddGeneratedCardToCombat", 4);
            var card = Expression.Parameter(typeof(CardModel), "card");
            var creator = Expression.Parameter(typeof(Player), "creator");
            var call = Expression.Call(mi, card,
                Expression.Constant(PileType.Draw, typeof(PileType)),
                creator,
                Expression.Constant(CardPilePosition.Random, typeof(CardPilePosition)));
            Curse.AddGeneratedToCombat = Expression.Lambda<Func<CardModel, Player?, Task>>(
                Expression.Convert(call, typeof(Task)), card, creator).Compile();

            // FollowUpStateId 是 init-only 属性，运行时反射赋值（本体动态 MoveState 必须有指向已注册状态的 FollowUp）
            Curse.SetFollowUpStateId = PropSetter<MoveState, string>(typeof(MoveState), "FollowUpStateId");

            // 本地化注入：LocManager.Instance.GetTable(table).MergeWith(dict)——给 "intents" 表
            // 塞自定义意图的 title/description 条目（LocString 缺 key 会直接抛异常，必须先注入）
            var instanceProp = typeof(LocManager).GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)
                ?? throw new MissingMemberException(nameof(LocManager), "Instance");
            var getTable = M(typeof(LocManager), "GetTable", 1);
            var merge = M(typeof(LocTable), "MergeWith", 1);
            var tableArg = Expression.Parameter(typeof(string), "table");
            var dictArg = Expression.Parameter(typeof(Dictionary<string, string>), "dict");
            var mergeCall = Expression.Call(
                Expression.Call(Expression.Property(null, instanceProp), getTable, tableArg),
                merge, dictArg);
            Curse.InjectLocEntries = Expression.Lambda<Action<string, Dictionary<string, string>>>(mergeCall, tableArg, dictArg).Compile();
            Curse.Available = true;
        }
        catch (Exception e)
        {
            Curse.Available = false;
            Curse.FailReason = e.Message;
        }
    }

    /// <summary>
    /// 解析 Hook.AfterDamageReceived（全游戏唯一触发点在 CreatureCmd.Damage 收尾）。
    /// 取参数最多的重载，参数索引优先按参数名映射；名字对不上时对已知形状做位置回退。
    /// </summary>
    private static void ResolvePatchTarget()
    {
        const string name = "AfterDamageReceived";
        AfterDamageReceived = typeof(Hook).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == name)
            .OrderByDescending(m => m.GetParameters().Length)
            .FirstOrDefault(m =>
            {
                var ps = m.GetParameters();
                return ps.Length >= 6
                    && ps.Count(p => p.Name is "target" or "result" or "props" or "dealer") >= 4;
            })
            ?? throw new MissingMethodException(typeof(Hook).Name, name);

        var ps = AfterDamageReceived.GetParameters();
        IdxTarget = IndexOfParam(ps, "target");
        IdxResult = IndexOfParam(ps, "result");
        IdxProps = IndexOfParam(ps, "props");
        IdxDealer = IndexOfParam(ps, "dealer");
        IdxCardSource = IndexOfParam(ps, "cardSource");

        // 位置回退：8 参形状 (choiceContext, runState, combatState, target, result, props, dealer, cardSource)
        if (IdxTarget < 0 && ps.Length == 8)
        {
            IdxTarget = 3;
            IdxResult = 4;
            IdxProps = 5;
            IdxDealer = 6;
            IdxCardSource = 7;
        }

        if (IdxTarget < 0 || IdxResult < 0 || IdxProps < 0 || IdxDealer < 0 || IdxCardSource < 0)
            throw new MissingMethodException(typeof(Hook).Name, $"{name}: 无法映射 target/result/props/dealer/cardSource 参数位置");
    }

    /// <summary>
    /// 本体 ValuePropExtensions.IsPoweredAttack：HasFlag(Move) 且非 Unpowered。
    /// 官方语义：Move=攻击卡与怪物攻击，Unpowered=遗物/药水/能力伤害。
    /// 优先反射调用本体实现保证语义一致；找不到时用枚举标志本地复刻兜底。
    /// </summary>
    private static Func<ValueProp, bool> ResolveIsPoweredAttack()
    {
        var mi = typeof(MonsterModel).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .FirstOrDefault(m => m.Name == "IsPoweredAttack"
                && m.GetParameters().Length == 1
                && m.GetParameters()[0].ParameterType == typeof(ValueProp));
        if (mi != null)
        {
            var arg = Expression.Parameter(typeof(ValueProp), "props");
            return Expression.Lambda<Func<ValueProp, bool>>(Expression.Call(mi, arg), arg).Compile();
        }
        return props => props.HasFlag(ValueProp.Move) && !props.HasFlag(ValueProp.Unpowered);
    }

    private static int IndexOfParam(ParameterInfo[] ps, string name)
    {
        for (int i = 0; i < ps.Length; i++)
        {
            if (string.Equals(ps[i].Name, name, StringComparison.Ordinal))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// SetMoveImmediate(MoveState, bool forceTransition = false)——第二参可选，
    /// 反射参数数含默认参，这里按首参类型匹配并以 <paramref name="forceTransition"/> 显式补齐调用。
    /// forceTransition=true 用于"救回被换走的自爆亡语"（本体自己也是这么进 ABOUT_TO_BLOW 的）。
    /// </summary>
    private static Action<MonsterModel, MoveState> BuildSetMoveImmediate(bool forceTransition)
    {
        var mi = typeof(MonsterModel)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
            .Where(m => m.Name == "SetMoveImmediate" && !m.IsGenericMethodDefinition)
            .OrderBy(m => m.GetParameters().Length)
            .FirstOrDefault(m => m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType == typeof(MoveState))
            ?? throw new MissingMethodException(nameof(MonsterModel), "SetMoveImmediate");
        var ps = mi.GetParameters();
        var self = Expression.Parameter(typeof(MonsterModel), "self");
        var state = Expression.Parameter(typeof(MoveState), "state");
        var converted = Expression.Convert(state, ps[0].ParameterType);
        Expression body = ps.Length == 1
            ? Expression.Call(Expression.Convert(self, mi.DeclaringType!), mi, converted)
            : Expression.Call(Expression.Convert(self, mi.DeclaringType!), mi, converted,
                Expression.Constant(forceTransition, ps[1].ParameterType));
        return Expression.Lambda<Action<MonsterModel, MoveState>>(body, self, state).Compile();
    }

    /// <summary>
    /// 链条后继读取：MoveState 走公开的 FollowUpState / FollowUpStateId；
    /// 分支状态（RandomBranchState / ConditionalBranchState）用"名叫 States 的集合 + 元素里名叫
    /// stateId/id 的字段"这种形状约定去读，读不准就返回 null（调用方保守处理）。
    /// 只在 boss 的一次性开场招判定里按需用，不在每次伤害的热路径上。
    /// </summary>
    private static Func<MonsterState, string[]?> BuildSuccessorIds()
    {
        var getFollowUp = Getter<MoveState, MonsterState>(P(typeof(MoveState), "FollowUpState"));
        var getFollowUpId = Getter<MoveState, string>(P(typeof(MoveState), "FollowUpStateId"));
        var getId = Getter<MonsterState, string>(P(typeof(MonsterState), "Id"));
        const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        return st =>
        {
            try
            {
                if (st == null)
                {
                    return null;
                }
                if (st is MoveState mst)
                {
                    var target = getFollowUp(mst);
                    if (target != null)
                    {
                        return new[] { getId(target) };
                    }
                    var byId = getFollowUpId(mst);
                    return string.IsNullOrEmpty(byId) ? Array.Empty<string>() : new[] { byId };
                }
                // 分支状态：自己的类型（含基类）上找名为 States 的集合属性
                object? seq = null;
                for (Type? t = st.GetType(); t != null && seq == null; t = t.BaseType)
                {
                    var pi = t.GetProperty("States", F);
                    if (pi != null && typeof(System.Collections.IEnumerable).IsAssignableFrom(pi.PropertyType) && pi.PropertyType != typeof(string))
                    {
                        seq = pi.GetValue(st);
                    }
                }
                if (seq is not System.Collections.IEnumerable list)
                {
                    return null;
                }
                var ids = new List<string>();
                foreach (var item in list)
                {
                    if (item == null)
                    {
                        return null;
                    }
                    string? found = null;
                    foreach (var name in new[] { "stateId", "id", "Id", "StateId" })
                    {
                        var fi = item.GetType().GetField(name, F);
                        if (fi != null) { found = fi.GetValue(item) as string; break; }
                        var pp = item.GetType().GetProperty(name, F);
                        if (pp != null) { found = pp.GetValue(item) as string; break; }
                    }
                    if (string.IsNullOrEmpty(found))
                    {
                        return null; // 元素形状不认识 → 不知道它能去哪
                    }
                    ids.Add(found!);
                }
                return ids.ToArray();
            }
            catch
            {
                return null;
            }
        };
    }

    private static PropertyInfo P(Type decl, string name)
    {
        return decl.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
            ?? throw new MissingMemberException(decl.Name, name);
    }

    private static MethodInfo M(Type decl, string name, int paramCount)
    {
        return decl.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(m => m.Name == name && m.GetParameters().Length == paramCount && !m.IsGenericMethodDefinition)
            .OrderBy(m => m.IsPublic ? 0 : 1)
            .FirstOrDefault()
            ?? throw new MissingMethodException(decl.Name, $"{name}/{paramCount}参");
    }

    private static Func<TIn, TOut> Getter<TIn, TOut>(PropertyInfo p)
    {
        var self = Expression.Parameter(typeof(TIn), "self");
        var body = Expression.Convert(
            Expression.Property(Expression.Convert(self, p.DeclaringType!), p),
            typeof(TOut));
        return Expression.Lambda<Func<TIn, TOut>>(body, self).Compile();
    }

    private static Action<T, R> PropSetter<T, R>(Type decl, string name)
    {
        var p = P(decl, name);
        var self = Expression.Parameter(typeof(T), "self");
        var val = Expression.Parameter(typeof(R), "value");
        return Expression.Lambda<Action<T, R>>(
            Expression.Assign(
                Expression.Property(Expression.Convert(self, p.DeclaringType!), p),
                Expression.Convert(val, p.PropertyType)),
            self, val).Compile();
    }

    private static Func<TIn, TOut> Invoke<TIn, TOut>(MethodInfo m)
    {
        var self = Expression.Parameter(typeof(TIn), "self");
        var body = Expression.Convert(
            Expression.Call(Expression.Convert(self, m.DeclaringType!), m),
            typeof(TOut));
        return Expression.Lambda<Func<TIn, TOut>>(body, self).Compile();
    }

    private static Func<TIn, TArg, TOut> Invoke<TIn, TArg, TOut>(MethodInfo m)
    {
        var self = Expression.Parameter(typeof(TIn), "self");
        var arg = Expression.Parameter(typeof(TArg), "arg");
        var a = Expression.Convert(arg, m.GetParameters()[0].ParameterType);
        var body = Expression.Convert(
            Expression.Call(Expression.Convert(self, m.DeclaringType!), m, a),
            typeof(TOut));
        return Expression.Lambda<Func<TIn, TArg, TOut>>(body, self, arg).Compile();
    }

    private static Func<TSelf, T1, T2, TOut> Invoke2<TSelf, T1, T2, TOut>(MethodInfo m)
    {
        var self = Expression.Parameter(typeof(TSelf), "self");
        var a1 = Expression.Parameter(typeof(T1), "a1");
        var a2 = Expression.Parameter(typeof(T2), "a2");
        var p1 = Expression.Convert(a1, m.GetParameters()[0].ParameterType);
        var p2 = Expression.Convert(a2, m.GetParameters()[1].ParameterType);
        var body = Expression.Convert(
            Expression.Call(Expression.Convert(self, m.DeclaringType!), m, p1, p2),
            typeof(TOut));
        return Expression.Lambda<Func<TSelf, T1, T2, TOut>>(body, self, a1, a2).Compile();
    }

    private static Action<TIn, TArg> InvokeAct<TIn, TArg>(MethodInfo m)
    {
        var self = Expression.Parameter(typeof(TIn), "self");
        var arg = Expression.Parameter(typeof(TArg), "arg");
        var a = Expression.Convert(arg, m.GetParameters()[0].ParameterType);
        return Expression.Lambda<Action<TIn, TArg>>(
            Expression.Call(Expression.Convert(self, m.DeclaringType!), m, a), self, arg).Compile();
    }

    private static Func<TIn, TOut> StaticCall<TIn, TOut>(MethodInfo m)
    {
        var arg = Expression.Parameter(typeof(TIn), "arg");
        var a = Expression.Convert(arg, m.GetParameters()[0].ParameterType);
        var body = Expression.Convert(Expression.Call(null, m, a), typeof(TOut));
        return Expression.Lambda<Func<TIn, TOut>>(body, arg).Compile();
    }

    private static Func<T1, T2, TOut> StaticCall<T1, T2, TOut>(MethodInfo m)
    {
        var a1 = Expression.Parameter(typeof(T1), "a1");
        var a2 = Expression.Parameter(typeof(T2), "a2");
        var p1 = Expression.Convert(a1, m.GetParameters()[0].ParameterType);
        var p2 = Expression.Convert(a2, m.GetParameters()[1].ParameterType);
        var body = Expression.Convert(Expression.Call(null, m, p1, p2), typeof(TOut));
        return Expression.Lambda<Func<T1, T2, TOut>>(body, a1, a2).Compile();
    }
}
