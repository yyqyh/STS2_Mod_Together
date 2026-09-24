using System.Threading.Tasks;
using System.Reflection;

using Godot;

using HarmonyLib;

using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

using Together.Core.Combat;
using Together.Core.Settings;
using Together.Core.Utils;

namespace Together.Core.Patches;

/// <summary>原版事件在共生体下的"两个人同时动同一张牌"问题。</summary>
/// <remarks>
/// 联机时每个玩家各有一份事件实例（<c>EventModel.IsShared=false</c>），而共生体<b>共用一副卡组</b>：
/// P1 还停在"选一张牌附魔"时，P2 可能已经把同一张牌附魔 / 移除了 —— P1 再选它就会撞上
/// <c>Cannot enchant …</c>（附魔不可叠加）或 <c>You cannot remove a card that is not in the deck.</c>，
/// 异常抛在 <c>SetEventFinished</c> 之前 → <b>事件不结束、房间出不去</b>。
/// 对策（默认开启，不改变事件形态）：① 共享卡组一变就按最新状态重建本机选牌界面的候选；
/// ② 在附魔 / 移除 / 变牌入口把已失效的选择跳过而不是抛异常；
/// ③ 选牌界面"建到玩家眼前才算数"（见 <see cref="EventEnchantSelection" />）。
/// 事件本身一律照原版"每人一份、各自选"（<c>IsDeterministic =&gt; !IsShared</c>，翻成共享会少一次校验和）。
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

    /// <summary>锚点 netId（诊断用：说明"共享卡组选牌"会落在哪个窗口）。</summary>
    public static ulong? AnchorNetId => TogetherPair.Anchor?.NetId;

    /// <summary>下一次"只给候选列表"的附魔选牌该由谁做（由调用点自己登记，见 <see cref="PendingSelectorRegisterPatch" />）。</summary>
    private static ulong? _pendingSelectorNetId;

    public static void SetPendingSelector(Player? player)
    {
        _pendingSelectorNetId = player?.NetId;
    }

    public static ulong? TakePendingSelector()
    {
        var netId = _pendingSelectorNetId;
        _pendingSelectorNetId = null;
        return netId;
    }

    /// <summary>按 netId 找共生体成员（找不到返回 null）。</summary>
    public static Player? FindMember(ulong netId)
    {
        if (TogetherPair.Anchor is { } anchor && anchor.NetId == netId)
        {
            return anchor;
        }

        foreach (var echo in TogetherPair.Echoes)
        {
            if (echo.NetId == netId)
            {
                return echo;
            }
        }

        return null;
    }
}

/// <summary>"只给候选列表"的附魔选牌调用点（蓝宝石种子「播种」+ 皇家印章）：登记"这次是谁在选"。</summary>
/// <remarks>
/// 那条重载没有玩家参数，本体只能从 <c>cards[0].Owner</c> 反推选择者，而共享卡组里牌的 owner 两端不一致
/// → 双向死锁。这两个调用点手里就有 owner，所以在这里登记，选牌时取用并清空。
/// 以后再遇到同类调用点，往 <see cref="TargetMethods" /> 加一行即可。
/// </remarks>
[HarmonyPatch]
internal static class PendingSelectorRegisterPatch
{
    /// <summary>要挂的目标方法：事件侧（蓝宝石种子「播种」）+ 遗物侧（皇家印章）。</summary>
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(SapphireSeed), "Plant");
        yield return AccessTools.Method(typeof(RoyalStamp), "AfterObtained");
    }

    /// <summary>两个目标方法都是 <c>async</c> 方法，前缀在方法体真正跑起来之前执行，所以先登记后调用。</summary>
    [HarmonyPrefix]
    private static void Prefix(object __instance)
    {
        switch (__instance)
        {
            case EventModel evt:
                EventFlow.SetPendingSelector(evt.Owner);
                CappedLog.Info("event.register", $"登记本次选牌的玩家（事件 {evt.GetType().Name}）→ netId{evt.Owner?.NetId}");
                break;

            case RelicModel relic:
                EventFlow.SetPendingSelector(relic.Owner);
                CappedLog.Info("event.register", $"登记本次选牌的玩家（遗物 {relic.GetType().Name}）→ netId{relic.Owner?.NetId}");
                break;
        }
    }
}

