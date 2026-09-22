using System.Reflection;

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

/// <summary>
/// "取牌"的确定性：<b>挂本体公共入口，不认具体是哪张牌</b>。
/// </summary>
/// <remarks>
/// <list type="number">
/// <item><description><b>弃牌堆 / 主卡组</b>：挂 <c>CardPile.AddInternal</c>（含 MoveToTop/Bottom），每次变动后保持规范序。
/// 这两口堆的顺序在玩法上没有语义（全库没有"取弃牌堆顶 / 卡组第 N 张"的读法），所以可以一直保持规范序。</description></item>
/// <item><description><b>打出抽牌堆</b>：挂 <c>CardPileCmd.AutoPlayFromDrawPile</c>（破灭 / 倾泻 / 无敌 / 混沌蒸馏药水…）。</description></item>
/// <item><description><b>StableShuffle 取牌</b>：靠 <c>DeterministicCardComparePatch</c> 把 <c>CardModel.CompareTo</c> 变成全序
/// （横祸 / 乱战 / 寻者之击 / 能量电池…），不需要单独挂。</description></item>
/// <item><description><b>候选池</b>：挂 <c>CardFactory.GetDistinctForCombat</c>（攻击药水 / 发现一类）。</description></item>
/// </list>
/// <para>
/// <b>唯一例外</b>：<c>TakeRandom</c>（内部 UnstableShuffle）直接读<b>抽牌堆</b>的写法。抽牌堆不能保持规范序
/// （本体抽牌就是取 <c>Cards[0]</c>，排了就变成永远按固定顺序抽牌）。全库目前只有受膏一张，见文件末尾；
/// 以后新增这种写法（<c>TakeRandom</c> + 抽牌堆）grep 一下 <c>TakeRandom(</c> 再加一条。
/// </para>
/// </remarks>
internal static class RandomPickOrder
{
    /// <summary>
    /// 顺序<b>没有语义</b>的牌堆：可以一直保持规范序。
    /// </summary>
    /// <remarks>
    /// 只放弃牌堆与主卡组。抽牌堆（抽牌取堆顶）、手牌（左右位置有意义）、
    /// 消耗堆 / 打出堆（没人从里面随机取）都不碰。
    /// </remarks>
    private static bool IsOrderFreePile(PileType type)
    {
        return type is PileType.Discard or PileType.Deck;
    }

    /// <summary>牌堆变动后归一顺序（弃牌堆 / 主卡组）。</summary>
    public static void NormalizeOrderFreePile(CardPile? pile, Player? player, string why)
    {
        if (pile is null || player is null)
        {
            return;
        }

        if (!TogetherPair.IsActive || !TogetherPair.IsMember(player))
        {
            return;
        }

        if (!IsOrderFreePile(pile.Type))
        {
            return;
        }

        DeterministicCardOrder.SortPile(pile, why);
    }

    /// <summary>取牌之前把某口堆排成两端一致（只有明确知道"这口堆此刻只被随机读取"时才用）。</summary>
    public static void SortBeforePick(Player? player, PileType type, string why)
    {
        if (player is null || !TogetherPair.IsActive || !TogetherPair.IsMember(player))
        {
            return;
        }

        DeterministicCardOrder.SortPile(type.GetPile(player), why);
    }
}

// ======================================================================================
// 1) 弃牌堆 / 主卡组：只要堆变了就归一顺序（覆盖所有"从这两口堆随机取"的效果）
// ======================================================================================

/// <summary>堆变动（加牌 / 移到堆顶 / 移到堆底）之后归一顺序。</summary>
[HarmonyPatch]
internal static class PileOrderNormalizePatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(CardPile), nameof(CardPile.AddInternal));
        yield return AccessTools.Method(typeof(CardPile), nameof(CardPile.MoveToTopInternal));
        yield return AccessTools.Method(typeof(CardPile), nameof(CardPile.MoveToBottomInternal));
    }

    [HarmonyPostfix]
    private static void Postfix(CardPile __instance, CardModel card, MethodBase __originalMethod)
    {
        RandomPickOrder.NormalizeOrderFreePile(__instance, card.Owner, __originalMethod.Name);
    }
}

// ======================================================================================
// 2) 打出抽牌堆：破灭 / 倾泻 / 无敌 / 混沌蒸馏药水 / 混乱之力…
// ======================================================================================

/// <summary>
/// 从抽牌堆自动打牌（<c>Top</c> / <c>Bottom</c> / <c>Random</c>）。
/// </summary>
/// <remarks>
/// <c>Top</c> 分支完全不消耗随机数，只按堆顺序取（<c>Cards.FirstOrDefault()</c>），
/// 所以必须先把堆排齐，否则两端各打各的牌。
/// </remarks>
[HarmonyPatch(typeof(CardPileCmd), nameof(CardPileCmd.AutoPlayFromDrawPile))]
internal static class AutoPlayFromDrawPileOrderPatch
{
    [HarmonyPrefix]
    private static void Prefix(Player player, int count, CardPilePosition position)
    {
        RandomPickOrder.SortBeforePick(player, PileType.Draw, $"自动打牌/{position}×{count}");
    }
}

// ======================================================================================
// 3) 候选池：攻击药水 / 发现这类"生成随机候选"，TakeRandom 内部是 UnstableShuffle
// ======================================================================================

/// <summary>
/// 战斗中的"生成随机候选牌"（攻击药水、发现一类）。
/// </summary>
/// <remarks>
/// 这里排的是<b>候选池的副本</b>，不动任何牌堆：输入顺序一致，<c>UnstableShuffle</c> 的结果就一致。
/// </remarks>
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
// 4) 诊断：每次"自动打出某张牌"留一行，便于两份 log 对第一个分歧点
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
// 5) 例外：TakeRandom（= UnstableShuffle）直接读抽牌堆的牌
// ======================================================================================

/// <summary>
/// 受膏（<c>ANOINTED</c>）：<c>TakeRandom</c> 从<b>抽牌堆</b>抽稀有牌进手牌。
/// </summary>
/// <remarks>
/// 抽牌堆不能一直保持规范序（抽牌就是取 <c>Cards[0]</c>），而 <c>TakeRandom</c> 是泛型扩展方法
/// ——.NET 对引用类型实参只生成一份代码，挂上去会连累遗物抓包 / 地图生成（实测直接崩），
/// 所以这一类只能挂在具体牌上。全库目前只有这一张。
/// </remarks>
[HarmonyPatch(typeof(Anointed), "OnPlay")]
internal static class AnointedOrderPatch
{
    [HarmonyPrefix]
    private static void Prefix(Anointed __instance)
    {
        RandomPickOrder.SortBeforePick(__instance.Owner, PileType.Draw, "受膏");
    }
}
