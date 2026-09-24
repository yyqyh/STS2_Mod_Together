using HarmonyLib;

using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

using Together.Core.Combat;
using Together.Core.Utils;

namespace Together.Core.Patches.Deck;

/// <summary>"取牌"的确定性：<b>挂本体公共入口，不认具体是哪张牌</b>。</summary>
/// <remarks>
/// <b>本 mod 不重排任何牌堆</b>（弃牌堆 / 主卡组 / 抽牌堆都会原样保留，方便"控制牌堆顺序"的 mod 共存）。
/// 只做两件不碰牌堆的事：<b>候选池</b>排序（挂 <c>CardFactory.GetDistinctForCombat</c>，排的是一次性副本），
/// 以及 <c>DeterministicCardComparePatch</c>（本体的 <c>List.Sort</c> 排的也是副本）。
/// 原来在"按堆序取牌"前的就地排序（破灭 / 倾泻 / 无敌 / 混沌蒸馏药水、受膏）已撤，
/// 换成 <see cref="PileOrderProbe" /> 的只读指纹日志 —— 顺序漂了能看见，但不再被我们改写。
/// </remarks>
internal static class RandomPickOrder
{
    /// <summary>只读探针：把某口堆"当前顺序"的指纹打进日志（<b>不改牌堆</b>）。</summary>
    public static void Probe(Player? player, PileType type, string why)
    {
        if (player is null || !TogetherPair.IsActive || !TogetherPair.IsMember(player))
        {
            return;
        }

        CardPile? pile;
        try
        {
            pile = type.GetPile(player);
        }
        catch (Exception ex)
        {
            CappedLog.Info("order.probe", $"{type} 顺序指纹取不到（{why}）：{ex.GetType().Name}: {ex.Message}");
            return;
        }

        CappedLog.Info(
            "order.probe",
            $"{type} 顺序指纹（{why}）：{DeterministicCardOrder.Fingerprint(pile?.Cards)}（两端应一致；不一致=顺序已漂）");
    }
}

// ======================================================================================
// 1) 打出抽牌堆：破灭 / 倾泻 / 无敌 / 混沌蒸馏药水 / 混乱之力…（只留指纹，不改顺序）
// ======================================================================================

/// <summary>从抽牌堆自动打牌前，留一条抽牌堆顺序指纹。</summary>
/// <remarks>
/// <c>Top</c> 分支完全不消耗随机数、只按堆顺序取（<c>Cards.FirstOrDefault()</c>）—— 这是"顺序决定行为"的典型点，
/// 所以两端顺序必须一致。我们<b>不再替它排齐</b>（那会覆盖控制牌堆的 mod），改成在这里留指纹：
/// 若这一段行为两端不同，两份 log 一比就知道是不是顺序先漂的。
/// </remarks>
[HarmonyPatch(typeof(CardPileCmd), nameof(CardPileCmd.AutoPlayFromDrawPile))]
internal static class AutoPlayFromDrawPileProbePatch
{
    [HarmonyPrefix]
    private static void Prefix(Player player, int count, CardPilePosition position)
    {
        RandomPickOrder.Probe(player, PileType.Draw, $"自动打牌/{position}×{count}");
    }
}

// ======================================================================================
// 2) 候选池：攻击药水 / 发现这类"生成随机候选"，TakeRandom 内部是 UnstableShuffle
// ======================================================================================

/// <summary>战斗中的"生成随机候选牌"（攻击药水、发现一类）。</summary>
/// <remarks>这里排的是<b>候选池的副本</b>，不动任何牌堆：输入顺序一致，<c>UnstableShuffle</c> 的结果就一致。</remarks>
[HarmonyPatch(typeof(CardFactory), nameof(CardFactory.GetDistinctForCombat))]
internal static class CardFactoryDistinctOrderPatch
{
    [HarmonyPrefix]
    private static void Prefix(ref IEnumerable<CardModel> cards)
    {
        if (!TogetherPair.IsActive)
        {
            return;
        }

        cards = DeterministicCardOrder.SortedCopy(cards);
    }
}

// ======================================================================================
// 3) 诊断：每次"自动打出某张牌"留一行，便于两份 log 对第一个分歧点
// ======================================================================================

/// <summary>每一次"自动打出某张牌"都留一行日志。</summary>
[HarmonyPatch(typeof(CardCmd), nameof(CardCmd.AutoPlay))]
internal static class AutoPlayDiagnosticPatch
{
    [HarmonyPrefix]
    private static void Prefix(CardModel card, Creature? target)
    {
        if (!TogetherPair.IsActive)
        {
            return;
        }

        var drawPile = card.Owner is { } owner ? PileType.Draw.GetPile(owner) : null;

        CappedLog.Info(
            "autoplay",
            $"自动打出：{DeterministicCardOrder.DescribeCards([card], 1)}"
            + $" owner=netId{card.Owner?.NetId}"
            + $" target={(target is null ? "<null>" : target.GetType().Name)}"
            + $" 抽牌堆={DeterministicCardOrder.DescribeCards(drawPile?.Cards)}");
    }
}

// ======================================================================================
// 4) TakeRandom（= UnstableShuffle）直接读抽牌堆的牌：受膏
// ======================================================================================

/// <summary>受膏（<c>ANOINTED</c>）：<c>TakeRandom</c> 从<b>抽牌堆</b>抽稀有牌进手牌（打指纹，不排堆）。</summary>
/// <remarks>
/// <c>TakeRandom</c> 是泛型扩展方法 —— .NET 对引用类型实参只生成一份代码，挂上去会连累遗物抓包 / 地图生成
/// （实测直接崩），所以这类只能挂在具体牌上。全库目前只有这一张，以后新增就 grep <c>TakeRandom(</c> 再加一条。
/// </remarks>
[HarmonyPatch(typeof(Anointed), "OnPlay")]
internal static class AnointedOrderProbePatch
{
    [HarmonyPrefix]
    private static void Prefix(Anointed __instance)
    {
        RandomPickOrder.Probe(__instance.Owner, PileType.Draw, "受膏");
    }
}