// ======================================================================================
// 1) 本机正在开的"卡组选牌"界面：卡组一变就按最新状态重建候选
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

    private static readonly AccessTools.FieldRef<NDeckEnchantSelectScreen, HashSet<CardModel>> SelectedCardsField =
        AccessTools.FieldRefAccess<NDeckEnchantSelectScreen, HashSet<CardModel>>("_selectedCards");

    public static void Opened(NCardGridSelectionScreen screen)
    {
        _screen = screen;
        _isDeckScreen = IsDeckScreen(screen);

        if (_isDeckScreen)
        {
            var cards = CardsField(screen);
            CappedLog.Info(
                "event.screen",
                $"本机打开了「卡组选牌」界面：候选 {cards?.Count ?? 0} 张，选择者={SelectorOf(screen)?.NetId}"
                + $"（本机 netId={LocalContext.NetId}）");
        }
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

            // 玩家已经在界面上选了牌（正在确认/预览）时不要重建网格，免得把界面搞乱——交给应用前的兜底处理
            if (screen is NDeckEnchantSelectScreen { } enchantScreen
                && SelectedCardsField(enchantScreen) is { Count: > 0 })
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

    /// <summary>开屏那一刻判断：候选是不是都在主卡组里。</summary>
    /// <remarks>
    /// 只有"从主卡组选牌"的界面（事件附魔 / 商店删牌 / 升级）才该被刷新；战斗中"从抽牌堆 / 弃牌堆选牌"
    /// 的界面候选不在卡组里，被按"不在卡组就删"过滤会整屏空掉。开屏时所有候选都合法，所以这里判断最准。
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

    /// <summary>本体是按 <c>cards[0].Owner</c> 决定"谁来选"的，这里只把这个值打出来（诊断用）。</summary>
    private static Player? SelectorOf(NCardGridSelectionScreen screen)
    {
        var cards = CardsField(screen);
        return cards is { Count: > 0 } ? cards[0].Owner : null;
    }
}

/// <summary>界面开始等选择时登记 / 被销毁时注销（<c>CardsSelected</c> 是各方共用的入口）。</summary>
[HarmonyPatch]
internal static class DeckSelectionLifecyclePatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(NCardGridSelectionScreen), nameof(NCardGridSelectionScreen.CardsSelected));
        yield return AccessTools.Method(typeof(NCardGridSelectionScreen), nameof(NCardGridSelectionScreen._ExitTree));
    }

    [HarmonyPostfix]
    private static void Postfix(NCardGridSelectionScreen __instance, MethodBase __originalMethod)
    {
        if (__originalMethod.Name == nameof(NCardGridSelectionScreen.CardsSelected))
        {
            DeckSelectionWatch.Opened(__instance);
        }
    }

    [HarmonyPrefix]
    private static void Prefix(NCardGridSelectionScreen __instance, MethodBase __originalMethod)
    {
        if (__originalMethod.Name == nameof(NCardGridSelectionScreen._ExitTree))
        {
            DeckSelectionWatch.Closed(__instance);
        }
    }
}

/// <summary>共享卡组一变（加牌 / 移除牌）就刷新本机开着的选牌界面。</summary>
[HarmonyPatch]
internal static class DeckChangeRefreshPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(CardPile), nameof(CardPile.AddInternal));
        yield return AccessTools.Method(typeof(CardPile), nameof(CardPile.RemoveInternal));
    }

    [HarmonyPostfix]
    private static void Postfix(CardPile __instance, MethodBase __originalMethod)
    {
        if (__instance.Type == PileType.Deck)
        {
            DeckSelectionWatch.RefreshIfOpen(__originalMethod.Name);
        }
    }
}

// ======================================================================================
// 2) 附魔入口：失效的选择跳过（不抛异常） + 成功附魔后刷新本机选牌界面
// ======================================================================================

/// <summary><c>CardCmd.Enchant</c>：目标牌已不能再附魔就跳过（返回 null，不抛 <c>Cannot enchant …</c>）；改完刷新界面。</summary>
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

    /// <summary>附魔不改牌堆，界面候选得单独刷一次。</summary>
    [HarmonyPostfix]
    private static void Postfix(CardModel card)
    {
        if (card.Pile?.Type == PileType.Deck)
        {
            DeckSelectionWatch.RefreshIfOpen("牌被附魔");
        }
    }
}

