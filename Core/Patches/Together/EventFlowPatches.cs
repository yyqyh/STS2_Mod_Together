using System.Threading.Tasks;

using Godot;

using HarmonyLib;

using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Runs;

using Together.Core.Combat;
using Together.Core.Settings;
using Together.Core.Utils;

namespace Together.Core.Patches;

/// <summary>
/// 原版事件在共生体下的"两个人同时动同一张牌"问题。
/// </summary>
/// <remarks>
/// <para>
/// <b>问题</b>：联机时每个玩家各有一份事件实例（<c>EventModel.IsShared=false</c>），而共生体<b>共用一副卡组</b>。
/// 于是 P1 还停在"选一张牌附魔"的界面时，P2 可能已经把同一张牌附魔/移除了 —— P1 再选它就会撞上：
/// </para>
/// <list type="bullet">
/// <item><description>附魔：<c>EnchantmentModel.CanEnchant</c> 对"已有附魔的牌"返回 false（Sharp/Nimble/Swift 都不可叠加）
/// → <c>CardCmd.Enchant</c> 抛 <c>Cannot enchant …</c>；</description></item>
/// <item><description>移除：<c>CardPileCmd.RemoveFromDeck</c> 抛 <c>You cannot remove a card that is not in the deck.</c>。</description></item>
/// </list>
/// <para>
/// 异常抛在事件 <c>SetEventFinished(...)</c> 之前 → <b>事件不结束、房间出不去</b>（两端都会抛，所以不是"不同步"，
/// 但是卡死）。
/// </para>
/// <para>
/// <b>这里给的两层保护（默认开启，不影响原版流程）</b>：
/// </para>
/// <list type="number">
/// <item><description><b>刷新界面</b>：共享卡组一变（加牌 / 移除 / 附魔），就把本机正在开的选牌界面按"现在还有效"
/// 的候选重建一次 → 另一边已经改过的牌会从界面里消失，选不到。</description></item>
/// <item><description><b>应用前兜底</b>：万一在刷新落地之前就点了确认，则在 <c>CardCmd.Enchant</c> / <c>CardPileCmd.RemoveFromDeck</c>
/// 入口处把失效的那几张丢掉（跳过而不是抛异常）→ 事件照常走到结束，不卡房。</description></item>
/// </list>
/// <para>
/// 另外提供设置项 <c>ShareEvents</c>（设置页「事件改为共享」）：把原版事件整体换成共享事件（两人投票、只有一次选择），
/// 从根上消除并发；代价是事件奖励变成"整组一份"，并且共享事件结束时不再发校验和。
/// </para>
/// </remarks>
internal static class EventFlow
{
    /// <summary>本局是否管事件（共生体激活 + 联机）。</summary>
    public static bool Applies
    {
        get
        {
            if (!TogetherPair.IsActive)
            {
                return false;
            }

            return RunManager.Instance?.NetService is { } net && net.Type.IsMultiplayer();
        }
    }
}

// ======================================================================================
// 1) 设置项：把原版事件改成共享事件（两人投票）
// ======================================================================================

/// <summary>
/// 打开 <c>ShareEvents</c> 时，把非共享事件当作共享事件处理。
/// </summary>
/// <remarks>
/// <para>
/// 只打基类的 <c>get_IsShared</c>：本体已经有 9 个事件自己覆写成 <c>true</c>（它们走各自的实现，不受影响），
/// 这里只把"默认 false"的那些翻成 true → 变成"两人投票、票高的选项对所有人执行"，即<b>只有一次选择</b>。
/// </para>
/// <para>
/// 注意连带效果：<c>IsDeterministic =&gt; !IsShared</c> 会跟着变 false，该事件结束时不再发校验和（本体对共享事件就是这么设计的）。
/// </para>
/// </remarks>
[HarmonyPatch(typeof(EventModel), "get_IsShared")]
internal static class EventSharePatch
{
    [HarmonyPostfix]
    private static void Postfix(ref bool __result)
    {
        if (__result || !EventFlow.Applies || !TogetherSettingsSync.EffectiveShareEvents)
        {
            return;
        }

        __result = true;
        CappedLog.Info("event.share", "[settings] 事件按共享事件处理（两人投票，只有一次选择）");
    }
}

// ======================================================================================
// 2) 本机正在开的"卡组选牌"界面：卡组一变就按最新状态重建候选
// ======================================================================================

/// <summary>记录本机当前打开的卡组选牌界面，并在共享卡组变化时刷新它。</summary>
internal static class DeckSelectionWatch
{
    private static NCardGridSelectionScreen? _screen;

    /// <summary>本机这个界面是不是"从主卡组选牌"（战斗中"从抽牌堆/弃牌堆选"的界面不能被我们清候选）。</summary>
    private static bool _isDeckScreen;

