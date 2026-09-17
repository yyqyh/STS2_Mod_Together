using HarmonyLib;

using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

namespace Together.Core.Multiplayer;

/// <summary>
/// "弃掉整手牌、再抽同样数量"（计算下注 / 赌徒之酿 / 赌徒筹码）的抽牌对象修正。
/// </summary>
/// <remarks>
/// <para>
/// 本体 <c>CardCmd.DiscardAndDraw</c> 的顺序是：<b>先把每张牌塞进弃牌堆，然后用
/// <c>discardCards[0].Owner</c> 决定谁来抽牌</b>。
/// </para>
/// <para>
/// 而共享弃牌堆里的牌在我们这边会被统一归到锚点名下（见 <see cref="SharedPileOwnerLateNormalizePatch" />），
/// 所以等轮到抽牌时那个 owner 已经变成锚点了 ——
/// 回声打计算下注就变成"弃掉自己的手牌、由锚点抽牌"：p2 这边看着一张都没抽到（抽到的牌随后在回合结束被清手牌丢掉了），
/// 锚点那边反而白赚一手。锚点自己打则恰好是对的，所以之前只看到 p2 有问题。
/// </para>
/// <para>
/// 修法不动本体的归属规则：进入这个方法时先记下"这批牌原本在谁的手里"，
/// 等紧接着那次 <c>CardPileCmd.Draw</c> 真的在为另一半抽牌时，把抽牌者改回手牌主人。
/// 只认"最近一次"且限定在同一小段窗口内，其它抽牌不受影响。
/// </para>
/// </remarks>
internal static class DiscardDrawTarget
{
    private const long WindowMs = 5000;

    private static Player? _intended;

    private static int _intendedCount;

    private static long _ticks;

    /// <summary>记下"这批要弃掉的牌原本在谁的手里"。</summary>
    public static void Remember(IEnumerable<CardModel>? cards)
    {
        _intended = null;

        if (!TogetherPair.IsActive || cards is null)
        {
            return;
        }

        var first = cards as IReadOnlyList<CardModel> is { Count: > 0 } list
            ? list[0]
            : cards.FirstOrDefault();

        if (first is null)
        {
            return;
        }

        if (PileOf(first) is not { Type: PileType.Hand } hand)
        {
            return;
        }

        if (TogetherPair.Anchor?.RunState is not { } runState)
        {
            return;
        }

        if (CardOwnershipImpl.HandOwnerOf(runState, hand) is not { } owner)
        {
            return;
        }

        _intended = owner;
        _intendedCount = cards.Count();
        _ticks = Environment.TickCount64;
    }

    /// <summary>把"在为另一半抽牌"纠正回手牌主人。</summary>
    /// <param name="drawCount">这次要抽几张。</param>
    /// <remarks>
    /// 必须连<b>抽牌张数</b>一起对：如果那次"弃牌再抽"实际没抽（比如手里是空的 → 张数 0），
    /// 记录就会一直挂到超时，这时别的成员刚好回合开始抽 5 张就会被误改道
    /// （实测 "p2 抽 10、p1 抽 0" 就是这么来的）。
    /// </remarks>
    public static bool TryRedirect(ref Player player, int drawCount)
    {
        var intended = _intended;
        if (intended is null || Environment.TickCount64 - _ticks > WindowMs)
        {
            return false;
        }

        if (drawCount != _intendedCount)
        {
            return false;
        }

        if (ReferenceEquals(intended, player))
        {
            // 本来就是对的（锚点自己打）：消费掉记录，不做改动。
            _intended = null;
            return false;
        }

        // 只有"这次抽牌本来按组里另一位成员算、但实际该给手牌主人"才改写。
        if (ReferenceEquals(player, intended)
            || !TogetherPair.IsMember(player)
            || !TogetherPair.IsMember(intended))
        {
            return false;
        }

        _intended = null;
        player = intended;
        return true;
    }

    private static CardPile? PileOf(CardModel card)
    {
        try
        {
            return card.Pile;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>进 <c>DiscardAndDraw</c> 时记下"这批牌在谁手里"。</summary>
[HarmonyPatch(typeof(CardCmd), nameof(CardCmd.DiscardAndDraw))]
internal static class DiscardAndDrawRememberPatch
{
    [HarmonyPrefix]
    private static void Prefix(IEnumerable<CardModel> __1)
    {
        try
        {
            DiscardDrawTarget.Remember(__1);
        }
        catch (Exception ex)
        {
            Main.Logger.Warn($"[together] 记录弃牌抽牌对象失败：{ex.Message}");
        }
    }
}

/// <summary>那次抽牌如果真的落到了另一半头上，就纠正回手牌主人。</summary>
[HarmonyPatch(
    typeof(CardPileCmd),
    nameof(CardPileCmd.Draw),
    new[] { typeof(PlayerChoiceContext), typeof(decimal), typeof(Player), typeof(bool) })]
internal static class DrawRedirectToHandOwnerPatch
{
    [HarmonyPrefix]
    private static void Prefix(ref Player __2, decimal __1)
    {
        try
        {
            if (DiscardDrawTarget.TryRedirect(ref __2, (int)__1))
            {
                CappedLog.Info("draw.redirect", $"抽牌对象修正回手牌主人：netId={__2.NetId}");
            }
        }
        catch (Exception ex)
        {
            Main.Logger.Warn($"[together] 抽牌对象修正失败：{ex.Message}");
        }
    }
}