/// <summary>「卡组选牌界面到底建出来没有、为什么玩家看不到」的现场取证（只写日志、不改游戏状态）。</summary>
/// <remarks>
/// 症状：<c>本机打开了「卡组选牌」界面</c> 打了，玩家屏幕上却什么都没有，过一会儿才突然冒出来。
/// 原因在 <c>NOverlayStack</c> 的可见性：<c>Push()</c> 遇到"栈被盖住"（地图开着 / capstone 在用）会立刻对
/// 新界面调 <c>AfterOverlayHidden()</c> → 节点在树里但 <c>Visible=false</c>。另一个与 overlay 无关的坑：
/// 窗口没在前台（本地双开时 <c>LocalCoopClone</c> 会把当前不操作的窗口最小化），界面正常显示玩家也看不见。
/// 取证点：① 建好那一刻；② 同一帧末；③ 之后每次 overlay 栈变化。
/// </remarks>
internal static class SelectionScreenProbe
{
    /// <summary>overlay 栈里的完整列表（<c>NOverlayStack._overlays</c>，只读，用来打"到底压了几层、都是谁"）。</summary>
    private static readonly AccessTools.FieldRef<NOverlayStack, List<IOverlayScreen>> OverlaysField =
        AccessTools.FieldRefAccess<NOverlayStack, List<IOverlayScreen>>("_overlays");

    /// <summary>正在盯着的界面 → 它挂在 overlay 栈 <c>Changed</c> 上的回调（用于注销）。</summary>
    private static readonly Dictionary<NCardGridSelectionScreen, NOverlayStack.ChangedEventHandler> Watchers = [];

    /// <summary>界面建好后立刻调用：登记 + 打第一份状态。</summary>
    public static void Attach(NCardGridSelectionScreen screen, Player selector, int candidateCount)
    {
        CappedLog.Info("event.screen", $"「卡组选牌」界面已建：候选 {candidateCount} 张（选择者=netId{selector.NetId}）");
        Dump("建好那一刻", screen);

        // 同一帧末再打一次：Push 里有好几处"被盖住就先隐藏自己"的分支，帧末的状态才是玩家真正看到的。
        Callable.From(() => Dump("同一帧末", screen)).CallDeferred();
        WatchStack(screen);
    }

    /// <summary>界面关掉后调用：注销监听。</summary>
    public static void Detach(NCardGridSelectionScreen screen)
    {
        try
        {
            if (Watchers.Remove(screen, out var handler) && NOverlayStack.Instance is { } stack)
            {
                stack.Changed -= handler;
            }
        }
        catch (Exception)
        {
            // 注销失败无所谓：节点已经没了，Godot 自己会断掉信号。
        }
    }

    /// <summary>盯着 overlay 栈：只要栈一变（有人被压上来 / 被移除），就把本界面的可见性再打一遍。</summary>
    private static void WatchStack(NCardGridSelectionScreen screen)
    {
        try
        {
            if (NOverlayStack.Instance is not { } stack || Watchers.ContainsKey(screen))
            {
                return;
            }

            NOverlayStack.ChangedEventHandler handler = () => Dump("overlay 栈变化", screen);
            Watchers[screen] = handler;
            stack.Changed += handler;
        }
        catch (Exception ex)
        {
            CappedLog.Info("event.screen", $"监听 overlay 栈失败（忽略）：{ex.Message}");
        }
    }

    /// <summary>把"这张界面此刻到底能不能被看见"摊成一行日志。</summary>
    private static void Dump(string tag, NCardGridSelectionScreen screen)
    {
        try
        {
            if (!GodotObject.IsInstanceValid(screen))
            {
                CappedLog.Info("event.screen", $"[{tag}] 界面节点已失效（已释放）");
                return;
            }

            var stack = NOverlayStack.Instance;
            var isTop = stack is not null && ReferenceEquals(stack.Peek(), screen);
            var windowMode = DisplayServer.WindowGetMode();

            CappedLog.Info(
                "event.screen",
                $"[{tag}] Visible={screen.Visible} 在树里={screen.IsInsideTree()} 是否栈顶={isTop}"
                + $" overlay栈={DescribeStack(stack)}"
                + $" capstone={NCapstoneContainer.Instance?.CurrentCapstoneScreen?.GetType().Name ?? "无"}"
                + $" 地图开着={NMapScreen.Instance?.IsOpen ?? false}"
                + $" 焦点={screen.GetViewport()?.GuiGetFocusOwner()?.Name.ToString() ?? "无"}"
                + $" 窗口={windowMode}/有焦点={DisplayServer.WindowIsFocused()}");
        }
        catch (Exception ex)
        {
            CappedLog.Info("event.screen", $"[{tag}] 打印界面状态失败（忽略）：{ex.Message}");
        }
    }

