using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace IntentChaos;

/// <summary>
/// Mod 入口：加载配置 → 运行时解析本体 API → 给 Hook.AfterDamageReceived 挂 Harmony postfix。
/// 无任何 mod 前置；解析失败只打日志并休眠，不影响游戏。
/// </summary>
[ModInitializer(nameof(OnModLoaded))]
public static class ModEntry
{
    public const string ModId = "IntentChaos";
    public const string Version = "0.3.1";

    public static void OnModLoaded()
    {
        GD.Print($"[{ModId}] v{Version} 加载中……");
        try
        {
            ModConfig.Load();
            string? err = Compat.Resolve();
            if (err != null)
            {
                GD.PushWarning($"[{ModId}] 本体 API 适配失败，mod 休眠（游戏不受影响）：{err}");
                return;
            }

            var harmony = new Harmony($"com.zcode.{ModId.ToLowerInvariant()}");
            harmony.Patch(
                Compat.AfterDamageReceived,
                postfix: new HarmonyMethod(AccessTools.Method(typeof(IntentRerollPatch), nameof(IntentRerollPatch.Postfix))));
            IntentRerollPatch.CompatReady = true;

            GD.Print(
                $"[{ModId}] 已适配当前版本（Hook 参数索引 target={Compat.IdxTarget} result={Compat.IdxResult} " +
                $"props={Compat.IdxProps} dealer={Compat.IdxDealer} cardSource={Compat.IdxCardSource}），补丁生效：" +
                "怪物被玩家直接攻击掉血 → 从招式池（含开场招）随机换成其他招；女王前三招固定；" +
                "结构性招双向固定（亡语/自爆 + 空白意图招 + 本体锁定招）：瀑布巨兽不会卡无限血，" +
                "千足虫 DEAD_MOVE 那类空意图不会被随机发给活肢体；" +
                $"结构性固定={(ModConfig.Instance.pinStructuralMoves ? "开" : "关")}，" +
                $"黑名单对称固定={(ModConfig.Instance.pinBlacklistedMoves ? "开" : "关")}，" +
                $"断链救场={(ModConfig.Instance.repairStrandedDeathBlows ? "开" : "关")}，" +
                $"boss 招式池含塞诅咒招（{(Compat.Curse.Available ? $"可用，{ModConfig.Instance.bossCurseCardId}" : $"不可用：{Compat.Curse.FailReason}")}）。");
        }
        catch (Exception e)
        {
            GD.PushError($"[{ModId}] 初始化异常，mod 休眠（游戏不受影响）：{e}");
        }
    }
}
