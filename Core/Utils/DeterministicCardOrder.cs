using System.Text.Json;

using HarmonyLib;

using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace Together.Core.Utils;

/// <summary>
/// 把牌列表 / 牌堆排成<b>两端一致</b>的顺序，让"随机取牌"两端取到同一张。
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
/// 这里按"序列化等价键"排序：键相同的两张牌在两端互换<b>不影响校验和</b>
/// （校验和比对的正是这套序列化字段），所以排序后两端的输入必然一致，取牌结果也就一致了。
/// </para>
/// <para>
/// <b>三条口径</b>：
/// </para>
/// <list type="number">
/// <item><description><see cref="Sort" />：洗牌前排序（本体随后会自己再洗一次）。</description></item>
/// <item><description><see cref="SortPile" />：<b>随机取牌前</b>排序，直接改牌堆本身。两端都改 → 不只是这次取到同一张，
/// 连"之后从堆顶抽牌"的顺序也一起收敛。破灭（<c>HAVOC</c>）/ 横祸（<c>CATASTROPHE</c>）/ 乱战（<c>UPROAR</c>）
/// 这类"打出随机牌"的效果都走这里（接点见 <c>Core/Patches/Together/Deck/RandomPickPatches.cs</c>）。</description></item>
/// <item><description><see cref="SortedCopy" />：给"候选池"排序（<c>TakeRandom</c> 内部是 <c>UnstableShuffle</c>，最怕输入顺序不同）。</description></item>
/// </list>
/// </remarks>
internal static class DeterministicCardOrder
{
    /// <summary><c>CardPile.Cards</c> 是只读包装，真正可重排的是这个列表。</summary>
    internal static readonly AccessTools.FieldRef<CardPile, List<CardModel>> CardsField =
        AccessTools.FieldRefAccess<CardPile, List<CardModel>>("_cards");

    /// <summary>把列表排成两端一致的顺序；返回是否真的动过（已经是这个顺序时不动它）。</summary>
    public static bool Sort(List<CardModel>? cards)
    {
        if (cards is null || cards.Count < 2)
        {
            return false;
        }

        var sorted = SortedCopy(cards);

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
            return false;
        }

        cards.Clear();
        cards.AddRange(sorted);
        return true;
    }

    /// <summary>按确定性键排序后的副本（用于"候选池"这类不该就地改的输入）。</summary>
    /// <summary>
    /// 归一之后再<b>确定性重排</b>：两端顺序仍然一模一样，但看起来是打乱的（同名卡不再总挨着）。
    /// </summary>
    /// <remarks>
    /// 只用排序会让同名卡扎堆（卡组/弃牌堆一眼看去像被整理过）。这里在规范序的基础上用
    /// <b>自己的 PRNG</b> 做 Fisher–Yates：种子 = 规范序内容（牌键）的 FNV-1a 哈希 + 牌堆类型，
    /// 两端算出来必然相同 → 置换也相同；内容一变种子就变，顺序看起来就是不固定的随机序。
    /// <b>绝不能用本体的 <c>Rng</c></b>：那会推进它的随机流、直接两端分叉。
    /// </remarks>
    public static void SortAndMix(CardPile? pile, string why)
    {
        if (pile is null)
        {
            return;
        }

        SortPile(pile, why);

        var cards = CardsField(pile);
        if (cards is null || cards.Count < 2)
        {
            return;
        }

        var seed = 0xcbf29ce484222325UL ^ (ulong)(int)pile.Type;
        foreach (var card in cards)
        {
            foreach (var ch in SortKey(card))
            {
                seed = (seed ^ ch) * 0x100000001b3UL;
            }
        }

        var state = seed;
        for (var i = cards.Count - 1; i > 0; i--)
        {
            state = NextRandom(state);
            var j = (int)(state % (ulong)(i + 1));
            (cards[i], cards[j]) = (cards[j], cards[i]);
        }
    }

    /// <summary>splitmix64：自带的小 PRNG（不碰本体随机流）。</summary>
    private static ulong NextRandom(ulong state)
    {
        state += 0x9E3779B97F4A7C15UL;
        var z = state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public static List<CardModel> SortedCopy(IEnumerable<CardModel>? cards)
    {
        return cards is null
            ? []
            : cards.OrderBy(SortKey, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// <b>随机取牌之前</b>把这个牌堆本身排成两端一致（就地改）。
    /// </summary>
    /// <remarks>
    /// 共生体两端"谁的牌堆顺序"其实已经因为各种本地时序漂了；这里在两端各排一次，
    /// 等于给共享牌堆做一次确定性归一：既保证这次随机取到同一张，也让后续抽牌重新对上。
    /// </remarks>
    public static void SortPile(CardPile? pile, string why)
    {
        if (pile is null)
        {
            return;
        }

        List<CardModel>? cards;
        try
        {
            cards = CardsField(pile);
        }
        catch (Exception)
        {
            return;
        }

        if (cards is null || cards.Count < 2)
        {
            return;
        }

        // 先在副本上排序 + 比对：没变就直接返回（热路径上别白建字符串）
        var sorted = SortedCopy(cards);
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

        var before = DescribeCards(cards);
        cards.Clear();
        cards.AddRange(sorted);

        CappedLog.Info("pile.order", $"牌堆顺序归一（{why}）：{before} → {DescribeCards(cards)}");
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
