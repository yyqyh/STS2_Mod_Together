using HarmonyLib;

using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace Together.Core.Multiplayer;

/// <summary>
/// 手牌归属不变量：<b>在谁手里就归谁</b>。
/// </summary>
/// <remarks>
/// <para>
/// 为什么要在数据层强制这件事：本体不少"手牌类"效果是拿<b>卡牌自己的 owner</b> 去推
/// "这是谁的手牌 / 该谁抽牌"的。例如 <c>CalculatedGamble</c>（计算下注）里
/// <c>PileType.Hand.GetPile(base.Owner).Cards</c> 取的是 owner 的手牌，
/// 而 <c>CardCmd.DiscardAndDraw</c> 干脆用 <c>discardCards[0].Owner</c> 决定"谁抽牌"。
/// </para>
/// <para>
/// 一旦手牌里混进 owner 不是手牌主人的牌（读档恢复、效果搬运等路径都可能这样），
/// 就会出现"p2 打计算下注，却把 p1 的手牌弃掉、并让 p1 抽牌"这种错位 ——
/// 表现就是"p2 的计算下注不能正确抽牌"。
/// </para>
/// <para>
/// 修法是在牌<b>进手牌</b>的那一刻（<c>CardPile.AddInternal</c>）就把 owner 对齐到该手牌的主人。
/// 这里是原地改 owner、<b>不</b>像 <see cref="CardOwnershipImpl.NormalizeForHand" /> 那样先摘牌：
/// 牌已经躺在这口手牌里了，新主人的堆集合里就有这口堆，<c>card.Pile</c> 依然找得到它，
/// 不会出现"牌同时挂在两处"。离手时的归属仍由 <see cref="SharedPileOwnerLateNormalizePatch" /> 处理。
/// </para>
/// </remarks>
[HarmonyPatch(typeof(CardPile), nameof(CardPile.AddInternal))]
internal static class HandOwnershipInvariantPatch
{
    [HarmonyPostfix]
    private static void Postfix(CardPile __instance, CardModel __0)
    {
        if (!TogetherPair.IsActive || __instance.Type != PileType.Hand)
        {
            return;
        }

        if (TogetherPair.Anchor?.RunState is not { } runState
            || CardOwnershipImpl.HandOwnerOf(runState, __instance) is not { } handOwner)
        {
            return;
        }

        // 记下"这张牌最后躺在谁的手牌里"：牌离手后会被归一成锚点归属，而遗物/能力
        // 认领"我的牌"时看的正是 card.Owner（例如金纸 JossPaper）。见 CardLastHandOwner。
        CardLastHandOwner.Remember(__0, handOwner);

        if (ReferenceEquals(__0.Owner, handOwner))
        {
            return;
        }

        CappedLog.Info(
            "hand.owner_fixed",
            $"进手牌归属对齐：card={__0.Id.Entry} → netId={handOwner.NetId}");

        __0.GiveToAnotherPlayer(handOwner);
    }
}
