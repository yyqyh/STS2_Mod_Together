using System.Reflection;

using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using Together.Core.Combat;
using Together.Core.Utils;

namespace Together.Core.Patches.Deck;

/// <summary>M1：共享牌库的共享逻辑（不含补丁特性）。</summary>
/// <remarks>
/// 做法是<b>让回声的访问入口返回锚点的同一个实例</b>，而不是"每次动作后复制"：复制方案要自己保证不漏点、
/// 顺序正确、两端一致；重定向方案的同步点直接归零。
/// <c>DrawPile / DiscardPile / ExhaustPile / PlayPile</c> 与 <c>Player.Deck</c>（run 期主卡组）回声重定向到锚点；
/// <c>Hand</c> <b>不重定向</b>（每人各自 10 张上限）；<c>PopulateCombatState</c> 只让锚点跑，
/// 否则同一套主卡组会被克隆两遍倒进同一个抽牌堆（双倍卡组）。
/// 弃牌堆必须跟着一起共享：本体洗牌的判定是"**该玩家自己的**抽牌堆空 且 **该玩家自己的**弃牌堆非空"，
/// 只共享抽牌堆会让回声永远等不到洗牌，等于软锁。
/// </remarks>
internal static class SharedPileImpl
{
    internal static readonly AccessTools.FieldRef<PlayerCombatState, Player> PlayerOf =
        AccessTools.FieldRefAccess<PlayerCombatState, Player>("_player");

    /// <summary><c>AllPiles</c> 是首次访问即固化的缓存数组（构造函数里就会访问），锚点换战斗状态时必须清掉。</summary>
    internal static readonly AccessTools.FieldRef<PlayerCombatState, CardPile[]?> PileCache =
        AccessTools.FieldRefAccess<PlayerCombatState, CardPile[]?>("_piles");

    // 四个战斗牌堆也是 get-only 自动属性：直接换 backing field，理由同 TogetherPair.DeckField。
    // Hand 刻意不在其中——手牌必须保持各自独立。
    private static readonly AccessTools.FieldRef<PlayerCombatState, CardPile> DrawField =
        AccessTools.FieldRefAccess<PlayerCombatState, CardPile>("<DrawPile>k__BackingField");

    private static readonly AccessTools.FieldRef<PlayerCombatState, CardPile> DiscardField =
        AccessTools.FieldRefAccess<PlayerCombatState, CardPile>("<DiscardPile>k__BackingField");

    private static readonly AccessTools.FieldRef<PlayerCombatState, CardPile> ExhaustField =
        AccessTools.FieldRefAccess<PlayerCombatState, CardPile>("<ExhaustPile>k__BackingField");

    private static readonly AccessTools.FieldRef<PlayerCombatState, CardPile> PlayField =
        AccessTools.FieldRefAccess<PlayerCombatState, CardPile>("<PlayPile>k__BackingField");

    /// <summary>把回声的四个战斗牌堆字段换成锚点那一份（Hand 不动）。</summary>
    internal static void AdoptAnchorPiles(PlayerCombatState echoState, PlayerCombatState anchorState)
    {
        DrawField(echoState) = anchorState.DrawPile;
        DiscardField(echoState) = anchorState.DiscardPile;
        ExhaustField(echoState) = anchorState.ExhaustPile;
        PlayField(echoState) = anchorState.PlayPile;

        // 字段换了 → 缓存数组作废，必须清掉让它按新字段重建。
        PileCache(echoState) = null;
    }

    internal static CardPile? PileOf(PlayerCombatState state, PileType type)
    {
        return type switch
        {
            PileType.Draw => state.DrawPile,
            PileType.Discard => state.DiscardPile,
            PileType.Exhaust => state.ExhaustPile,
            PileType.Play => state.PlayPile,
            _ => null,
        };
    }

    internal static void Redirect(PlayerCombatState state, PileType type, ref CardPile result)
    {
        var player = PlayerOf(state);

        if (!TogetherPair.IsEcho(player))
        {
            return;
        }

        var anchorState = ResolveAnchorState(state);
        if (anchorState is null || ReferenceEquals(anchorState, state))
        {
            LogRedirect(type, $"放弃：anchorState={Describe(anchorState)} selfState={state.GetHashCode()}");
            return;
        }

        var original = result;

        if (PileOf(anchorState, type) is { } shared)
        {
            result = shared;
            LogRedirect(type, $"已重定向（{original.Cards.Count} → {shared.Cards.Count} 张）");
        }
    }

