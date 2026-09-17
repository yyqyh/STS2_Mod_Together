using HarmonyLib;

using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace Together.Core.Multiplayer;

/// <summary>
/// 让 <c>CardModel.Pile</c> 在配对局里也能找到"借住"在另一半堆里的自己。
/// </summary>
/// <remarks>
/// <para>
/// 本体的实现是：<c>Pile =&gt; _owner?.Piles.FirstOrDefault(p =&gt; p.Cards.Contains(this))</c> ——
/// 只在<b>卡牌自己 owner</b> 的堆集合里找自己。
/// </para>
/// <para>
/// 共享牌库打破了这条隐含约定：为了通过本体批量 <c>CardPileCmd.Add</c> 的
/// "同批 owner 必须一致"校验（洗牌走这条路，否则回合循环会死），
/// 我们把离开手牌的牌统一归到锚点名下。于是 <c>Owner</c> 不再能唯一决定"牌在哪个堆里"，
/// 而依赖 owner 反查堆的代码（<c>RemoveFromCurrentPile</c>、<c>NPlayerHand.GetHandInsertIndex</c> 等）
/// 就会认错堆 —— 表现就是"幽灵卡牌 / 牌同时留在两处 / 计数不刷新"。
/// </para>
/// <para>
/// 这里只做一件事：原查找失败时，再去另一半的堆里找一遍。
/// 找到了就说明这张牌"借住"在对方的共享堆里（共享堆本来两边都指向同一实例）。
/// </para>
/// </remarks>
[HarmonyPatch(typeof(CardModel), "get_Pile")]
internal static class CardPileLookupPatch
{
    [HarmonyPostfix]
    private static void Postfix(CardModel __instance, ref CardPile? __result)
    {
        if (__result is not null || !TogetherPair.IsActive)
        {
            return;
        }

        foreach (var other in TogetherPair.OthersOf(OwnerOf(__instance)))
        {
            foreach (var pile in other.Piles)
            {
                if (pile.Cards.Contains(__instance))
                {
                    __result = pile;
                    return;
                }
            }
        }
    }

    /// <summary><c>Owner</c> 的 getter 会 AssertMutable，对 canonical 模型会抛，所以兜一层。</summary>
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
}
