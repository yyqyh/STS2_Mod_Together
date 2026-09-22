using System.Reflection;

using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.TopBar;
using Together.Core.Combat;

namespace Together.Core.Patches.Deck;

/// <summary>
/// 牌堆计数 UI 的即时同步：把按钮上的数字按<b>真实张数</b>写死，不再靠事件累加。
/// </summary>
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

    /// <summary>
    /// 字段缓存。键必须带<b>字段名</b>：同一类型上要读三个字段，只用类型当键会拿回上一个 <c>FieldInfo</c>
    /// （实测报 <c>Object of type 'System.Int32' cannot be converted to type 'CardPile'</c>，整条刷新静默失效）。
    /// </summary>
    private static readonly Dictionary<(Type Type, string Name), FieldInfo?> FieldCache = [];

    private static readonly Dictionary<(Type Type, string Name), MethodInfo?> MethodCache = [];

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
        var key = (type, name);
        if (FieldCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        FieldInfo? field;
        try
        {
            field = AccessTools.Field(type, name);
        }
        catch (Exception)
        {
            field = null;
        }

        if (field is null)
        {
            WarnOnce($"读不到 {type.Name}.{name}：本体改过这个字段，牌堆计数即时刷新会失效。");
        }

        FieldCache[key] = field;
        return field;
    }

    private static MethodInfo? MethodOf(Type type, string name, params Type[] parameters)
    {
        var key = (type, name);
        if (MethodCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        MethodInfo? method;
        try
        {
            method = parameters.Length == 0
                ? AccessTools.Method(type, name)
                : AccessTools.Method(type, name, parameters);
        }
        catch (Exception)
        {
            method = null;
        }

        MethodCache[key] = method;
        return method;
    }

    /// <summary>
    /// 只警告一次。
    /// </summary>
    /// <remarks>
    /// 这些调用点都在"每张牌进出牌堆"的热路径上，出了问题会一瞬间刷上千行日志。
    /// </remarks>
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

/// <summary>入堆 / 出堆（含 <c>silent: true</c> 的静默搬运，抽牌走的就是出堆）之后，把计数写成真实张数。</summary>
[HarmonyPatch]
internal static class PileCountSyncOnChangePatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(CardPile), nameof(CardPile.AddInternal));
        yield return AccessTools.Method(typeof(CardPile), nameof(CardPile.RemoveInternal));
    }

    [HarmonyPostfix]
    private static void Postfix(CardPile __instance)
    {
        PileCountSync.SyncPile(__instance);
    }
}

/// <summary>
/// 登记两种"显示某口牌堆"的 UI：战斗牌堆按钮 + 顶栏卡组按钮（顺手对齐一次初始张数）。
/// </summary>
/// <remarks>
/// 战斗牌堆按钮挂在含虚方法 <c>Initialize</c> 的基类上：<c>NExhaustPileButton</c> 的覆写会调用 <c>base.Initialize</c>，
/// 所以三种按钮都会被登记到。
/// </remarks>
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

/// <summary>
/// 让 <c>CardModel.Pile</c> 在配对局里也能找到"借住"在另一半堆里的自己。
/// </summary>
/// <remarks>
/// 本体是 <c>Pile =&gt; _owner?.Piles.FirstOrDefault(p =&gt; p.Cards.Contains(this))</c>，只在<b>卡牌自己 owner</b>
/// 的堆集合里找自己。共享牌库打破了这条隐含约定：为了过本体批量 <c>CardPileCmd.Add</c> 的"同批 owner 必须一致"
/// 校验（洗牌走这条路，否则回合循环会死），离开手牌的牌被统一归到锚点名下 → 依赖 owner 反查堆的代码
/// （<c>RemoveFromCurrentPile</c>、<c>NPlayerHand.GetHandInsertIndex</c> 等）会认错堆，表现就是
/// "幽灵卡牌 / 牌同时留在两处 / 计数不刷新"。这里只做一件事：原查找失败时再去另一半的堆里找一遍。
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

        foreach (var other in TogetherPair.OthersOf(OwnerOf(__instance)))
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

    /// <summary><c>Owner</c> 的 getter 会 AssertMutable，对 canonical 模型会抛，所以兜一层。</summary>
    private static Player? OwnerOf(CardModel card)
    {
        try
        {
            return card.Owner;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
