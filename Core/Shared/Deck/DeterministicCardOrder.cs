using System.Text.Json;

using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using Together.Core.Common;
using Together.Core.Diagnostics;
using Together.Core.Foundation;
using Together.Core.Ui;

namespace Together.Core.Shared.Deck;
/// <summary>牌序相关的工具：排序键、候选池副本、顺序指纹。</summary>
/// <remarks>
/// <b>本 mod 不重排任何牌堆</b>：弃牌堆 / 主卡组 / 抽牌堆的顺序完全由本体、玩家或其他 mod 决定 ——
/// 想"控制牌堆顺序"的 mod 不会被我们覆盖。这里的东西都是非破坏性的：
/// <see cref="SortKey" /> 给 <c>DeterministicCardComparePatch</c> 用（本体的 <c>List.Sort</c> 排的是副本，不动牌堆）；
/// <see cref="SortedCopy" /> 给"候选池"这类一次性列表用；
/// <see cref="Fingerprint" /> 只用于日志 —— 破灭 / 受膏 / 初始洗牌这些"按堆序取牌"的点，两端指纹一比就知道顺序漂没漂。
/// </remarks>
internal static class DeterministicCardOrder
{
    /// <summary>按确定性键排序后的副本（用于"候选池"这类不该就地改的输入）。</summary>
    public static List<CardModel> SortedCopy(IEnumerable<CardModel>? cards)
    {
        return cards is null
            ? []
            : cards.OrderBy(SortKey, StringComparer.Ordinal).ToList();
    }

    /// <summary>一串牌"按当前顺序"的指纹（FNV-1a over <see cref="SortKey" />），形如 <c>3F2A…/31</c>。</summary>
    /// <remarks>
    /// 只用于<b>比对两端</b>：指纹相同 = 顺序与内容都一致。它<b>不改牌堆</b>，
    /// 所以可以作为"我们不再归一牌堆"之后的证据来源（见 <c>order.probe</c> 日志）。
    /// </remarks>
    public static string Fingerprint(IEnumerable<CardModel>? cards)
    {
        if (cards is null)
        {
            return "<null>";
        }

        var list = cards as IList<CardModel> ?? cards.ToList();
        var hash = 0xcbf29ce484222325UL;
        foreach (var card in list)
        {
            foreach (var ch in SortKey(card))
            {
                hash = (hash ^ ch) * 0x100000001b3UL;
            }
        }

        return $"{hash:X16}/{list.Count}";
    }

    /// <summary>把牌堆内容打成人能看懂的一行（诊断用：两端日志一比就知道是哪一步开始不一样的）。</summary>
    public static string DescribeCards(IEnumerable<CardModel>? cards, int max = 12)
    {
        if (cards is null)
        {
            return "<null>";
        }

        var list = cards as IList<CardModel> ?? cards.ToList();
        var parts = list.Take(max).Select(ShortLabel).ToList();

        if (list.Count > max)
        {
            parts.Add($"…+{list.Count - max}");
        }

        return $"[{string.Join(",", parts)}]";
    }

    /// <summary>排序键：与校验和比对的字段口径一致（<see cref="CardModel.ToSerializable" /> 的 JSON）。</summary>
    /// <remarks>
    /// <c>ToSerializable</c> 会 <c>AssertMutable</c>，所以兜一层；万一抛了就退化成
    /// "楼层 + 模型 + 升级"的组合键（同键的牌在两端互换同样不影响序列化结果）。
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

    /// <summary>短标签（日志用，不带序列化细节）。</summary>
    private static string ShortLabel(CardModel card)
    {
        var level = card.CurrentUpgradeLevel;
        return level > 0 ? $"{card.Id.Entry}+{level}" : card.Id.Entry;
    }
}