    private static string DescribeStack(NOverlayStack? stack)
    {
        if (stack is null)
        {
            return "无";
        }

        try
        {
            var overlays = OverlaysField(stack);
            return $"{stack.ScreenCount} 层[自下而上 {string.Join(" → ", overlays.Select(o => o.GetType().Name))}]";
        }
        catch (Exception)
        {
            return $"{stack.ScreenCount} 层";
        }
    }
}

/// <summary>附魔选牌的界面流程（选择者由 <see cref="EnchantSelectionPatches" /> 定）。</summary>
/// <remarks>
/// 本机负责这次选择时走 <see cref="ShowLocalSelectionAsync" />；否则 <c>WaitForRemoteChoice</c> 等对面，
/// 由对面的 <c>SyncLocalChoice</c> 把结果送回（选择者按 netId 认，两端必然一致）。
/// </remarks>
internal static class EventEnchantSelection
{
    /// <summary>本机这次选牌总共愿意等多久；超了才认输（按空选择收场）。</summary>
    private const long LocalSelectionBudgetMs = 60_000;

    /// <summary>被盖住 / 界面被销毁之后，隔多久重弹一次。</summary>
    private const double RetryDelaySeconds = 0.7;

    /// <summary>本体重写：选择者由调用方指定，不再用 <c>cards[0].Owner</c> 猜。</summary>
    public static async Task<IEnumerable<CardModel>> RunAsync(
        Player selector,
        IReadOnlyList<CardModel> candidates,
        EnchantmentModel enchantment,
        int amount,
        CardSelectorPrefs prefs)
    {
        if (enchantment is null)
        {
            return Array.Empty<CardModel>();
        }

        // 每次调用都重算：共享卡组随时可能被对面改（附魔 / 移除 / 加牌）。
        List<CardModel> Filter()
        {
            return candidates.Where(card => card.Pile?.Type == PileType.Deck && enchantment!.CanEnchant(card)).ToList();
        }

        var cards = Filter();
        if (cards.Count == 0)
        {
            return Array.Empty<CardModel>();
        }

        if (cards.Count <= prefs.MinSelect)
        {
            return cards;
        }

        if (selector.Creature.IsDead)
        {
            return Array.Empty<CardModel>();
        }

        CappedLog.Info(
            "event.selector",
            $"共享卡组选牌（附魔 {enchantment?.Id.Entry}×{amount}）：选择者=netId{selector.NetId}"
            + $"（锚点=netId{EventFlow.AnchorNetId}，本机 netId={LocalContext.NetId}"
            + $"，本机是否弹界面={LocalContext.IsMe(selector)}，候选 {cards.Count} 张）");

        var choiceId = RunManager.Instance.PlayerChoiceSynchronizer.ReserveChoiceId(selector);

        if (LocalContext.IsMe(selector))
        {
            // 【先建再等】把界面真正建给玩家看，然后等结果。细节见 ShowLocalSelectionAsync。
            var chosen = await ShowLocalSelectionAsync(selector, Filter, enchantment!, amount, prefs);

            RunManager.Instance.PlayerChoiceSynchronizer.SyncLocalChoice(
                selector,
                choiceId,
                PlayerChoiceResult.FromMutableDeckCards(chosen));

            return chosen;
        }

        return (await RunManager.Instance.PlayerChoiceSynchronizer.WaitForRemoteChoice(selector, choiceId)).AsDeckCards();
    }

