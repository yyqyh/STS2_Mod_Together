using HarmonyLib;

using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

namespace Together.Core.Multiplayer;

/// <summary>
/// 进阶之灾去重：共享卡组下本体"逐玩家各加一张"的诅咒会变成两张。
/// </summary>
/// <remarks>
/// <para>
/// <c>AscensionManager.ApplyEffectsTo(player)</c> 是逐玩家调用的
/// （<c>RunManager.InitializeNewRun</c> 里 <c>foreach (player) ApplyAscensionEffects(player)</c>），
/// 里面那句 <c>player.Deck.AddInternal(AscendersBane, -1, silent: true)</c> 自然也就执行了两次。
/// 共享卡组下两个人的 <c>player.Deck</c> 指向同一份（<see cref="TogetherPair.Arm" /> 已经换掉了
/// 回声的 <c>Deck</c> 字段，getter 也做了重定向），于是同一张诅咒被加了两遍 ——
/// 表现就是开局卡组里有两张"进阶之灾"。
/// </para>
/// <para>
/// <b>为什么用 Postfix 去重，而不是 Prefix 直接跳过回声</b>：这个方法里还有<b>应当逐玩家生效</b>的部分
/// （<c>SubtractFromMaxPotionCount</c>，药水格是各算各的）。整段跳过会让回声的药水格比锚点多一个。
/// 所以让原方法照常跑，只把共享卡组里多出来的那张摘掉。
/// </para>
/// <para>
/// 摘牌要连 <c>RunState</c> 的卡牌登记一起清：只用 <c>RemoveInternal</c> 把牌从卡组里拿掉的话，
/// 这张牌还留在 <c>RunState</c> 的全卡表里，存档/校验和会看到一张"无主的牌"。
/// </para>
/// <para>
/// 判卡靠类型 + ID 双保险：类型名对不上（本体改过命名）时退回 ID 匹配，
/// 两样都对不上就什么都不做——绝不去删不认识的牌。
/// </para>
/// <para>
/// 除了挂在 <c>ApplyEffectsTo</c> 后面，<see cref="TogetherPair.Arm" /> 激活配对时也会对齐一次：
/// <b>读档/重连走的是 <c>FromSerializable</c>，那条路根本不会调 <c>ApplyEffectsTo</c></b>，
/// 只在后置补丁里去重的话，早先存下来的"两张进阶之灾"会被原样带回来。
/// </para>
/// </remarks>
[HarmonyPatch(typeof(AscensionManager), nameof(AscensionManager.ApplyEffectsTo))]
internal static class AscensionBaneDedupePatch
{
    private const string BanIdFragment = "ASCENDERS_BANE";

    [HarmonyPostfix]
    private static void Postfix(Player __0)
    {
        if (!TogetherPair.IsActive || TogetherPair.Anchor is not { } anchor)
        {
            return;
        }

        // 幂等：每次 ApplyEffectsTo 之后都对齐一次（新开局的两次、以及将来重连/加人再触发时都覆盖到）。
        DedupeSharedDeck(anchor);
    }

    /// <summary>把共享卡组里多出来的进阶之灾摘掉（幂等，可以随便多调）。</summary>
    internal static void DedupeSharedDeck(Player anchor)
    {
        var deck = anchor.Deck;
        var before = deck.Cards.Count;
        var extras = new List<CardModel>();
        var kept = false;

        foreach (var card in deck.Cards)
        {
            if (!IsAscendersBane(card))
            {
                continue;
            }

            if (!kept)
            {
                // 第一张留着：进阶之灾本来就是"卡组里有一张"。
                kept = true;
                continue;
            }

            extras.Add(card);
        }

        if (extras.Count == 0)
        {
            return;
        }

        foreach (var extra in extras)
        {
            deck.RemoveInternal(extra, silent: true);
            anchor.RunState.RemoveCard(extra);
        }

        Log.Info(
            $"[together] 进阶之灾去重：共享卡组本来 {before} 张，"
            + $"摘掉 {extras.Count} 张重复诅咒 → {deck.Cards.Count} 张");
    }

    private static bool IsAscendersBane(CardModel card)
    {
        if (card is AscendersBane)
        {
            return true;
        }

        try
        {
            return card.Id.Entry.Contains(BanIdFragment, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
