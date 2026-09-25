using System.Reflection;

using Godot;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.TopBar;
using Together.Core.Alignment;
using Together.Core.Common;
using Together.Core.Diagnostics;
using Together.Core.Foundation;
using Together.Core.Shared.Body;
using Together.Core.Shared.Gold;
using Together.Core.Shared.Orb;
using Together.Core.Shared.Pet;
using Together.Core.Shared.Power;
using Together.Core.Ui;

namespace Together.Core.Shared.Deck;
/// <summary>牌堆计数 UI 的即时同步：把按钮上的数字按<b>真实张数</b>写死，不再靠事件累加。</summary>
/// <remarks>
/// 本体是"事件 + 每次 ±1"（<c>Math.Min(_currentCount + 1, _pile.Cards.Count)</c>），共享牌堆下有<b>两处必然漏事件</b>：
/// ① 别人的牌不播动画（<c>GetTweenForCardsChangingPiles</c> 里非本地玩家的牌直接 continue）→ 回声那侧的计数基本不刷新；
/// ② 静默搬运不发任何事件（归属规整必须先静默摘牌再改 owner）。
/// 漏一次就再也追不回来，所以改挂在<b>数据层</b>：<c>CardPile.AddInternal / RemoveInternal</c> 这两处无论静默与否、
/// 无论牌属于谁都会走到，两端顺序也一致；本体原来的 ±1 钩子留着不动（只会把值夹回真实值）。
/// 顶栏卡组按钮订阅同样那两个事件，所以也一起挂（它自己的 <c>OnPileContentsChanged</c> 本来就读真实张数）。
/// 反射读的是本体私有字段（<c>_pile</c> / <c>_countLabel</c> / <c>_currentCount</c>），读不到时只警告一次并安静地不干活
/// —— 不能让"计数 UI 没修好"升级成"整个 mod 挂掉"。
/// </remarks>
internal static class PileCountSync
{
    private const string PileFieldName = "_pile";

    private const string LabelFieldName = "_countLabel";

    private const string CountFieldName = "_currentCount";

    /// <summary>已登记的 UI 节点 →（它盯着的牌堆，以及怎么刷新它）。</summary>
    private static readonly List<Entry> Entries = [];

    private static bool _warned;

    private sealed class Entry
    {
        public required object Node { get; init; }

        public required CardPile Pile { get; init; }

        public required Action<object, CardPile> Sync { get; init; }
    }

    /// <summary>
    /// 登记一个"显示某个牌堆"的 UI 节点（战斗牌堆按钮 / 顶栏卡组按钮）。
    /// </summary>
    /// <param name="node">UI 节点。</param>
    /// <param name="declaringType">字段 <c>_pile</c> 的声明类型（战斗牌堆按钮是 <c>NCombatCardPile</c>）。</param>
    /// <param name="sync">拿到节点后怎么把它的显示刷新到真实张数。</param>
    public static void Bind(object node, Type declaringType, Action<object, CardPile> sync)
    {
        if (node is not GodotObject instance || !GodotObject.IsInstanceValid(instance))
        {
            return;
        }

        Unbind(node);

        if (FieldOf(declaringType, PileFieldName)?.GetValue(node) is not CardPile pile)
        {
            return;
        }

        Entries.Add(new Entry { Node = node, Pile = pile, Sync = sync });
        SyncPile(pile);
    }