    /// <summary>「先建再等」：界面建到玩家眼前才算数 —— 被盖住就等、被销毁就重弹。</summary>
    /// <remarks>
    /// 本体 <c>NOverlayStack.Push</c> 有一条"栈被盖住就把新界面藏起来"的分支：只要此刻地图开着
    /// （事件结束后 <c>NEventRoom.Proceed()</c> 会打开地图让玩家点下一个节点）或 capstone 在用，
    /// 刚 Push 的界面会被立刻 <c>AfterOverlayHidden()</c> —— 节点在树里但 <c>Visible=false</c>，不会自己恢复
    /// （<c>run.tscn</c> 里地图画在 overlay 之上，硬显示也没用）。更糟的是那张"隐形界面"还压在栈里，玩家点地图
    /// 换房间把栈清掉时它会被销毁 → 收到 TaskCanceled → 这次附魔直接落空
    /// （实测 14:49 log：<c>Visible=False … 地图开着=True</c> → 4.2 秒后 TaskCanceled → 选了 0 张）。
    /// 所以：① 被盖住就不弹（每 <see cref="RetryDelaySeconds" /> 秒试一次）；② 弹完复核 <c>Visible</c>，
    /// 仍是 false 就立刻从栈里撤掉；③ 被销毁就用当前共享卡组重算候选重弹。
    /// 兜底：超过 <see cref="LocalSelectionBudgetMs" /> 才放弃（记日志 + 按空选择收场）；重试期间不阻塞游戏。
    /// </remarks>
    private static async Task<List<CardModel>> ShowLocalSelectionAsync(
        Player selector,
        Func<List<CardModel>> buildCandidates,
        EnchantmentModel enchantment,
        int amount,
        CardSelectorPrefs prefs)
    {
        var deadline = Time.GetTicksMsec() + LocalSelectionBudgetMs;
        var attempt = 0;

        while (true)
        {
            attempt++;

            if (IsStackCovered())
            {
                if (attempt == 1)
                {
                    CappedLog.Info(
                        "event.screen",
                        "地图/capstone 正开着：先等它关掉再弹「卡组选牌」界面"
                        + "（现在弹的话本体会把界面藏起来，玩家看不到）");
                }
            }
            else
            {
                // 每轮都重算候选：共享卡组随时可能被对面改（附魔 / 移除 / 加牌）。
                var valid = buildCandidates();
                if (valid.Count == 0)
                {
                    return [];
                }

                if (valid.Count <= prefs.MinSelect)
                {
                    return valid;
                }

                var list = OrderByDeck(selector, valid);
                var screen = NDeckEnchantSelectScreen.ShowScreen(list, enchantment, amount, prefs);
                SelectionScreenProbe.Attach(screen, selector, list.Count);

                if (screen.Visible)
                {
                    var startedAt = Time.GetTicksMsec();
                    try
                    {
                        var chosen = (await screen.CardsSelected()).ToList();
                        CappedLog.Info(
                            "event.submit",
                            $"「卡组选牌」界面交回结果：选择者=netId{selector.NetId}，选了 {chosen.Count} 张，"
                            + $"界面存活 {Time.GetTicksMsec() - startedAt} ms");
                        return chosen;
                    }
                    catch (TaskCanceledException)
                    {
                        CappedLog.Info(
                            "event.screen",
                            $"[第 {attempt} 次] 界面在选择完成前被销毁（多半是换房间把 overlay 栈清了）→ 稍后重弹一次");
                    }
                    finally
                    {
                        SelectionScreenProbe.Detach(screen);
                    }
                }
                else
                {
                    CappedLog.Info(
                        "event.screen",
                        $"[第 {attempt} 次] 界面 Push 后仍是 Visible=false（被地图/capstone 盖住）→ 立刻撤掉它，稍后重弹");
                    SelectionScreenProbe.Detach(screen);
                    DismissScreen(screen);
                }
            }

            if (Time.GetTicksMsec() > deadline)
            {
                CappedLog.Info(
                    "event.screen",
                    $"「卡组选牌」等了 {LocalSelectionBudgetMs / 1000} 秒还是没能让玩家选（试了 {attempt} 次）"
                    + "→ 本次按空选择收场（遗物/事件的这次附魔会落空）");
                return [];
            }

            await WaitAsync(RetryDelaySeconds);
        }
    }

    /// <summary>现在把界面 Push 进去会不会被本体当场藏起来（地图开着 / capstone 在用）。</summary>
    private static bool IsStackCovered()
    {
        return (NMapScreen.Instance?.IsOpen ?? false) || (NCapstoneContainer.Instance?.InUse ?? false);
    }

