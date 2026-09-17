using HarmonyLib;

using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace Together.Core.Multiplayer;

/// <summary>
/// M1：卡牌归属（owner）的维护规则。
/// </summary>
/// <remarks>
/// <para>
/// 规则一句话：<b>在手牌里属于手牌主人，离开手牌后归还锚点。</b>
/// </para>
/// <para>
/// 为什么必须成对：
/// </para>
/// <list type="number">
/// <item><description>
/// 进手牌要改成手牌主人，否则 <c>CardModel.Pile</c>（只在该卡 owner 的堆里找自己）会算出 null，
/// 而且打出去时扣的是另一个人的能量。
/// </description></item>
/// <item><description>
/// 离开手牌要还回锚点，否则共享的弃牌堆／抽牌堆里会出现**混合归属**的牌，
/// 而本体的批量 <c>CardPileCmd.Add</c> 有一条硬校验："同一次调用里所有牌 owner 必须一致"。
/// 洗牌正好走的是批量 Add（把弃牌堆混进抽牌堆），于是抛
/// <c>Tried to add cards with different owners to the same pile!</c>，
/// 直接把回合循环打死（实测"战斗中无法正确结束回合"就是这个）。
/// </description></item>
/// </list>
/// <para>
/// 翻牌用的 API 是本体自己留的那条路（"有主不可改"的唯一例外）：
/// <c>CardModel.GiveToAnotherPlayer</c>，也就是本体 <c>CardPileCmd.GiveToAnotherPlayer</c> 用的同一套。
/// </para>
/// </remarks>
internal static class CardOwnershipImpl
{
    /// <summary>
    /// 进手牌：把牌改成手牌主人。
    /// </summary>
    /// <remarks>
    /// 必须<b>先</b>把牌从原堆摘出来、<b>再</b>改 owner——顺序照抄本体
    /// <c>CardPileCmd.GiveToAnotherPlayer</c>。
    /// 反过来做的话，<c>Add</c> 内部要靠 <c>card.Pile</c> 摘除时 owner 已经变了、
    /// 牌在新 owner 的堆里找不到自己，摘除会静默失败 → 牌同时留在原堆和新堆里。
    /// </remarks>
    internal static void NormalizeForHand(CardModel card, CardPile hand)
    {
        if (!TogetherPair.IsActive || hand.Type != PileType.Hand)
        {
            return;
        }

        var runState = TogetherPair.Anchor?.RunState;
        if (runState is null)
        {
            return;
        }

        var handOwner = HandOwnerOf(runState, hand);
        if (handOwner is null || ReferenceEquals(card.Owner, handOwner))
        {
            return;
        }

        card.RemoveFromCurrentPile(true);
        card.GiveToAnotherPlayer(handOwner);
    }

    /// <summary>
    /// <b>只在"混合归属"的批量操作里</b>把 owner 统一到锚点。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 为什么必须"只在混合时"才动：本体的动画/节点流程里有一句
    /// <c>LocalContext.IsMe(card.Owner)</c> 判定（见 <c>CardPileCmd.GetTweenForCardsChangingPiles</c>），
    /// 不是"本地玩家的牌"就会 <c>continue</c>、**完全跳过节点处理**。
    /// 早期版本无条件把离开手牌的牌归到锚点，于是 p2 的牌在判定之前 owner 就变了
    /// → 手牌节点没人清 → 幽灵卡（p1 是锚点所以看不出来，正好对应"p1 正常、p2 有幽灵"）。
    /// </para>
    /// <para>
    /// 而同属一个玩家的批量（典型就是回合结束清手牌）本来就能通过本体的
    /// "同批 owner 必须一致"校验，**根本不需要归一化**。
    /// 真正需要的只有洗牌那类把不同玩家的牌混进共享堆的操作 —— 那类批量里没有手牌节点。
    /// </para>
    /// </remarks>
    internal static void NormalizeBatchIfMixed(IReadOnlyList<CardModel> cards)
    {
        // ⚠️ 已停用（2026-09-15）：调用点已经注释掉（见文件末尾 CardOwnerBatchPatch）。
        // 原因：这条"批量入堆前统一归属"和"入堆后统一归属"都属于**时机过早**的改写，
        // 会让 owner 与实际所在手牌脱钩 → 界面漏掉移除手牌节点 → 幽灵卡。
        // 现在归属改写统一交给 SharedPileOwnerLateNormalizePatch（挂在 AfterCardChangedPiles，
        // 即"搬完 + 动画播完"之后）。要恢复本方法，把调用点那行取消注释即可 ——
        // 但请先确认幽灵卡不会因此回归。
        if (!TogetherPair.IsActive || TogetherPair.Anchor is not { } anchor || cards.Count == 0)
        {
            return;
        }

        Player? first = null;
        var mixed = false;

        foreach (var card in cards)
        {
            var owner = OwnerOf(card);
            if (owner is null)
            {
                return;
            }

            if (first is null)
            {
                first = owner;
            }
            else if (!ReferenceEquals(first, owner))
            {
                mixed = true;
                break;
            }
        }

        if (!mixed)
        {
            return;
        }

        foreach (var card in cards)
        {
            if (!ReferenceEquals(OwnerOf(card), anchor))
            {
                card.GiveToAnotherPlayer(anchor);
            }
        }
    }

    private static Player? OwnerOf(CardModel card)
    {
        try
        {
            return card.Owner;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>手牌堆没有被共享，所以"这个 Hand 属于谁"是唯一的。</summary>
    internal static Player? HandOwnerOf(IRunState runState, CardPile pile)
    {
        foreach (var player in runState.Players)
        {
            if (player.PlayerCombatState?.Hand is { } hand && ReferenceEquals(hand, pile))
            {
                return player;
            }
        }

        return null;
    }

}

/// <summary>单张牌进堆的入口（抽牌走这里）。</summary>
[HarmonyPatch(
    typeof(CardPileCmd),
    nameof(CardPileCmd.Add),
    new[] { typeof(CardModel), typeof(CardPile), typeof(CardPilePosition), typeof(AbstractModel), typeof(bool) })]
internal static class CardOwnerSinglePatch
{
    [HarmonyPrefix]
    private static void Prefix(CardModel __0, CardPile __1)
    {
        try
        {
            CardOwnershipImpl.NormalizeForHand(__0, __1);
        }
        catch (Exception ex)
        {
            Log.Warn($"[together] owner 交接检查失败：{ex.Message}");
        }
    }
}

/// <summary>
/// 批量进堆的入口：只在"混合归属"时把 owner 统一到锚点（典型场景是洗牌）。
/// </summary>
[HarmonyPatch(
    typeof(CardPileCmd),
    nameof(CardPileCmd.Add),
    new[]
    {
        typeof(IEnumerable<CardModel>), typeof(CardPile), typeof(CardPilePosition),
        typeof(AbstractModel), typeof(bool), typeof(bool),
    })]
internal static class CardOwnerBatchPatch
{
    [HarmonyPrefix]
    private static void Prefix(IEnumerable<CardModel> __0)
    {
        // 只在参数本身是实体集合时预先枚举（惰性序列不能安全预枚举，原方法还要再枚举一次）。
        if (__0 is not IReadOnlyList<CardModel> cards)
        {
            return;
        }

        try
        {
            // 已停用（时机过早，会造成幽灵卡）：归属改写改在 SharedPileOwnerLateNormalizePatch。
            // CardOwnershipImpl.NormalizeBatchIfMixed(cards);
        }
        catch (Exception ex)
        {
            Log.Warn($"[together] 批量归属规整失败：{ex.Message}");
        }
    }
}