    /// <summary>取锚点当前的战斗状态。</summary>
    /// <remarks>
    /// 优先读 <c>Anchor.PlayerCombatState</c>（实时值），只有它还没被赋值时才回退到
    /// <see cref="TogetherPair.AnchorCombatState" /> 记录的那份 —— "记录值可能因为两个玩家构造顺序不同而没写对"
    /// 是这里唯一能让重定向静默失效的点。
    /// </remarks>
    private static PlayerCombatState? ResolveAnchorState(PlayerCombatState self)
    {
        if (TogetherPair.Anchor?.PlayerCombatState is { } live && !ReferenceEquals(live, self))
        {
            return live;
        }

        var recorded = TogetherPair.AnchorCombatState;
        return recorded is not null && !ReferenceEquals(recorded, self) ? recorded : null;
    }

    private static readonly HashSet<PileType> _loggedRedirects = [];

    /// <summary>临时诊断：每种牌堆只报前 3 次，避免刷屏。</summary>
    private static void LogRedirect(PileType type, string message)
    {
        lock (_loggedRedirects)
        {
            if (_loggedRedirects.Count >= 12 || !_loggedRedirects.Add(type))
            {
                return;
            }
        }

        SelfCheck.Write($"[together][diag] echo {type} 重定向：{message}");
    }

    private static string Describe(PlayerCombatState? state)
    {
        return state is null ? "null" : $"set({state.GetHashCode()})";
    }

    /// <summary>战斗状态重建时的收尾：维护锚点引用、清缓存、拉平身体数值。</summary>
    internal static void OnCombatStateCreated(PlayerCombatState instance)
    {
        var player = PlayerOf(instance);

        if (!TogetherPair.IsActive)
        {
            SelfCheck.Write($"[together][diag] PlayerCombatState 建立 netId={player.NetId}（本局非共享角色局）");
            return;
        }

        if (TogetherPair.IsAnchor(player))
        {
            TogetherPair.SetAnchorCombatState(instance);
            SelfCheck.Write($"[together][diag] PlayerCombatState 建立 netId={player.NetId} 角色=锚点 → AnchorCombatState 已记录");

            // 回声可能先一步被构造（那时还没有锚点的战斗状态可换），这里补做字段替换 + 清缓存。
            foreach (var echo in TogetherPair.Echoes)
            {
                if (echo.PlayerCombatState is { } echoState)
                {
                    AdoptAnchorPiles(echoState, instance);
                }
            }
        }
        else if (TogetherPair.IsEcho(player))
        {
            TogetherPair.SetAnchorCombatState(TogetherPair.Anchor?.PlayerCombatState);
            SelfCheck.Write(
                $"[together][diag] PlayerCombatState 建立 netId={player.NetId} 角色=回声 → "
                + $"AnchorCombatState={Describe(TogetherPair.AnchorCombatState)}");

            if (TogetherPair.Anchor?.PlayerCombatState is { } anchorState)
            {
                AdoptAnchorPiles(instance, anchorState);
            }
        }
        else
        {
            // ★ 3~4 人局里的"其他玩家"走这里。
            // 这里**必须**是 else if / else 两段：早期版本只有 `if (锚点) … else …`，
            // 于是"不是锚点的人"一律被当成回声去做 AdoptAnchorPiles —— 第三个人的
            // 抽牌堆/弃牌堆/消耗堆/出牌堆的 backing field 全被换成了锚点那一份，
            // 表现就是"三人局里所有牌库混在一起"。非配对玩家要完整保持原版行为。
            SelfCheck.Write(
                $"[together][diag] PlayerCombatState 建立 netId={player.NetId}（非配对玩家 → 牌堆保持独立）");
            return;
        }

        // 关键自检：回声的 getter 现在能不能拿到锚点的牌堆。
        if (TogetherPair.Anchor?.PlayerCombatState is { } anchorNow)
        {
            SelfCheck.Write(
                $"[together][diag] 牌堆共享检查 draw={ReferenceEquals(anchorNow.DrawPile, instance.DrawPile)} "
                + $"discard={ReferenceEquals(anchorNow.DiscardPile, instance.DiscardPile)} "
                + $"hand（应为 False）={ReferenceEquals(anchorNow.Hand, instance.Hand)} "
                + $"| anchorDraw={anchorNow.DrawPile.Cards.Count} 本侧 draw={instance.DrawPile.Cards.Count}");
        }

        PileCache(instance) = null;

        // 生命 / 格挡的初始值也要拉平一次（Creature 构造函数是直接写字段、不走 setter 的）。
        BodyMirror.SyncAll();

        // 球位（故障机器人的 orb）同样并到同一口队列上；靠这里的"锚点状态建好后会补做"这条路径收齐。
        OrbSlotSharing.Link(player);

    }
}

