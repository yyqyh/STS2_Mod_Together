using HarmonyLib;

using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;

namespace Together.Core.Multiplayer;

/// <summary>
/// 归属改写的**正确时机**：牌彻底离开手牌、且搬运动画已经播完之后，再把共享堆里的牌统一归锚点。
/// </summary>
/// <remarks>
/// <para>
/// 为什么必须"晚"：本体 <c>CardPileCmd.Add</c> 内部的顺序是
/// <c>①从原堆摘除 → ②插入目标堆 → ③播搬运动画（内含移除手牌节点）→ ④派发 AfterCardChangedPiles</c>。
/// 我们原来在 ② 就改 owner（挂在 <c>CardPile.AddInternal</c> 上），而界面在 ③ 才做移除手牌节点的工作，
/// 且它处处依赖 <c>card.Owner</c> / <c>card.Pile</c>：
/// </para>
/// <list type="bullet">
/// <item><description><c>NPlayerHand</c> 里 <c>PileType.Hand.GetPile(card.Owner).Cards</c> —— 按下标摆放手牌。</description></item>
/// <item><description><c>CardPileCmd.MoveCardNodeToNewPileBeforeTween</c> 里 <c>hand.IsAncestorOf(cardNode)</c> 的判断。</description></item>
/// </list>
/// <para>
/// owner 与实际所在手牌对不上 → 界面漏掉这张牌 → 节点残留成"幽灵卡"（点它还能操作真实卡，
/// 因为它持有的 <c>CardModel</c> 是真的，只是 owner 已被改成锚点）。
/// </para>
/// <para>
/// 改到 <b>④ 之后</b>（本补丁的挂点）：动画已播完、节点已搬完，owner 再变就不影响界面；
/// 而共享堆在本轮结束时仍然均匀归锚点，洗牌那条"同批 owner 必须一致"的校验照样能过。
/// </para>
/// <para>
/// 判断条件是**目的堆类型**（Draw / Discard / Exhaust），与来源无关 ——
/// 因为"打出的牌"走的是 Hand → Play → Discard，若只筛"从手牌直接离开"就会漏掉它，
/// 那些牌会保持回声归属，洗牌时再次撞校验。
/// </para>
/// <para>
/// 但<b>光看堆类型不够</b>：3~4 人局里没选共享角色的玩家也有自己的 Draw / Discard / Exhaust，
/// 那些堆<b>不是</b>共享堆，别人的牌落进去不该被改写归属（改了会让 <c>card.Pile</c>
/// 按 owner 反查不到自己的堆 → 那个人的整个牌库也跟着乱）。所以还要确认
/// "这一堆就是锚点那一份"（回声的 getter 重定向后拿到的是同一实例，所以回声的牌也会被正确归一）。
/// </para>
/// <para>
/// 只有自检/诊断不需要它；这里是正常功能，不做开关。
/// </para>
/// </remarks>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardChangedPiles))]
internal static class SharedPileOwnerLateNormalizePatch
{
    /// <summary>位置参数：runState=0, combatState=1, <b>card=2</b>, oldPileType=3, clonedBy=4。</summary>
    [HarmonyPrefix]
    private static void Prefix(CardModel __2)
    {
        if (!TogetherPair.IsActive || TogetherPair.Anchor is not { } anchor)
        {
            return;
        }

        // 已经搬完、动画也播完了：此刻读它的当前堆是稳定的。
        var pile = __2.Pile;
        if (pile is null)
        {
            return;
        }

        if (pile.Type is not (PileType.Draw or PileType.Discard or PileType.Exhaust))
        {
            return;
        }

        // 只处理共享堆（= 锚点的那一份）。非配对玩家自己的堆原样不动。
        if (!IsSharedPile(pile))
        {
            return;
        }

        if (!ReferenceEquals(__2.Owner, anchor))
        {
            __2.GiveToAnotherPlayer(anchor);
        }
    }

    /// <summary>这一堆是不是共享堆（锚点的 Draw / Discard / Exhaust）。</summary>
    private static bool IsSharedPile(CardPile pile)
    {
        if (TogetherPair.Anchor?.PlayerCombatState is not { } anchorState)
        {
            return false;
        }

        return ReferenceEquals(pile, anchorState.DrawPile)
               || ReferenceEquals(pile, anchorState.DiscardPile)
               || ReferenceEquals(pile, anchorState.ExhaustPile);
    }
}