    /// <summary>把一张"不该留着"的界面撤掉（本体 <c>Remove</c> 里会 QueueFree，并让下面的界面重新显示）。</summary>
    private static void DismissScreen(NCardGridSelectionScreen screen)
    {
        try
        {
            if (NOverlayStack.Instance is { } stack)
            {
                stack.Remove(screen);
            }
            else if (GodotObject.IsInstanceValid(screen))
            {
                screen.QueueFree();
            }
        }
        catch (Exception ex)
        {
            CappedLog.Info("event.screen", $"撤掉界面失败（忽略）：{ex.Message}");
        }
    }

    /// <summary>按"卡组里的顺序"排候选（两端算出来一致，界面里牌的次序才稳定）。</summary>
    private static List<CardModel> OrderByDeck(Player selector, List<CardModel> cards)
    {
        var deck = PileType.Deck.GetPile(selector).Cards;
        var order = new Dictionary<CardModel, int>();
        for (var i = 0; i < deck.Count; i++)
        {
            order[deck[i]] = i;
        }

        return cards.OrderBy(card => order.TryGetValue(card, out var index) ? index : int.MaxValue).ToList();
    }

    /// <summary>等一小会儿（走场景树的 Timer，不阻塞主线程）。</summary>
    private static async Task WaitAsync(double seconds)
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            return;
        }

        var timer = tree.CreateTimer(seconds);
        await timer.ToSignal(timer, SceneTreeTimer.SignalName.Timeout);
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

// ======================================================================================
// 3) 选择同步的两端埋点：只给一份 log 也能看出"谁在等谁"
// ======================================================================================

/// <summary>选择同步两端各打一条：<c>event.wait</c>（本机开始等远端）与 <c>event.submit</c>（本机把结果发出去）。</summary>
/// <remarks>只看到 wait、两边都没有"界面已建"，就是"选择者算不一致"的死锁（修法见 <see cref="EnchantSelectionPatches" />）。</remarks>
[HarmonyPatch]
internal static class ChoiceSyncDiagPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(PlayerChoiceSynchronizer), nameof(PlayerChoiceSynchronizer.WaitForRemoteChoice));
        yield return AccessTools.Method(typeof(PlayerChoiceSynchronizer), nameof(PlayerChoiceSynchronizer.SyncLocalChoice));
    }

    [HarmonyPrefix]
    private static void Prefix(Player player, uint choiceId, MethodBase __originalMethod)
    {
        if (!EventFlow.Applies)
        {
            return;
        }

        var waiting = __originalMethod.Name == nameof(PlayerChoiceSynchronizer.WaitForRemoteChoice);
        CappedLog.Info(
            waiting ? "event.wait" : "event.submit",
            $"{(waiting ? "本机开始等待远端选择" : "本机提交选择")}：player=netId{player?.NetId} choiceId={choiceId}"
            + $"（本机 netId={LocalContext.NetId}）");
    }
}

// ======================================================================================
// 5) 附魔选牌入口（三个重载）
// ======================================================================================