/// <summary>战斗牌堆重定向：回声的四口堆都返回锚点那一份（手牌不在这里，各人各一份）。</summary>
[HarmonyPatch]
internal static class CombatPileRedirectPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(PlayerCombatState), "get_DrawPile");
        yield return AccessTools.Method(typeof(PlayerCombatState), "get_DiscardPile");
        yield return AccessTools.Method(typeof(PlayerCombatState), "get_ExhaustPile");
        yield return AccessTools.Method(typeof(PlayerCombatState), "get_PlayPile");
    }

    [HarmonyPostfix]
    private static void Postfix(PlayerCombatState __instance, ref CardPile __result, MethodBase __originalMethod)
    {
        SharedPileImpl.Redirect(__instance, PileTypeOf(__originalMethod.Name), ref __result);
    }

    private static PileType PileTypeOf(string getter)
    {
        return getter switch
        {
            "get_DrawPile" => PileType.Draw,
            "get_DiscardPile" => PileType.Discard,
            "get_ExhaustPile" => PileType.Exhaust,
            _ => PileType.Play,
        };
    }
}

/// <summary>run 期主卡组重定向：回声的 <c>Deck</c> 指向锚点那一份。</summary>
[HarmonyPatch(typeof(Player), "get_Deck")]
internal static class DeckRedirectPatch
{
    [HarmonyPostfix]
    private static void Postfix(Player __instance, ref CardPile __result)
    {
        if (!TogetherPair.IsEcho(__instance))
        {
            return;
        }

        if (TogetherPair.Anchor is { } anchor)
        {
            __result = anchor.Deck;
        }
    }
}

/// <summary>进战斗时只让锚点填充战斗牌堆。</summary>
[HarmonyPatch(typeof(Player), nameof(Player.PopulateCombatState))]
internal static class PopulateCombatStateAnchorOnlyPatch
{
    [HarmonyPrefix]
    private static bool Prefix(Player __instance)
    {
        LogDeck(__instance);

        // 球位 / 召唤物的字段替换必须在**赋值之后**做：PlayerCombatState 的构造函数后置补丁跑在
        // `PlayerCombatState = new PlayerCombatState(this)` 这句赋值之前，那时 player.PlayerCombatState 还是 null，
        // 我们根本拿不到要换的那份实例（实测 log：`已有战斗状态 1 人 → 本次归并 0 份`，等于一次都没换成）。
        // PopulateCombatState 是本体的"进战斗填充"入口，跑在这里一定已经赋值完毕，而且对回声也照样会被调用。
        OrbSlotSharing.Link(__instance);

        // 返回 false = 跳过原方法。回声不填充：主卡组只有一份，只能克隆一次。
        return !TogetherPair.IsEcho(__instance);
    }

    [HarmonyPostfix]
    private static void Postfix(Player __instance)
    {
        if (TogetherPair.IsAnchor(__instance))
        {
            var deckIds = string.Join(",", __instance.Deck.Cards.Select(c => c.Id.Entry));
            SelfCheck.Write(
                $"[together][diag] 填充后 netId={__instance.NetId} "
                + $"draw={__instance.PlayerCombatState?.DrawPile.Cards.Count} "
                + $"hand={__instance.PlayerCombatState?.Hand.Cards.Count} "
                + $"共享卡组({__instance.Deck.Cards.Count})=[{deckIds}]");
        }
    }

    /// <summary>临时诊断：把"卡组是否真的共享、里面几张牌"打出来。</summary>
    private static void LogDeck(Player player)
    {
        if (!TogetherPair.IsActive)
        {
            SelfCheck.Write($"[together][diag] PopulateCombatState netId={player.NetId}（本局非共享角色局）");
            return;
        }

        var anchor = TogetherPair.Anchor;
        var deckShared = anchor is not null
                         && TogetherPair.Echoes.All(echo => ReferenceEquals(anchor.Deck, echo.Deck));

        SelfCheck.Write(
            $"[together][diag] PopulateCombatState netId={player.NetId} "
            + $"isAnchor={TogetherPair.IsAnchor(player)} isEcho={TogetherPair.IsEcho(player)} "
            + $"deck={player.Deck.Cards.Count} "
            + $"anchorDeck={anchor?.Deck.Cards.Count} 成员数={TogetherPair.MemberCount} deckShared={deckShared}");

    }
}