    private static readonly AccessTools.FieldRef<NCardGridSelectionScreen, IReadOnlyList<CardModel>> CardsField =
        AccessTools.FieldRefAccess<NCardGridSelectionScreen, IReadOnlyList<CardModel>>("_cards");

    private static readonly AccessTools.FieldRef<NCardGridSelectionScreen, NCardGrid> GridField =
        AccessTools.FieldRefAccess<NCardGridSelectionScreen, NCardGrid>("_grid");

    private static readonly AccessTools.FieldRef<NDeckEnchantSelectScreen, EnchantmentModel> EnchantmentField =
        AccessTools.FieldRefAccess<NDeckEnchantSelectScreen, EnchantmentModel>("_enchantment");

    public static void Opened(NCardGridSelectionScreen screen)
    {
        _screen = screen;
        _isDeckScreen = IsDeckScreen(screen);
    }

    public static void Closed(NCardGridSelectionScreen screen)
    {
        if (ReferenceEquals(_screen, screen))
        {
            _screen = null;
            _isDeckScreen = false;
        }
    }

    /// <summary>共享卡组变了：把界面里"已经无效"的候选去掉，重建一次网格。</summary>
    public static void RefreshIfOpen(string why)
    {
        var screen = _screen;
        if (screen is null || !_isDeckScreen || !GodotObject.IsInstanceValid(screen))
        {
            return;
        }

        try
        {
            var cards = CardsField(screen);
            if (cards is null || cards.Count == 0)
            {
                return;
            }

            var valid = cards.Where(StillValid).ToList();
            if (valid.Count == cards.Count)
            {
                return;
            }

            CardsField(screen) = valid;
            GridField(screen)?.SetCards(valid, PileType.None, new List<SortingOrders> { SortingOrders.Ascending });

            CappedLog.Info(
                "event.refresh",
                $"选牌界面已按共享卡组的最新状态刷新（{why}）：候选 {cards.Count} → {valid.Count}");
        }
        catch (Exception ex)
        {
            // 刷新只影响体验，失败绝不能影响原版流程
            CappedLog.Info("event.refresh", $"刷新选牌界面失败（忽略）：{ex.Message}");
        }
    }