/// <summary>附魔选牌的三个重载统统由我们接管：<b>选择者由调用方指定的玩家决定</b>（本体是拿 <c>cards[0].Owner</c> 猜的）。</summary>
/// <remarks>
/// 本体那条重载里是 <c>Player player = cards[0].Owner;</c>，再由 <c>ShouldSelectLocalCard(player)</c> 决定谁弹界面。
/// 共享卡组里混着两个人的牌，而牌的 <c>Owner</c> 引用两端并不一致（归属归一补丁会在动画时机改它），
/// 于是两台机器各自等对方 → 双向死锁、事件卡住。修法：带玩家参数的两条用调用方传进来的玩家（两端必然一致）；
/// 只给候选列表的那条（<c>SapphireSeed</c>「播种」/ <c>RoyalStamp</c>）用调用点登记的玩家，没登记就退回锚点。
/// </remarks>
[HarmonyPatch]
internal static class EnchantSelectionPatches
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        // 4 参：玩家 + 附魔（多数事件 / 遗物走这条）
        yield return AccessTools.Method(
            typeof(CardSelectCmd),
            nameof(CardSelectCmd.FromDeckForEnchantment),
            new[] { typeof(Player), typeof(EnchantmentModel), typeof(int), typeof(CardSelectorPrefs) });

        // 5 参：带额外过滤（自助指南走这条）
        yield return AccessTools.Method(
            typeof(CardSelectCmd),
            nameof(CardSelectCmd.FromDeckForEnchantment),
            new[]
            {
                typeof(Player), typeof(EnchantmentModel), typeof(int),
                typeof(Func<CardModel?, bool>), typeof(CardSelectorPrefs),
            });

        // 只给候选列表（蓝宝石种子「播种」/ 皇家印章）
        yield return AccessTools.Method(
            typeof(CardSelectCmd),
            nameof(CardSelectCmd.FromDeckForEnchantment),
            new[] { typeof(IReadOnlyList<CardModel>), typeof(EnchantmentModel), typeof(int), typeof(CardSelectorPrefs) });
    }

    [HarmonyPrefix]
    private static bool Prefix(object[] __args, ref Task<IEnumerable<CardModel>> __result)
    {
        if (!EventFlow.Applies)
        {
            return true;
        }

        var enchantment = (EnchantmentModel)__args[1];
        var amount = (int)__args[2];
        var prefs = (CardSelectorPrefs)__args[^1];

        if (__args[0] is Player player)
        {
            IReadOnlyList<CardModel> candidates = PileType.Deck.GetPile(player).Cards;

            if (__args.Length == 5 && __args[3] is Func<CardModel?, bool> filter)
            {
                candidates = candidates.Where(card => filter(card)).ToList();
            }

            __result = EventEnchantSelection.RunAsync(player, candidates, enchantment, amount, prefs);
            return false;
        }

        if (__args[0] is not IReadOnlyList<CardModel> cards || cards.Count == 0)
        {
            return true;
        }

        // 选择者：优先用调用点登记的玩家，没有就退回锚点（共享卡组的持有者）——两种都两端一致，不会死锁。
        var pendingNetId = EventFlow.TakePendingSelector();
        var selector = (pendingNetId is { } netId ? EventFlow.FindMember(netId) : null)
            ?? TogetherPair.Anchor
            ?? cards[0].Owner;
        if (selector is null)
        {
            return true;
        }

        CappedLog.Info(
            "event.selector",
            $"共享卡组选牌（附魔 {enchantment.Id.Entry}×{amount}）：调用方只给了候选列表"
            + $"（{(pendingNetId is null ? "没有登记玩家，退回锚点" : $"调用点登记了 netId{pendingNetId}")}）"
            + $"→ 选择者=netId{selector.NetId}"
            + $"（本机 netId={LocalContext.NetId}，本机是否弹界面={LocalContext.IsMe(selector)}，候选 {cards.Count} 张）");

        __result = EventEnchantSelection.RunAsync(selector, cards, enchantment, amount, prefs);
        return false;
    }
}

// ======================================================================================
// 6) 变牌兜底：另一份事件实例先动过的牌，别再硬变（本体在这两处是直接抛异常的）
// ======================================================================================

/// <summary>变牌前把"已经不能再变"的牌剔掉（本体对每张牌都是<b>直接抛异常</b>的，两份事件实例各动一次手很容易撞上）。</summary>
/// <remarks>
/// 本体 <c>CardCmd.Transform</c>：<c>!IsTransformable</c> → "… is un-transformable."、<c>Pile == null</c> → "… has no pile."，
/// 抛出去事件就中断。这里先过滤；全被过滤就整批跳过，事件照常往下走。
/// </remarks>
[HarmonyPatch(
    typeof(CardCmd),
    nameof(CardCmd.Transform),
    new[] { typeof(IEnumerable<CardTransformation>), typeof(Rng), typeof(CardPreviewStyle) })]
internal static class TransformGuardPatch
{
    [HarmonyPrefix]
    private static bool Prefix(
        ref IEnumerable<CardTransformation> transformations,
        ref Task<IEnumerable<CardPileAddResult>> __result)
    {
        if (!EventFlow.Applies || transformations is null)
        {
            return true;
        }

        var all = transformations as IList<CardTransformation> ?? transformations.ToList();
        var valid = all
            .Where(t => t.Original is { } card && card.Pile is not null && card.IsTransformable)
            .ToList();

        if (valid.Count == all.Count)
        {
            return true;
        }

        CappedLog.Info(
            "event.conflict",
            $"变牌跳过已失效的牌：{all.Count} → {valid.Count}（另一份事件实例先动过它）");

        if (valid.Count == 0)
        {
            __result = Task.FromResult(Enumerable.Empty<CardPileAddResult>());
            return false;
        }

        transformations = valid;
        return true;
    }
}