[HarmonyPatch(typeof(PlayerCombatState), MethodType.Constructor, new[] { typeof(Player) })]
internal static class CombatStateCreatedPatch
{
    [HarmonyPostfix]
    private static void Postfix(PlayerCombatState __instance)
    {
        SharedPileImpl.OnCombatStateCreated(__instance);
    }
}

/// <summary>进阶之灾去重：共享卡组下本体"逐玩家各加一张"的诅咒会变成两张。</summary>
/// <remarks>
/// <c>AscensionManager.ApplyEffectsTo(player)</c> 是逐玩家调用的
/// （<c>RunManager.InitializeNewRun</c> 里 <c>foreach (player) ApplyAscensionEffects(player)</c>），
/// 里面那句 <c>player.Deck.AddInternal(AscendersBane, -1, silent: true)</c> 自然也就执行了两次。
/// 共享卡组下两个人的 <c>player.Deck</c> 指向同一份（<see cref="TogetherPair.Arm" /> 已经换掉了
/// 回声的 <c>Deck</c> 字段，getter 也做了重定向），于是同一张诅咒被加了两遍 ——
/// 表现就是开局卡组里有两张"进阶之灾"。
/// <b>为什么用 Postfix 去重，而不是 Prefix 直接跳过回声</b>：这个方法里还有<b>应当逐玩家生效</b>的部分
/// （<c>SubtractFromMaxPotionCount</c>，药水格是各算各的）。整段跳过会让回声的药水格比锚点多一个。
/// 所以让原方法照常跑，只把共享卡组里多出来的那张摘掉。
/// 摘牌要连 <c>RunState</c> 的卡牌登记一起清：只用 <c>RemoveInternal</c> 把牌从卡组里拿掉的话，
/// 这张牌还留在 <c>RunState</c> 的全卡表里，存档/校验和会看到一张"无主的牌"。
/// 判卡靠类型 + ID 双保险：类型名对不上（本体改过命名）时退回 ID 匹配，
/// 两样都对不上就什么都不做——绝不去删不认识的牌。
/// 除了挂在 <c>ApplyEffectsTo</c> 后面，<see cref="TogetherPair.Arm" /> 激活配对时也会对齐一次：
/// <b>读档/重连走的是 <c>FromSerializable</c>，那条路根本不会调 <c>ApplyEffectsTo</c></b>，
/// 只在后置补丁里去重的话，早先存下来的"两张进阶之灾"会被原样带回来。
/// </remarks>
[HarmonyPatch(typeof(AscensionManager), nameof(AscensionManager.ApplyEffectsTo))]
internal static class AscensionBaneDedupePatch
{
    private const string BanIdFragment = "ASCENDERS_BANE";

    [HarmonyPostfix]
    private static void Postfix(Player __0)
    {
        if (!TogetherPair.IsActive || TogetherPair.Anchor is not { } anchor)
        {
            return;
        }

        // 幂等：每次 ApplyEffectsTo 之后都对齐一次（新开局的两次、以及将来重连/加人再触发时都覆盖到）。
        DedupeSharedDeck(anchor);
    }

    /// <summary>把共享卡组里多出来的进阶之灾摘掉（幂等，可以随便多调）。</summary>
    internal static void DedupeSharedDeck(Player anchor)
    {
        var deck = anchor.Deck;
        var before = deck.Cards.Count;
        var extras = new List<CardModel>();
        var kept = false;

        foreach (var card in deck.Cards)
        {
            if (!IsAscendersBane(card))
            {
                continue;
            }

            if (!kept)
            {
                // 第一张留着：进阶之灾本来就是"卡组里有一张"。
                kept = true;
                continue;
            }

            extras.Add(card);
        }

        if (extras.Count == 0)
        {
            return;
        }

        foreach (var extra in extras)
        {
            deck.RemoveInternal(extra, silent: true);
            anchor.RunState.RemoveCard(extra);
        }

        Log.Info(
            $"[together] 进阶之灾去重：共享卡组本来 {before} 张，"
            + $"摘掉 {extras.Count} 张重复诅咒 → {deck.Cards.Count} 张");
    }