    /// <summary>这张候选现在还有效吗（还在卡组里 + 附魔界面的话还满足该附魔的条件）。</summary>
    private static bool StillValid(CardModel card)
    {
        if (card.Pile?.Type != PileType.Deck)
        {
            return false;
        }

        if (_screen is NDeckEnchantSelectScreen enchantScreen
            && EnchantmentField(enchantScreen) is { } enchantment
            && !enchantment.CanEnchant(card))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// 开屏那一刻判断：候选是不是都在主卡组里。
    /// </summary>
    /// <remarks>
    /// 只有"从主卡组选牌"的界面（事件附魔 / 商店删牌 / 升级）才该被刷新；
    /// 战斗中"从抽牌堆 / 弃牌堆选牌"的界面候选不在卡组里，一旦被我们按"不在卡组就删"过滤就会整屏空掉。
    /// 开屏时所有候选都是合法的，所以这里判断最准；之后再遇到"牌被移出卡组"也不会误判成非卡组界面。
    /// </remarks>
    private static bool IsDeckScreen(NCardGridSelectionScreen screen)
    {
        try
        {
            var cards = CardsField(screen);
            return cards is { Count: > 0 } && cards.All(card => card.Pile?.Type == PileType.Deck);
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>界面开始等待选择时登记（<c>CardsSelected</c> 是各方共用的入口，非虚方法）。</summary>
[HarmonyPatch(typeof(NCardGridSelectionScreen), nameof(NCardGridSelectionScreen.CardsSelected))]
internal static class DeckSelectionOpenedPatch
{
    [HarmonyPostfix]
    private static void Postfix(NCardGridSelectionScreen __instance)
    {
        DeckSelectionWatch.Opened(__instance);
    }
}

/// <summary>界面销毁时注销。</summary>
[HarmonyPatch(typeof(NCardGridSelectionScreen), nameof(NCardGridSelectionScreen._ExitTree))]
internal static class DeckSelectionClosedPatch
{
    [HarmonyPrefix]
    private static void Prefix(NCardGridSelectionScreen __instance)
    {
        DeckSelectionWatch.Closed(__instance);
    }
}

/// <summary>卡组里加牌 → 刷新（对面的"复制一张牌进卡组"之类）。</summary>
[HarmonyPatch(typeof(CardPile), nameof(CardPile.AddInternal))]
internal static class DeckChangeRefreshOnAddPatch
{
    [HarmonyPostfix]
    private static void Postfix(CardPile __instance)
    {
        if (__instance.Type == PileType.Deck)
        {
            DeckSelectionWatch.RefreshIfOpen("卡组加牌");
        }
    }
}

/// <summary>卡组里移除牌 → 刷新（对面的"删一张牌"）。</summary>
[HarmonyPatch(typeof(CardPile), nameof(CardPile.RemoveInternal))]
internal static class DeckChangeRefreshOnRemovePatch
{
    [HarmonyPostfix]
    private static void Postfix(CardPile __instance)
    {
        if (__instance.Type == PileType.Deck)
        {
            DeckSelectionWatch.RefreshIfOpen("卡组移除牌");
        }
    }
}

/// <summary>附魔不改牌堆，得单独盯一下。</summary>
[HarmonyPatch(typeof(CardCmd), nameof(CardCmd.Enchant), new[] { typeof(EnchantmentModel), typeof(CardModel), typeof(decimal) })]
internal static class DeckChangeRefreshOnEnchantPatch
{
    [HarmonyPostfix]
    private static void Postfix(CardModel card)
    {
        if (card.Pile?.Type == PileType.Deck)
        {
            DeckSelectionWatch.RefreshIfOpen("卡组里的牌被附魔");
        }
    }
}

// ======================================================================================
// 3) 应用前兜底：刷新没赶上时，丢掉失效的选择而不是抛异常
// ======================================================================================

/// <summary>附魔入口：目标牌已不能再附魔 → 跳过（返回 null），不再抛 <c>Cannot enchant …</c>。</summary>
[HarmonyPatch(typeof(CardCmd), nameof(CardCmd.Enchant), new[] { typeof(EnchantmentModel), typeof(CardModel), typeof(decimal) })]
internal static class EnchantApplyGuardPatch
{
    [HarmonyPrefix]
    private static bool Prefix(EnchantmentModel enchantment, CardModel card, ref EnchantmentModel? __result)
    {
        if (!EventFlow.Applies)
        {
            return true;
        }

        bool canEnchant;
        try
        {
            canEnchant = enchantment.CanEnchant(card);
        }
        catch (Exception)
        {
            canEnchant = false;
        }

        if (canEnchant)
        {
            return true;
        }

        __result = null;
        CappedLog.Info(
            "event.conflict",
            $"附魔跳过：{DeterministicCardOrder.DescribeCards([card], 1)} ← {enchantment?.Id.Entry}"
            + "（这张牌此刻已经不满足附魔条件，另一个人先动手了；跳过而不是抛异常，避免事件卡住）");
        return false;
    }
}

/// <summary>选牌入口：把"开屏前就已经失效"的候选先剔掉，避免 <c>ArgumentException("All cards must be in the player's deck and enchantable.")</c>。</summary>
[HarmonyPatch(
    typeof(CardSelectCmd),
    nameof(CardSelectCmd.FromDeckForEnchantment),
    new[] { typeof(IReadOnlyList<CardModel>), typeof(EnchantmentModel), typeof(int), typeof(CardSelectorPrefs) })]
internal static class EnchantSelectFilterPatch
{
    [HarmonyPrefix]
    private static void Prefix(EnchantmentModel enchantment, ref IReadOnlyList<CardModel> cards)
    {
        if (!EventFlow.Applies || cards is null || cards.Count == 0)
        {
            return;
        }

        var valid = cards.Where(card => card.Pile?.Type == PileType.Deck && enchantment.CanEnchant(card)).ToList();
        if (valid.Count == cards.Count)
        {
            return;
        }

        CappedLog.Info("event.conflict", $"选牌候选已剔除失效的牌：{cards.Count} → {valid.Count}");
        cards = valid;
    }
}

/// <summary>移除入口：只移除还在卡组里的牌；全都失效就整批跳过。</summary>
[HarmonyPatch(typeof(CardPileCmd), nameof(CardPileCmd.RemoveFromDeck), new[] { typeof(IReadOnlyList<CardModel>), typeof(bool) })]
internal static class RemoveFromDeckGuardPatch
{
    [HarmonyPrefix]
    private static bool Prefix(ref IReadOnlyList<CardModel> cards, ref Task __result)
    {
        if (!EventFlow.Applies || cards is null || cards.Count == 0)
        {
            return true;
        }

        var valid = cards.Where(card => card.Pile?.Type == PileType.Deck).ToList();
        if (valid.Count == cards.Count)
        {
            return true;
        }

        CappedLog.Info("event.conflict", $"从卡组移除：跳过已失效的牌 {cards.Count} → {valid.Count}");

        if (valid.Count == 0)
        {
            __result = Task.CompletedTask;
            return false;
        }

        cards = valid;
        return true;
    }
}
