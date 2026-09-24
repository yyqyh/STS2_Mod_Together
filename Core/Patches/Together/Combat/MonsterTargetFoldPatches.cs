using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using Together.Core.Combat;

namespace Together.Core.Patches.Combat;

/// <summary>
/// 招式折叠的作用域开关。
/// </summary>
/// <remarks>
/// 挂在 <c>MonsterModel.PerformMove</c> 上而不是 <c>MoveState.PerformMove</c> 上：
/// 只有前者拿得到"是哪只怪"，排除名单要按怪判。作用域收尾挂在返回的 <c>Task</c> 之后
/// （<c>PerformMove</c> 是 async 方法，Harmony 打在存根上，Postfix 拿到的是还没跑完的 Task）。
/// </remarks>
[HarmonyPatch(typeof(MonsterModel), nameof(MonsterModel.PerformMove))]
internal static class MonsterMoveScopePatch
{
    [HarmonyPrefix]
    private static void Prefix(MonsterModel __instance, ref MonsterTargetFold.Scope? __state)
    {
        __state = MonsterTargetFold.Enter(__instance, __instance.CombatState?.PlayerCreatures ?? []);
    }

    [HarmonyPostfix]
    private static void Postfix(ref Task __result, MonsterTargetFold.Scope? __state)
    {
        if (__state is not null && __result is not null)
        {
            __result = MonsterTargetFold.ExitAfterAsync(__result, __state);
        }
    }
}

/// <summary>
/// 把招式收到的目标折叠成"每组一个代表"。
/// </summary>
/// <remarks>
/// 只改这一个入参：攻击伤害走 <c>FromMonster → AttackCommand.GetPossibleTargets() → PlayerCreatures</c>，
/// 不经过这里，所以伤害量不变（见 <see cref="MonsterTargetFold" /> 的说明）。
/// </remarks>
[HarmonyPatch(typeof(MoveState), nameof(MoveState.PerformMove))]
internal static class MoveStateTargetFoldPatch
{
    [HarmonyPrefix]
    private static void Prefix(ref IEnumerable<Creature> __0)
    {
        __0 = MonsterTargetFold.Fold(__0);
    }
}

/// <summary>
/// 折叠后"塞牌量砍半"的补偿：生成进战斗的牌按组补齐份数。
/// </summary>
/// <remarks>
/// 挑这个挂点是因为它是<b>所有"生成一张牌进战斗"的非泛型必经之路</b>：
/// <c>CardPileCmd.AddToCombatAndPreview&lt;T&gt;</c>（沙漏的凋萎、窒息兽的眩晕、机械骑士的灼伤……）
/// 与怪自己直接调用的 <c>AddGeneratedCardToCombat</c>（噪声机、魂鱼、贪食者）都走它。
/// 泛型入口没法逐个实例化去打补丁，打这个就够了。
/// </remarks>
[HarmonyPatch(typeof(CardPileCmd), nameof(CardPileCmd.AddGeneratedCardToCombat))]
internal static class GeneratedCardCompensationPatch
{
    [HarmonyPostfix]
    private static void Postfix(
        CardModel __0,
        PileType __1,
        Player? __2,
        CardPilePosition __3,
        ref Task<CardPileAddResult> __result)
    {
        if (__result is not null && MonsterTargetFold.CompensationActive)
        {
            __result = MonsterTargetFold.CompensateAsync(__result, __0, __1, __2, __3);
        }
    }
}

/// <summary>
/// 补偿之二：<b>自己造卡、再直接塞进战斗牌堆</b>那条路（`Add(card, PileType.X)`）。
/// </summary>
/// <remarks>
/// <para>
/// 本体生成入口 <c>AddGeneratedCardToCombat(s)</c> 走的是 <c>Add</c> 的 <b>CardPile 重载</b>
/// （由上面那条补丁处理）；而 mod 怪常见的写法是
/// <c>combatState.CreateCard&lt;T&gt;(player)</c> 之后直接
/// <c>CardPileCmd.Add(card, PileType.Discard)</c> —— 那条走 <b>PileType 重载</b>，原来没人补。
/// 两条路不重叠，所以不会重复计数。
/// </para>
/// <para>
/// 只认"这次刚进战斗"的牌（内部按 <c>oldPile == null</c> 判）：洗牌、出牌结果堆、弃牌这些
/// "移动已有牌"的操作一律不碰。
/// </para>
/// </remarks>
[HarmonyPatch(
    typeof(CardPileCmd),
    nameof(CardPileCmd.Add),
    new[] { typeof(CardModel), typeof(PileType), typeof(CardPilePosition), typeof(AbstractModel), typeof(bool) })]
internal static class FreshCardAddCompensationPatch
{
    [HarmonyPostfix]
    private static void Postfix(
        PileType __1,
        CardPilePosition __2,
        AbstractModel? __3,
        ref Task<CardPileAddResult> __result)
    {
        if (__result is not null && MonsterTargetFold.CompensationActive)
        {
            __result = MonsterTargetFold.CompensateFreshCardAsync(__result, __1, __2, __3);
        }
    }
}

/// <summary>补偿之二（批量版本）：<c>Add(cards, PileType.X)</c>。</summary>
[HarmonyPatch(
    typeof(CardPileCmd),
    nameof(CardPileCmd.Add),
    new[]
    {
        typeof(IEnumerable<CardModel>), typeof(PileType), typeof(CardPilePosition),
        typeof(AbstractModel), typeof(bool),
    })]
internal static class FreshCardsAddCompensationPatch
{
    [HarmonyPostfix]
    private static void Postfix(
        PileType __1,
        CardPilePosition __2,
        AbstractModel? __3,
        ref Task<IReadOnlyList<CardPileAddResult>> __result)
    {
        if (__result is not null && MonsterTargetFold.CompensationActive)
        {
            __result = MonsterTargetFold.CompensateFreshCardsAsync(__result, __1, __2, __3);
        }
    }
}

/// <summary>
/// 视觉接口把目标还原成全部成员：折叠只该影响结算，不该让特效少画。
/// </summary>
/// <remarks>
/// 本体有 8 个怪招用 <c>VfxCmd.PlayOnCreatureCenters(targets, …)</c> 之类的批量特效
/// （圣兽、齿眼、地精佣兵、粘液……），折叠后只会画在锚点身上。
/// 这些调用不参与校验和，还原它们零风险。
/// </remarks>
[HarmonyPatch]
internal static class VisualTargetExpandPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(VfxCmd), nameof(VfxCmd.PlayOnCreatures));
        yield return AccessTools.Method(typeof(VfxCmd), nameof(VfxCmd.PlayOnCreatureCenters));
    }

    [HarmonyPrefix]
    private static void Prefix(ref IEnumerable<Creature> __0)
    {
        __0 = MonsterTargetFold.ExpandForVisuals(__0);
    }
}
