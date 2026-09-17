using System.Text.Json;

using MegaCrit.Sts2.Core.Models;

namespace Together.Core.Multiplayer;

/// <summary>
/// 洗牌前把牌列表排成<b>两端一致</b>的顺序。
/// </summary>
/// <remarks>
/// <para>
/// 本体的两种洗牌都<b>依赖输入顺序</b>：
/// </para>
/// <list type="bullet">
/// <item><description><c>UnstableShuffle</c>（Fisher-Yates）：输入顺序不同 → 结果不同。</description></item>
/// <item><description><c>StableShuffle</c>：虽然先 <c>list.Sort()</c> 抹平顺序，但 <c>List.Sort</c> 是<b>不稳定排序</b>，
/// 而 <c>CardModel</c> 之间大量存在"比较相等"的牌（同名牌、同升级），它们的先后仍取决于输入顺序 → 结果仍可能不同。</description></item>
/// </list>
/// <para>
/// 而共生体下两侧的输入顺序天然会差：牌堆顺序受"两端各自加牌/弃牌的先后"影响，
/// <c>CardPileCmd.Shuffle</c> 里的 <c>list.AddRange(drawPileCards)</c> 更是直接枚举
/// <c>HashSet&lt;CardModel&gt;</c>（引用哈希 → 两端顺序必然不同）。
/// </para>
/// <para>
/// 实测：打出破灭（<c>HAVOC</c>）之后，主机侧对两只啃咬机各多打了 5 点伤害、客户端没有——
/// 因为"从抽牌堆顶/随机取牌"这类效果两端取到了不同的牌，校验和当场炸掉并把客户端踢下线。
/// </para>
/// <para>
/// 这里按"序列化等价键"排序：键相同的两张牌在两端互换<b>不影响校验和</b>
/// （校验和比对的正是这套序列化字段），所以排序后两端的洗牌输入必然一致，洗牌结果也就一致了。
/// </para>
/// </remarks>
internal static class DeterministicCardOrder
{
    /// <summary>把列表排成两端一致的顺序（已经是这个顺序时不动它）。</summary>
    public static void Sort(List<CardModel>? cards)
    {
        if (cards is null || cards.Count < 2)
        {
            return;
        }

        var sorted = cards.OrderBy(SortKey, StringComparer.Ordinal).ToList();

        var changed = false;
        for (var i = 0; i < cards.Count; i++)
        {
            if (!ReferenceEquals(cards[i], sorted[i]))
            {
                changed = true;
                break;
            }
        }

        if (!changed)
        {
            return;
        }

        cards.Clear();
        cards.AddRange(sorted);

        CappedLog.Info("shuffle.order", $"洗牌前先按确定性顺序排列 {cards.Count} 张牌（让两端取到同一张）");
    }

    /// <summary>
    /// 排序键：与校验和比对的字段口径一致（<see cref="CardModel.ToSerializable" /> 的 JSON）。
    /// </summary>
    /// <remarks>
    /// <c>ToSerializable</c> 会 <c>AssertMutable</c>，所以兜一层；万一抛了就退化成
    /// "模型 + 升级 + 楼层"的组合键（同键的牌在两端互换同样不影响序列化结果）。
    /// </remarks>
    internal static string SortKey(CardModel card)
    {
        try
        {
            return JsonSerializer.Serialize(card.ToSerializable());
        }
        catch (Exception)
        {
            return $"{card.FloorAddedToDeck ?? 0:D4}|{card.Id.Entry}|{card.CurrentUpgradeLevel:D2}";
        }
    }
}
