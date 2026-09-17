using HarmonyLib;

using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;

namespace Together.Core.Multiplayer;

/// <summary>
/// "从共享堆拿牌回手"这一类效果的归属修正。
/// </summary>
/// <remarks>
/// <para>
/// 症状：捏奥之怒（NeowsFury）/挖掘（Dredge）/全息影像（Hologram）等从弃牌堆选牌回手时，
/// 弃牌堆确实少了几张，但牌<b>没有进自己的手牌</b>。
/// </para>
/// <para>
/// 原因：这类效果的目标手牌是本体<b>用卡牌自己的 owner 推出来的</b> ——
/// CardPileCmd.Add(cards, PileType.Hand, …) 内部走的是
/// <c>PileType.Hand.GetPile(cards.First().Owner)</c>。
/// 而共享堆（抽牌堆/弃牌堆/消耗堆）里的牌在我们的设计里统一归<b>锚点</b>
/// （见 SharedPileOwnerLateNormalizePatch，那是为了洗牌那条"同批 owner 必须一致"的校验）。
/// 于是回声打这类牌时，<c>cards.First().Owner</c> 是锚点 → 目标被推成<b>锚点的手牌</b>：
/// 牌从回声的屏幕上消失，却跑进锚点手里去了。
/// （反过来若进了自己的手但 owner 仍是锚点，界面又会因为 LocalContext.IsMe(card.Owner)
/// 不为真而不给这张牌建手牌节点 —— 同样是"看不见"。）
/// </para>
/// <para>
/// 解法：在搬运之前，把这批"从共享堆回手"的牌先改成<b>当前正在结算效果的那名玩家</b>，
/// 本体随后用 owner 推出的目标手牌就是他自己那口。判定"正在结算效果的人"用本体自己的
/// CombatManager.BeginCardOrPotionEffect / EndCardOrPotionEffect 深度计数
/// （它就在 finally 里配对，比我们自己去挂"开始出牌/结束出牌"稳），
/// 再要求两名配对玩家中<b>只有一个人</b>在执行效果 —— 嵌套效果（两个都在执行）时不猜，保持原版行为。
/// </para>
/// <para>
/// 只改 <c>PileType.Hand</c> 这一类目标：其余堆（抽/弃/消耗/出牌/卡组）在配对里本来就是同一份，
/// 用谁当 owner 推出来的都是同一个堆，没必要碰。
/// </para>
/// </remarks>
internal static class HandReturnOwnership
{
    /// <summary>当前正在结算卡牌/药水效果的那名配对玩家；判不出来时返回 null。</summary>
    public static Player? ActingPairMember()
    {
        if (!TogetherPair.IsActive)
        {
            return null;
        }

        if (TogetherPair.Anchor is not { } anchor || TogetherPair.Echoes.Count == 0)
        {
            return null;
        }

        if (CombatManager.Instance is not { } combat)
        {
            return null;
        }

        // 只有"组里恰好一个人正在结算效果"才敢下判断；多个/零个（嵌套效果或不在出牌期间）都保持原版行为。
        Player? acting = null;
        foreach (var member in TogetherPair.Members())
        {
            if (!combat.IsExecutingCardOrPotionEffect(member))
            {
                continue;
            }

            if (acting is not null)
            {
                return null;
            }

            acting = member;
        }

        if (acting is null)
        {
            return null;
        }

        return acting;
    }

    /// <summary>
    /// 这批牌要进"当前效果执行者的手牌"→ 先把它们改成那个人，本体随后自己会推出正确的目标堆。
    /// </summary>
    /// <returns>是否真的做了改写。</returns>
    public static bool RetargetToActingHand(IReadOnlyList<CardModel> cards)
    {
        if (cards.Count == 0)
        {
            return false;
        }

        // 优先信"谁正在结算效果"；认不出来（能力/遗物在钩子里触发，不在出牌期间）时，
        // 退回"这批牌是不是刚从某个共享堆里被某位配对玩家选出来的"。
        var acting = ActingPairMember() ?? SelectedFromPile.PlayerFor(cards);
        if (acting is null)
        {
            return false;
        }

        if (acting.PlayerCombatState?.Hand is null)
        {
            // 不是战斗期（没有手牌堆）→ 本体自己那句 GetPile 会抛，不关我们的事。
            return false;
        }

        // 先整体判断：只处理"全都还躺在共享堆里"的批次。
        // 只要有一张正在别人手里，就整批不动 —— 那说明这不是"从共享堆回手"这条路径。
        foreach (var card in cards)
        {
            if (PileOf(card) is not { } pile)
            {
                return false;
            }

            if (pile.Type is not (PileType.Draw or PileType.Discard or PileType.Exhaust))
            {
                return false;
            }
        }

        foreach (var card in cards)
        {
            if (ReferenceEquals(card.Owner, acting))
            {
                continue;
            }

            // 顺序照抄本体 CardPileCmd.GiveToAnotherPlayer / CardOwnershipImpl.NormalizeForHand：
            // 必须先把牌从原堆摘出来再改 owner（反过来 owner 变了会按 owner 反查不到旧堆）。
            card.RemoveFromCurrentPile(true);
            card.GiveToAnotherPlayer(acting);
        }

        SelfCheck.Write(
            $"[together][diag] 回手归属修正：{cards.Count} 张 → 手牌主人 netId={acting.NetId} "
            + $"(anchor={TogetherPair.IsAnchor(acting)} echo={TogetherPair.IsEcho(acting)})");

        return true;
    }

