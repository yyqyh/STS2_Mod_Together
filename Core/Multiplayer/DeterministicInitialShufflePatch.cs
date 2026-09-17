using HarmonyLib;

using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace Together.Core.Multiplayer;

/// <summary>
/// 初始洗牌（<c>CardPile.RandomizeOrderInternal</c>）前先把牌堆排成两端一致的顺序。
/// </summary>
/// <remarks>
/// <para>
/// 战斗开始时会把主卡组复制进抽牌堆再 <c>UnstableShuffle</c> 打乱，而 <c>UnstableShuffle</c> 是
/// Fisher-Yates、<b>结果依赖输入顺序</b>。共生体下两端往主卡组里加牌的先后可能不同
/// （卡牌奖励是两端各自本地执行再互相同步的），于是洗出的顺序不同，校验和当场对不上。
/// </para>
/// <para>
/// 这里只挂具体的非泛型方法。**不要**去挂 <c>ListExtensions.UnstableShuffle&lt;T&gt;</c> 这类泛型洗牌方法：
/// .NET 对"引用类型实参的泛型方法"只生成一份代码，Harmony 打上去之后
/// <c>UnstableShuffle&lt;RelicModel&gt;</c>（遗物抓包）和 <c>StableShuffle&lt;MapPointType&gt;</c>（地图生成）
/// 都会跑进我们这份补丁里，实测直接把开局打成 <c>EntryPointNotFoundException</c>。
/// 战斗中洗牌改从比较器那一侧解决，见 <see cref="DeterministicCardComparePatch" />。
/// </para>
/// </remarks>
[HarmonyPatch(typeof(CardPile), nameof(CardPile.RandomizeOrderInternal))]
internal static class DeterministicInitialShufflePatch
{
    /// <summary><c>CardPile.Cards</c> 是只读包装，真正可重排的是这个列表。</summary>
    private static readonly AccessTools.FieldRef<CardPile, List<CardModel>> CardsField =
        AccessTools.FieldRefAccess<CardPile, List<CardModel>>("_cards");

    [HarmonyPrefix]
    private static void Prefix(CardPile __instance, Player player)
    {
        if (!TogetherPair.IsActive || !TogetherPair.IsMember(player))
        {
            return;
        }

        DeterministicCardOrder.Sort(CardsField(__instance));
    }
}