    /// <summary>节点离开场景树 / 被销毁时注销，避免留下对已死 Godot 对象的引用。</summary>
    public static void Unbind(object node)
    {
        for (var i = Entries.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(Entries[i].Node, node))
            {
                Entries.RemoveAt(i);
            }
        }
    }

    /// <summary>某个牌堆的内容变了 → 把所有盯着它的 UI 刷成真实张数。</summary>
    public static void SyncPile(CardPile? pile)
    {
        if (pile is null || Entries.Count == 0)
        {
            return;
        }

        // 只关心界面上有计数的堆（Hand / Play 没有按钮）。
        if (pile.Type is not (PileType.Draw or PileType.Discard or PileType.Exhaust or PileType.Deck))
        {
            return;
        }

        // 快照：刷新过程中可能有节点被判定失效并从表里摘掉。
        foreach (var entry in Entries.ToArray())
        {
            if (!ReferenceEquals(entry.Pile, pile))
            {
                continue;
            }

            if (entry.Node is not GodotObject instance || !GodotObject.IsInstanceValid(instance))
            {
                Unbind(entry.Node);
                continue;
            }

            try
            {
                entry.Sync(entry.Node, entry.Pile);
            }
            catch (Exception ex)
            {
                WarnOnce($"刷新牌堆计数 UI 失败：{ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>战斗牌堆按钮：把 <c>_currentCount</c> 与标签一起写成真实张数。</summary>
    public static void WriteCountLabel(object node, CardPile pile)
    {
        var count = pile.Cards.Count;
        var countField = FieldOf(typeof(NCombatCardPile), CountFieldName);

        if (countField?.GetValue(node) is int current && current == count)
        {
            // 已经是真实值：不重复调 SetTextAutoSize（免得白白重建一次文本排版）。
            return;
        }

        countField?.SetValue(node, count);

        if (FieldOf(typeof(NCombatCardPile), LabelFieldName)?.GetValue(node) is not { } label)
        {
            return;
        }

        InvokeSetText(label, count.ToString());

        // 消耗堆按钮一开始是隐藏的，本体靠"牌落进堆"那次事件播进入动画才让它显形。
        // 共享堆里的牌大多属于锚点 → 回声那一侧永远等不到那个事件 → 整只按钮都不出现
        // （数字再准也没用）。这里补一次显形：只在"堆里有牌而按钮还藏着"时动手。
        if (count > 0 && node is NExhaustPileButton exhaust && !exhaust.Visible)
        {
            exhaust.AnimIn();
            exhaust.Enable();
        }
    }

    /// <summary>顶栏卡组按钮：触发它自己的重算（它内部会读真实张数）。</summary>
    public static void RefreshDeckButton(object node, CardPile pile)
    {
        var method = MethodOf(node.GetType(), "OnPileContentsChanged");
        if (method is null)
        {
            WarnOnce("顶栏卡组按钮上没有找到 OnPileContentsChanged，卡组张数只能靠本体自己刷新。");
            return;
        }

        method.Invoke(node, null);
    }

    private static void InvokeSetText(object label, string text)
    {
        var method = MethodOf(label.GetType(), "SetTextAutoSize", typeof(string));
        if (method is null)
        {
            WarnOnce("牌堆计数标签上没有找到 SetTextAutoSize，计数 UI 无法即时刷新。");
            return;
        }

        method.Invoke(label, [text]);
    }

    private static FieldInfo? FieldOf(Type type, string name)
    {
        var field = ModelAccess.FieldOf(type, name);
        if (field is null)
        {
            WarnOnce($"读不到 {type.Name}.{name}：本体改过这个字段，牌堆计数即时刷新会失效。");
        }

        return field;
    }

    private static MethodInfo? MethodOf(Type type, string name, params Type[] parameters)
    {
        return ModelAccess.MethodOf(type, name, parameters);
    }

    /// <summary>只警告一次（这些调用点都在"每张牌进出牌堆"的热路径上，出问题会一瞬间刷上千行日志）。</summary>
    private static void WarnOnce(string message)
    {
        if (_warned)
        {
            return;
        }

        _warned = true;
        Log.Warn($"[together] {message}");
    }
}

/// <summary>
/// "这张牌现在躺在哪口堆里"的索引：<c>CardModel → CardPile</c>。
/// </summary>
/// <remarks>
/// 本体 <c>CardModel.Pile</c> 是 <c>_owner?.Piles.FirstOrDefault(p =&gt; p.Cards.Contains(this))</c> ——
/// 只在<b>牌自己 owner</b> 的堆集合里找。共享牌库下这层关系会断（牌 owner 是锚点、牌却躺在回声的手牌里），
/// 所以需要一层兜底（见 <see cref="CardPileLookupPatch" />）。
/// 兜底要是现扫"所有成员的所有堆"，每次调用都是 O(成员数 × 牌数)；而 <c>Pile</c> 是热路径，
/// "牌不在任何堆里"（生成牌 / 奖励预览 / 搬运途中）又是常态，等于白扫很多遍。
/// 这里改用本体自己唯一的两处入堆 / 出堆收口（<c>CardPile.AddInternal</c> / <c>CardPile.RemoveInternal</c>，
/// 静默搬运也一定会走到，<see cref="PileCountSyncOnChangePatch" /> 用的就是这两个点）维护索引 → 查询 O(1)。
/// <b>value 用弱引用包一层</b>：<c>ConditionalWeakTable</c> 会对 value 持强引用，而 run 期主卡组的牌会活整局，
/// 直接存 <c>CardPile</c> 会把每场战斗的临时牌堆钉在内存里。
/// </remarks>
internal static class CardPileIndex
{
    private sealed class Slot
    {
        public required WeakReference<CardPile> Pile { get; init; }
    }

    private static readonly ConditionalWeakTable<CardModel, Slot> Index = new();

    /// <summary>牌进堆：覆盖旧记录（换堆是"先 Add 新堆、再 Remove 旧堆"，见 <see cref="OnRemoved" />）。</summary>
    public static void OnAdded(CardPile pile, CardModel card)
    {
        Index.Remove(card);
        Index.Add(card, new Slot { Pile = new WeakReference<CardPile>(pile) });
    }

    /// <summary>牌出堆：只有"当前记录的就是这口堆"时才删 —— 否则会把刚写好的新记录误删。</summary>
    public static void OnRemoved(CardPile pile, CardModel card)
    {
        if (Index.TryGetValue(card, out var slot)
            && slot.Pile.TryGetTarget(out var current)
            && ReferenceEquals(current, pile))
        {
            Index.Remove(card);
        }
    }

    /// <summary>查询；索引过期（堆已被回收 / 复核不通过）就顺手摘掉，返回 null 让调用方走别的路。</summary>
    public static CardPile? Lookup(CardModel card)
    {
        if (!Index.TryGetValue(card, out var slot))
        {
            return null;
        }

        if (!slot.Pile.TryGetTarget(out var pile) || !pile.Cards.Contains(card))
        {
            // 复核：万一有路径绕过 Add/RemoveInternal 直改 _cards，宁可当没索引，也不返回一口不含这张牌的堆。
            Index.Remove(card);
            return null;
        }

        return pile;
    }
}

/// <summary>入堆 / 出堆（含 <c>silent: true</c> 的静默搬运，抽牌走的就是出堆）之后，维护归属索引并把计数写成真实张数。</summary>
[HarmonyPatch]
internal static class PileCountSyncOnChangePatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(CardPile), nameof(CardPile.AddInternal));
        yield return AccessTools.Method(typeof(CardPile), nameof(CardPile.RemoveInternal));
    }

    [HarmonyPostfix]
    private static void Postfix(CardPile __instance, CardModel __0, MethodBase __originalMethod)
    {
        if (__originalMethod.Name == nameof(CardPile.AddInternal))
        {
            CardPileIndex.OnAdded(__instance, __0);
        }
        else
        {
            CardPileIndex.OnRemoved(__instance, __0);
        }

        PileCountSync.SyncPile(__instance);
    }
}