    private static bool IsAscendersBane(CardModel card)
    {
        if (card is AscendersBane)
        {
            return true;
        }

        try
        {
            return card.Id.Entry.Contains(BanIdFragment, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>让卡牌之间的排序变成<b>全序</b>，从而让 <c>StableShuffle</c> 真正"与输入顺序无关"。</summary>
/// <remarks>
/// 本体的 <c>StableShuffle</c> 是"先 <c>list.Sort()</c> 抹平顺序，再用 rng 打乱"，但它依赖
/// <see cref="CardModel.CompareTo" />：同名牌同升级时直接返回 0，而 <c>List.Sort</c> 是<b>不稳定排序</b>，
/// 这些"相等"的牌之间的先后仍然取决于输入顺序 —— 共生体两端输入顺序不同，洗牌结果就不同。
/// 这里在原本"相等"的情况下继续按<b>序列化等价键</b>比大小。同键的牌序列化内容完全一样，
/// 互换位置不影响校验和，所以排序结果只取决于集合内容，与输入顺序无关。
/// 这一条同时修好了"从抽牌堆随机取牌"的卡（破灭 <c>HAVOC</c>、灾变 <c>CATASTROPHE</c>、骚动 <c>UPROAR</c>、
/// 先制打击 <c>BEAT_DOWN</c>、寻者之击 <c>SEEKER_STRIKE</c>、能量电池 <c>POWER_CELL</c> 等，
/// 它们都写成 <c>Where(...).ToList().StableShuffle(rng)</c>），以及战斗中"弃牌堆洗回抽牌堆"。
/// 实测症状：打出破灭后主机侧对两只啃咬机各多打了 5 点伤害、客户端没有，随后客户端被主机踢下线。
/// 只在本局激活共生体时生效，只影响卡牌之间的比较（遗物 / 地图 / 事件列表不碰）。
/// </remarks>
[HarmonyPatch(typeof(CardModel), nameof(CardModel.CompareTo))]
internal static class DeterministicCardComparePatch
{
    [HarmonyPostfix]
    private static void Postfix(CardModel __instance, AbstractModel? other, ref int __result)
    {
        if (__result != 0 || !TogetherPair.IsActive || other is not CardModel otherCard)
        {
            return;
        }

        __result = string.CompareOrdinal(
            DeterministicCardOrder.SortKey(__instance),
            DeterministicCardOrder.SortKey(otherCard));
    }
}

/// <summary>初始洗牌（<c>CardPile.RandomizeOrderInternal</c>）前先把牌堆排成两端一致的顺序。</summary>
/// <remarks>
/// 战斗开始时会把主卡组复制进抽牌堆再 <c>UnstableShuffle</c> 打乱，而 <c>UnstableShuffle</c> 是
/// Fisher-Yates、<b>结果依赖输入顺序</b>。共生体下两端往主卡组里加牌的先后可能不同
/// （卡牌奖励是两端各自本地执行再互相同步的），于是洗出的顺序不同，校验和当场对不上。
/// 这里只挂具体的非泛型方法。**不要**去挂 <c>ListExtensions.UnstableShuffle&lt;T&gt;</c> 这类泛型洗牌方法：
/// .NET 对"引用类型实参的泛型方法"只生成一份代码，Harmony 打上去之后
/// <c>UnstableShuffle&lt;RelicModel&gt;</c>（遗物抓包）和 <c>StableShuffle&lt;MapPointType&gt;</c>（地图生成）
/// 都会跑进我们这份补丁里，实测直接把开局打成 <c>EntryPointNotFoundException</c>。
/// 战斗中洗牌改从比较器那一侧解决，见 <see cref="DeterministicCardComparePatch" />。
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

/// <summary>新跑局：等 <c>RunState</c> 完全构造完之后再激活共享配对。</summary>
/// <remarks>
/// 绝不能提前激活——<c>CreateShared</c> 会在设置 <c>player.RunState</c> 之后
/// 遍历该玩家的卡组给每张卡设 owner，提前激活会让第二次遍历读到被重定向的卡组，
/// 从而"同一张牌设两次 owner"抛异常（实测开局黑屏就是这么来的）。见 <see cref="TogetherPair.Arm" />。
/// </remarks>
[HarmonyPatch(typeof(RunState), nameof(RunState.CreateForNewRun))]
internal static class RunCreatedPatch
{
    [HarmonyPostfix]
    private static void Postfix(RunState __result)
    {
        TogetherPair.Arm(__result, isNewRun: true);
    }
}

/// <summary>读档 / 重连：同样在 <c>RunState</c> 构造完之后激活。</summary>
[HarmonyPatch(typeof(RunState), nameof(RunState.FromSerializable))]
internal static class RunLoadedPatch
{
    [HarmonyPostfix]
    private static void Postfix(RunState __result)
    {
        // 读档 / 重连：不能再加血量上限（存档里已经有了）。
        TogetherPair.Arm(__result, isNewRun: false);
    }
}
