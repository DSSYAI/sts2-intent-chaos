using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;

namespace IntentChaos;

/// <summary>
/// 配置：mods/IntentChaos/config.json。首次运行自动写默认值；改文件后重启游戏生效。
/// </summary>
public sealed class ModConfig
{
    /// <summary>排除明显破坏性的招式（意图为逃跑/自爆/睡眠/眩晕的不进候选池）。</summary>
    public bool blacklistDangerousMoves { get; set; } = true;

    /// <summary>黑名单对称保护：黑名单里的招既不进池，也不会被从当前换上换走。
    /// 因为"不进池"是单向门——一旦换走就永远回不来，等于把这招从怪物的招式表里删掉
    /// （睡眠/眩晕/逃跑这类流程会被永久打断）。
    /// 注意：亡语/自爆招是<b>无条件</b>固定的，不受本项影响；
    /// 空白意图招与本体锁定招由下面的 pinStructuralMoves 管。</summary>
    public bool pinBlacklistedMoves { get; set; } = true;

    /// <summary>结构性招式双向固定（v0.3.1，默认开）：下面两类招既不随机进池、也不会被换走——
    /// ① 空白意图招（零意图，或意图全是 HiddenIntent，画面上什么都不显示）：千足虫
    ///    （DecimillipedeSegment）断肢重生链的链头 DEAD_MOVE 就是这种，发给活肢体就会变成
    ///    "什么都不做、再打也不变"的空意图；
    /// ② 本体锁定招（MustPerformOnceBeforeTransitioning）：千足虫 REATTACH_MOVE、
    ///    实验体 RESPAWN_MOVE、瀑布巨兽 ABOUT_TO_BLOW_MOVE——换进来连本体自己都换不走，
    ///    意图会钉死，还会挡住本体的死亡/重生脚本。
    /// 关掉它可以拿到更多花样（比如随机给活体发"回血/重生"招），但可能重新引入卡死与脚本错乱。
    /// 亡语/自爆招不受本项影响，永远固定。</summary>
    public bool pinStructuralMoves { get; set; } = true;

    /// <summary>boss 的一次性开场招固定（v0.3.2，默认开）：状态机里"没有任何后继指向它"的链头招，
    /// 本体一辈子只会走到一次；而 <c>SetMoveImmediate</c> 换招时连状态机的当前位置一起搬走
    /// （本体源码：NextMove = state; MoveStateMachine.ForceCurrentState(state)），
    /// 所以一旦把它换走，本体的脚本就永久改道、这招再也不会自己回来。
    /// 判据只认"boss 房 + 入度 0 的链头 + 意图含 StatusIntent（往玩家牌堆塞牌）"三条游戏内属性，
    /// v0.111.0 全库实测只有沙虫 TheInsatiable 的 LIQUIFY_GROUND_MOVE 命中：
    /// 换走它 = 吞噬倒计时（SandpitPower）和 6 张慌乱逃离整场消失；
    /// 反过来把它随机发到中盘 = 同一玩家身上挂两条独立倒计时（SandpitPower 是 Instanced）。
    /// 所以双向固定：不换走、也不进池。普通怪的同类开场照旧全随机。
    /// 关掉它则沙虫的开场招重新变成每回合约 1/5 的摇号，boss 机制可能整场不出现。</summary>
    public bool preserveBossOpenings { get; set; } = true;

    /// <summary>救场：怪物处于本体标记的"无限血/死亡结算中"阶段（瀑布巨兽血归零后的约爆状态）
    /// 而它的自爆亡语招曾被换走时，把亡语招强行放回去，让这场战斗能正常结束。
    /// 关掉它则旧存档里已经卡死的场次会一直打不死。</summary>
    public bool repairStrandedDeathBlows { get; set; } = true;

    /// <summary>额外按招式状态 id 排除（如 "MAGIC_BOMB"——魔法骑士需要蓄力预警的核弹）。</summary>
    public List<string> excludeMoveIds { get; set; } = new();

    /// <summary>女王开场固定不换的招数（已完成招数少于此值时不重掷，默认 3：她的开场三连招）。</summary>
    public int queenFixedOpeningMoves { get; set; } = 3;

    /// <summary>女王开场招的状态 id 集合：当前招属于集合且未出满固定招数时保持固定；
    /// 爪牙提前死亡时本体路由直接跳到攻击循环（不在集合内），固定窗口即刻结束。</summary>
    public List<string> queenFixedMoveIds { get; set; } = new()
    {
        "PUPPET_STRINGS_MOVE",
        "YOU_ARE_MINE_MOVE",
        "BURN_BRIGHT_FOR_ME_MOVE"
    };

    /// <summary>是否向 boss 招式池注入"塞诅咒牌"招。</summary>
    public bool bossCurseMove { get; set; } = true;

    /// <summary>诅咒招塞入的诅咒牌（本体 CardType.Curse 的 ModelId Entry，如 INJURY/REGRET/GREED）。</summary>
    public string bossCurseCardId { get; set; } = "INJURY";

    /// <summary>诅咒招头顶动画：复用本体意图动画名。summon=紫色眼睛团（召唤爪牙图案）。</summary>
    public string curseIntentIcon { get; set; } = "summon";

    /// <summary>诅咒招鼠标悬停标题。</summary>
    public string curseIntentTitle { get; set; } = "强力诅咒";

    /// <summary>诅咒招鼠标悬停描述。</summary>
    public string curseIntentDescription { get; set; } = "这个敌人将会给你的牌库里添加一张强力诅咒牌";

    /// <summary>在日志里打印触发/重掷追踪（godot.log 可查）。</summary>
    public bool logRerolls { get; set; } = false;

    public static ModConfig Instance { get; private set; } = new();

    public static void Load()
    {
        string dir = Path.GetDirectoryName(typeof(ModEntry).Assembly.Location) ?? ".";
        string path = Path.Combine(dir, "config.json");
        try
        {
            if (!File.Exists(path))
            {
                File.WriteAllText(path,
                    JsonSerializer.Serialize(new ModConfig(), new JsonSerializerOptions { WriteIndented = true }));
                GD.Print("[IntentChaos] 已生成默认配置 " + path);
            }
            var cfg = JsonSerializer.Deserialize<ModConfig>(File.ReadAllText(path)) ?? new ModConfig();
            cfg.queenFixedOpeningMoves = Math.Max(0, cfg.queenFixedOpeningMoves);
            cfg.bossCurseCardId = (cfg.bossCurseCardId ?? "INJURY").Trim().ToUpperInvariant();
            // 头顶动画必须是本体 IntentAnimData 里存在的动画名（含帧动画的组），否则意图渲染会崩
            string[] validIcons =
            {
                "attack", "buff", "card_debuff", "debuff", "defend", "escape",
                "heal", "sleep", "status", "stun", "summon", "unknown"
            };
            cfg.curseIntentIcon = cfg.curseIntentIcon?.Trim() ?? "";
            if (((IList<string>)validIcons).IndexOf(cfg.curseIntentIcon) < 0)
            {
                cfg.curseIntentIcon = "summon";
            }
            Instance = cfg;
        }
        catch (Exception e)
        {
            GD.PushWarning($"[IntentChaos] 读取 config.json 失败，使用默认配置：{e.Message}");
            Instance = new ModConfig();
        }
    }
}