    /// <summary>
    /// 目标堆是明确给出的手牌堆时，把牌改成该手牌的主人。
    /// </summary>
    /// <remarks>
    /// 和单张路径的 CardOwnershipImpl.NormalizeForHand 同一个规则，
    /// 但<b>只对"不在任何手牌里"的牌动手</b>：正在某人手牌里的牌如果被改写 owner，
    /// 那边的 NPlayerHand 会认不出它（幽灵卡）。
    /// </remarks>
    public static void NormalizeIntoHand(CardModel card, CardPile hand)
    {
        if (PileOf(card) is { Type: PileType.Hand })
        {
            return;
        }

        CardOwnershipImpl.NormalizeForHand(card, hand);
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

/// <summary>
/// 记录"最近一次从战斗牌堆里选牌的是谁、选的是哪个堆"。
/// </summary>
/// <remarks>
/// 本体那些"从抽牌堆/弃牌堆选牌回手"的效果都先走
/// <c>CardSelectCmd.FromCombatPile(context, pile, player, prefs)</c>，其中 <c>player</c> 就是发起者。
/// 这类效果有一部分不在"出牌/用药水"期间（比如能力在回合开始触发、遗物触发），
/// 用效果深度计数认不出人，就用这条记录补上。
/// 匹配要求"这批牌现在仍在同一个堆实例里"，所以不会误伤别的搬运。
/// </remarks>
internal static class SelectedFromPile
{
    private static Player? _player;

    private static CardPile? _pile;

    public static void Record(Player player, CardPile pile)
    {
        _player = player;
        _pile = pile;
    }

    /// <summary>这批牌确实来自刚才那次选牌的那个堆 → 返回当时的发起者。</summary>
    public static Player? PlayerFor(IReadOnlyList<CardModel> cards)
    {
        if (_player is null || _pile is null || !TogetherPair.IsPaired(_player))
        {
            return null;
        }

        foreach (var card in cards)
        {
            if (!ReferenceEquals(PileOf(card), _pile))
            {
                return null;
            }
        }

        return _player;
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

/// <summary>记下"谁从哪个堆里选牌"，供 <see cref="SelectedFromPile" /> 查询。</summary>
[HarmonyPatch(
    typeof(CardSelectCmd),
    nameof(CardSelectCmd.FromCombatPile),
    new[]
    {
        typeof(PlayerChoiceContext), typeof(CardPile), typeof(Player),
        typeof(CardSelectorPrefs), typeof(Func<CardModel, bool>),
    })]
internal static class SelectedFromPilePatch
{
    [HarmonyPrefix]
    private static void Prefix(Player __2, CardPile __1)
    {
        try
        {
            SelectedFromPile.Record(__2, __1);
        }
        catch (Exception)
        {
            // 记录失败最多是"这次不修正"，不该影响原方法。
        }
    }
}

/// <summary>单张：<c>Add(card, PileType.Hand)</c>（全息影像、重磅出击等）。</summary>
[HarmonyPatch(
    typeof(CardPileCmd),
    nameof(CardPileCmd.Add),
    new[] { typeof(CardModel), typeof(PileType), typeof(CardPilePosition), typeof(AbstractModel), typeof(bool) })]
internal static class HandReturnSingleTypePatch
{
    [HarmonyPrefix]
    private static void Prefix(CardModel __0, PileType __1)
    {
        if (__1 != PileType.Hand)
        {
            return;
        }

        try
        {
            HandReturnOwnership.RetargetToActingHand([__0]);
        }
        catch (Exception ex)
        {
            Log.Warn($"[together] 回手归属修正失败：{ex.GetType().Name}: {ex.Message}");
        }
    }
}

/// <summary>批量：<c>Add(cards, PileType.Hand)</c>（捏奥之怒、挖掘、预言终局等）。</summary>
[HarmonyPatch(
    typeof(CardPileCmd),
    nameof(CardPileCmd.Add),
    new[]
    {
        typeof(IEnumerable<CardModel>), typeof(PileType), typeof(CardPilePosition),
        typeof(AbstractModel), typeof(bool),
    })]
internal static class HandReturnBatchTypePatch
{
    [HarmonyPrefix]
    private static void Prefix(IEnumerable<CardModel> __0, PileType __1)
    {
        if (__1 != PileType.Hand)
        {
            return;
        }

        try
        {
            HandReturnOwnership.RetargetToActingHand(__0 as IReadOnlyList<CardModel> ?? __0.ToList());
        }
        catch (Exception ex)
        {
            Log.Warn($"[together] 回手归属修正失败（批量）：{ex.GetType().Name}: {ex.Message}");
        }
    }
}

/// <summary>批量 + 明确给出的手牌堆：把牌归到该手牌的主人（补齐单张路径已有的规则）。</summary>
[HarmonyPatch(
    typeof(CardPileCmd),
    nameof(CardPileCmd.Add),
    new[]
    {
        typeof(IEnumerable<CardModel>), typeof(CardPile), typeof(CardPilePosition),
        typeof(AbstractModel), typeof(bool), typeof(bool),
    })]
internal static class HandReturnBatchPilePatch
{
    [HarmonyPrefix]
    private static void Prefix(IEnumerable<CardModel> __0, CardPile __1)
    {
        if (__1.Type != PileType.Hand)
        {
            return;
        }

        foreach (var card in __0 as IReadOnlyList<CardModel> ?? __0.ToList())
        {
            try
            {
                HandReturnOwnership.NormalizeIntoHand(card, __1);
            }
            catch (Exception ex)
            {
                Log.Warn($"[together] 手牌归属规整失败：{ex.GetType().Name}: {ex.Message}");
                return;
            }
        }
    }
}