/// <summary>登记两种"显示某口牌堆"的 UI：战斗牌堆按钮 + 顶栏卡组按钮（顺手对齐一次初始张数）。</summary>
/// <remarks>战斗牌堆按钮挂在含虚方法 <c>Initialize</c> 的基类上：<c>NExhaustPileButton</c> 的覆写会调用
/// <c>base.Initialize</c>，所以三种按钮都会被登记到。</remarks>
[HarmonyPatch]
internal static class PileCountBindPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(NCombatCardPile), nameof(NCombatCardPile.Initialize));
        yield return AccessTools.Method(typeof(NTopBarDeckButton), nameof(NTopBarDeckButton.Initialize));
    }

    [HarmonyPostfix]
    private static void Postfix(object __instance)
    {
        switch (__instance)
        {
            case NCombatCardPile pile:
                PileCountSync.Bind(pile, typeof(NCombatCardPile), PileCountSync.WriteCountLabel);
                break;

            case NTopBarDeckButton deck:
                PileCountSync.Bind(deck, typeof(NTopBarDeckButton), PileCountSync.RefreshDeckButton);
                break;
        }
    }
}

/// <summary>UI 离开场景树 / 被销毁时注销（顶栏卡组按钮本体就是在 PREDELETE 通知里退订自己事件的）。</summary>
[HarmonyPatch]
internal static class PileCountUnbindPatch
{
    /// <summary>Godot 的 <c>NOTIFICATION_PREDELETE</c>。</summary>
    private const int PredeleteNotification = 1;

    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(NCombatCardPile), nameof(NCombatCardPile._ExitTree));
        yield return AccessTools.Method(typeof(NTopBarDeckButton), nameof(NTopBarDeckButton._Notification));
    }

    [HarmonyPostfix]
    private static void Postfix(object __instance, object[] __args)
    {
        // 顶栏卡组按钮每次通知都会被调到，只在"即将销毁"那条里注销。
        if (__instance is NTopBarDeckButton && (__args.Length == 0 || (int)__args[0] != PredeleteNotification))
        {
            return;
        }

        PileCountSync.Unbind(__instance);
    }
}

/// <summary>让 <c>CardModel.Pile</c> 在配对局里也能找到"借住"在另一半堆里的自己。</summary>
/// <remarks>
/// 本体是 <c>Pile =&gt; _owner?.Piles.FirstOrDefault(p =&gt; p.Cards.Contains(this))</c>，只在<b>卡牌自己 owner</b>
/// 的堆集合里找自己。共享牌库打破了这条隐含约定：一张牌可能"借住"在另一半的共享堆里
/// （牌的 owner 是它自己的自然归属，而它此刻躺在锚点的弃牌堆里），依赖 owner 反查堆的代码
/// （<c>RemoveFromCurrentPile</c>、<c>NPlayerHand.GetHandInsertIndex</c> 等）就可能认错堆，表现就是
/// "幽灵卡牌 / 牌同时留在两处 / 计数不刷新"。
/// 这里只做一件事：原查找失败时先查 <see cref="CardPileIndex" />（本体入堆 / 出堆时维护的 O(1) 索引），
/// 索引里没有（过期 / 映射没覆盖到）才退回"去其他成员的堆里扫一遍"。正常路径下不会触发，是张安全网。
/// </remarks>
[HarmonyPatch(typeof(CardModel), "get_Pile")]
internal static class CardPileLookupPatch
{
    [HarmonyPostfix]
    private static void Postfix(CardModel __instance, ref CardPile? __result)
    {
        if (__result is not null || !TogetherPair.IsActive)
        {
            return;
        }

        if (CardPileIndex.Lookup(__instance) is { } indexed)
        {
            __result = indexed;
            return;
        }

        foreach (var other in TogetherPair.OthersOf(ModelAccess.OwnerOf(__instance)))
        {
            foreach (var pile in other.Piles)
            {
                if (pile.Cards.Contains(__instance))
                {
                    __result = pile;
                    return;
                }
            }
        }
    }

}
