using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;

namespace IntentChaos;

/// <summary>
/// 诅咒招的自定义意图：
/// - 头顶动画/图标复用本体意图动画帧（config curseIntentIcon，默认 summon 紫色眼睛团）；
/// - 标题/描述来自运行时注入的本地化条目（intents:INTENT_CHAOS_CURSE.title/.description）；
/// - 类型保持 CardDebuff：不参与 IntendsToAttack 等攻击判定，玩法语义中性。
/// </summary>
public sealed class CurseIntent : AbstractIntent
{
    public override IntentType IntentType => IntentType.CardDebuff;

    protected override string IntentPrefix => "INTENT_CHAOS_CURSE";

    protected override string? SpritePath =>
        "atlases/intent_atlas.sprites/intent_" + ModConfig.Instance.curseIntentIcon + ".tres";

    // NIntent 头顶动画按此名字到 IntentAnimData 查帧——必须用本体已有的动画名
    public override string GetAnimation(IEnumerable<Creature> targets, Creature owner)
    {
        return ModConfig.Instance.curseIntentIcon;
    }
}
