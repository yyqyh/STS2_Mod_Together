using System.Reflection.Emit;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using Together.Core.Combat;
using Together.Core.Utils;
using Together;

namespace Together.Core.Patches.Deck;

// ======================================================================
// 合并自 Core/Multiplayer/CardOwnershipPatches.cs（2026-09-21 合并文件，正文未改动）
// ======================================================================
/// <summary>
/// M1：卡牌归属（owner）的维护规则。
/// </summary>
/// <remarks>
/// <para>
/// 规则一句话：<b>在手牌里属于手牌主人，离开手牌后归还锚点。</b>
/// </para>
/// <para>
/// 为什么必须成对：
/// </para>
/// <list type="number">
/// <item><description>
/// 进手牌要改成手牌主人，否则 <c>CardModel.Pile</c>（只在该卡 owner 的堆里找自己）会算出 null，
/// 而且打出去时扣的是另一个人的能量。
/// </description></item>
/// <item><description>
/// 离开手牌要还回锚点，否则共享的弃牌堆／抽牌堆里会出现**混合归属**的牌，
/// 而本体的批量 <c>CardPileCmd.Add</c> 有一条硬校验："同一次调用里所有牌 owner 必须一致"。
/// 洗牌正好走的是批量 Add（把弃牌堆混进抽牌堆），于是抛
/// <c>Tried to add cards with different owners to the same pile!</c>，
/// 直接把回合循环打死（实测"战斗中无法正确结束回合"就是这个）。
/// </description></item>
/// </list>
/// <para>
/// 翻牌用的 API 是本体自己留的那条路（"有主不可改"的唯一例外）：
/// <c>CardModel.GiveToAnotherPlayer</c>，也就是本体 <c>CardPileCmd.GiveToAnotherPlayer</c> 用的同一套。
/// </para>
/// </remarks>
internal static class CardOwnershipImpl
{
    /// <summary>
    /// 进手牌：把牌改成手牌主人。
    /// </summary>
    /// <remarks>
    /// 必须<b>先</b>把牌从原堆摘出来、<b>再</b>改 owner——顺序照抄本体
    /// <c>CardPileCmd.GiveToAnotherPlayer</c>。
    /// 反过来做的话，<c>Add</c> 内部要靠 <c>card.Pile</c> 摘除时 owner 已经变了、
    /// 牌在新 owner 的堆里找不到自己，摘除会静默失败 → 牌同时留在原堆和新堆里。
    /// </remarks>
    internal static void NormalizeForHand(CardModel card, CardPile hand)
    {
        if (!TogetherPair.IsActive || hand.Type != PileType.Hand)
        {
            return;
        }

        var runState = TogetherPair.Anchor?.RunState;
        if (runState is null)
        {
            return;
        }

        var handOwner = HandOwnerOf(runState, hand);
        if (handOwner is null || ReferenceEquals(card.Owner, handOwner))
        {
            return;
        }

        card.RemoveFromCurrentPile(true);
        card.GiveToAnotherPlayer(handOwner);
    }

    /// <summary>
    /// <b>只在"混合归属"的批量操作里</b>把 owner 统一到锚点。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 为什么必须"只在混合时"才动：本体的动画/节点流程里有一句
    /// <c>LocalContext.IsMe(card.Owner)</c> 判定（见 <c>CardPileCmd.GetTweenForCardsChangingPiles</c>），
    /// 不是"本地玩家的牌"就会 <c>continue</c>、**完全跳过节点处理**。
    /// 早期版本无条件把离开手牌的牌归到锚点，于是 p2 的牌在判定之前 owner 就变了
    /// → 手牌节点没人清 → 幽灵卡（p1 是锚点所以看不出来，正好对应"p1 正常、p2 有幽灵"）。
    /// </para>
    /// <para>
    /// 而同属一个玩家的批量（典型就是回合结束清手牌）本来就能通过本体的
    /// "同批 owner 必须一致"校验，**根本不需要归一化**。
    /// 真正需要的只有洗牌那类把不同玩家的牌混进共享堆的操作 —— 那类批量里没有手牌节点。
    /// </para>
    /// </remarks>
    internal static void NormalizeBatchIfMixed(IReadOnlyList<CardModel> cards)
    {
        // ⚠️ 已停用（2026-09-15）：调用点已经注释掉（见文件末尾 CardOwnerBatchPatch）。
        // 原因：这条"批量入堆前统一归属"和"入堆后统一归属"都属于**时机过早**的改写，
        // 会让 owner 与实际所在手牌脱钩 → 界面漏掉移除手牌节点 → 幽灵卡。
        // 现在归属改写统一交给 SharedPileOwnerLateNormalizePatch（挂在 AfterCardChangedPiles，
        // 即"搬完 + 动画播完"之后）。要恢复本方法，把调用点那行取消注释即可 ——
        // 但请先确认幽灵卡不会因此回归。
        if (!TogetherPair.IsActive || TogetherPair.Anchor is not { } anchor || cards.Count == 0)
        {
            return;
        }

        Player? first = null;
        var mixed = false;

        foreach (var card in cards)
        {
            var owner = OwnerOf(card);
            if (owner is null)
            {
                return;
            }

            if (first is null)
            {
                first = owner;
            }
            else if (!ReferenceEquals(first, owner))
            {
                mixed = true;
                break;
            }
        }

        if (!mixed)
        {
            return;
        }

        foreach (var card in cards)
        {
            if (!ReferenceEquals(OwnerOf(card), anchor))
            {
                card.GiveToAnotherPlayer(anchor);
            }
        }
    }

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

    /// <summary>手牌堆没有被共享，所以"这个 Hand 属于谁"是唯一的。</summary>
    internal static Player? HandOwnerOf(IRunState runState, CardPile pile)
    {
        foreach (var player in runState.Players)
        {
            if (player.PlayerCombatState?.Hand is { } hand && ReferenceEquals(hand, pile))
            {
                return player;
            }
        }

        return null;
    }

}

/// <summary>单张牌进堆的入口（抽牌走这里）。</summary>
[HarmonyPatch(
    typeof(CardPileCmd),
    nameof(CardPileCmd.Add),
    new[] { typeof(CardModel), typeof(CardPile), typeof(CardPilePosition), typeof(AbstractModel), typeof(bool) })]
internal static class CardOwnerSinglePatch
{
    [HarmonyPrefix]
    private static void Prefix(CardModel __0, CardPile __1)
    {
        try
        {
            CardOwnershipImpl.NormalizeForHand(__0, __1);
        }
        catch (Exception ex)
        {
            Log.Warn($"[together] owner 交接检查失败：{ex.Message}");
        }
    }
}

/// <summary>
/// 批量进堆的入口：只在"混合归属"时把 owner 统一到锚点（典型场景是洗牌）。
/// </summary>
[HarmonyPatch(
    typeof(CardPileCmd),
    nameof(CardPileCmd.Add),
    new[]
    {
        typeof(IEnumerable<CardModel>), typeof(CardPile), typeof(CardPilePosition),
        typeof(AbstractModel), typeof(bool), typeof(bool),
    })]
internal static class CardOwnerBatchPatch
{
    [HarmonyPrefix]
    private static void Prefix(IEnumerable<CardModel> __0)
    {
        // 只在参数本身是实体集合时预先枚举（惰性序列不能安全预枚举，原方法还要再枚举一次）。
        if (__0 is not IReadOnlyList<CardModel> cards)
        {
            return;
        }

        try
        {
            // 已停用（时机过早，会造成幽灵卡）：归属改写改在 SharedPileOwnerLateNormalizePatch。
            // CardOwnershipImpl.NormalizeBatchIfMixed(cards);
        }
        catch (Exception ex)
        {
            Log.Warn($"[together] 批量归属规整失败：{ex.Message}");
        }
    }
}

// ======================================================================
// 合并自 Core/Multiplayer/CardLastHandOwnerPatch.cs（2026-09-21 合并文件，正文未改动）
// ======================================================================
/// <summary>
/// 记住每张牌"最后一次躺在谁的手牌里"。
/// </summary>
/// <remarks>
/// <para>
/// 共生体下共享牌堆里的牌必须<b>统一归锚点</b>：本体的 <c>CardModel.Pile</c> 是用
/// <c>_owner.Piles</c> 反查自己所在的堆的，牌要是带着别人的归属躺在锚点的堆里，
/// 迟早会有一处反查不到（更别说洗牌那条"同一堆的牌 owner 必须一致"的校验）。
/// </para>
/// <para>
/// 但"归锚点"会丢掉一个信息：<b>这张牌原本是谁的</b>。而本体不少遗物/能力恰恰是靠
/// <c>card.Owner == 自己</c> 来认领"我的牌"的 —— 例如金纸（<c>JossPaper</c>）：
/// <c>AfterCardExhausted</c> 里 <c>if (card.Owner == base.Owner)</c> 才累计消耗数。
/// 于是 P2 消耗自己的手牌时，牌已经被归一成锚点，P2 的金纸永远不计数。
/// </para>
/// <para>所以这里把"最后的手牌主人"单独记一份，供下面的补丁在派发钩子时临时还原。</para>
/// </remarks>
internal static class CardLastHandOwner
{
    private static readonly ConditionalWeakTable<CardModel, Player> LastHand = new();

    public static void Remember(CardModel card, Player owner)
    {
        lock (LastHand)
        {
            LastHand.Remove(card);
            LastHand.Add(card, owner);
        }
    }

    public static bool TryGet(CardModel card, out Player? owner)
    {
        return LastHand.TryGetValue(card, out owner);
    }
}

/// <summary>
/// <c>AfterCardExhausted</c> 派发期间，把卡牌的归属临时还原成"最后持有它的玩家"。
/// </summary>
/// <remarks>
/// <para>
/// 牌进消耗堆时已经按共享牌堆的规则归一成锚点了，而归属于谁正是金纸这类遗物的判据。
/// 派发钩子前临时改回、钩子跑完（含其中的 await）再改回来，既让遗物认得出"这是我的牌"，
/// 又不破坏共享牌堆那条"堆里的牌归属一致"的不变量。
/// </para>
/// <para>
/// 因为 <c>Hook.AfterCardExhausted</c> 是 async 方法，Prefix/Postfix 都跑在同步段里，
/// 所以恢复动作要把返回的 <c>Task</c> 包一层，等它真正结束再执行。
/// </para>
/// </remarks>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardExhausted))]
internal static class ExhaustedOwnerForHooksPatch
{
    /// <summary>参数位置：combatState=0, choiceContext=1, <b>card=2</b>, causedByEthereal=3。</summary>
    [HarmonyPrefix]
    private static void Prefix(CardModel __2, ref Player? __state)
    {
        __state = null;

        if (!TogetherPair.IsActive || __2 is null)
        {
            return;
        }

        if (!CardLastHandOwner.TryGet(__2, out var lastOwner) || lastOwner is null)
        {
            return;
        }

        if (ReferenceEquals(__2.Owner, lastOwner))
        {
            return;
        }

        __state = __2.Owner;
        __2.GiveToAnotherPlayer(lastOwner);

        CappedLog.Info(
            "owner.restore",
            $"消耗结算：把 {__2.Id.Entry} 的归属临时还给 netId={lastOwner.NetId}"
            + $"（结算前已按共享牌堆归一为 netId={__state?.NetId}）");
    }

    [HarmonyPostfix]
    private static void Postfix(CardModel __2, Player? __state, ref Task __result)
    {
        if (__state is null || __result is null || __2 is null)
        {
            return;
        }

        __result = RestoreAfterAsync(__result, __2, __state);
    }

    private static async Task RestoreAfterAsync(Task task, CardModel card, Player original)
    {
        try
        {
            await task;
        }
        finally
        {
            card.GiveToAnotherPlayer(original);
        }
    }
}

// ======================================================================
// 合并自 Core/Multiplayer/DifferentOwnersCheckPatch.cs（2026-09-21 合并文件，正文未改动）
// ======================================================================
/// <summary>
/// 方法 1（重做版）：打掉批量 <c>CardPileCmd.Add</c> 里那条"同批 owner 必须一致"的校验，
/// 让共享牌堆里的牌保持<b>自然归属</b>。
/// </summary>
/// <remarks>
/// <para>
/// 为什么必须这么做：那条校验的前提是"每个玩家的牌堆各自独立"。共享牌库下共享的弃牌堆／抽牌堆
/// 本来就该同时装着两个人的牌，而<b>洗牌</b>正是"把弃牌堆混进抽牌堆"的批量 Add ——
/// 一旦抛 <c>…different owners…</c>，<b>回合循环直接终止、战斗卡住</b>。
/// </para>
/// <para>
/// 之前的临时办法是"把共享堆里牌的 owner 统一改成锚点"去迎合这条校验，但它连带出两个问题：
/// <list type="bullet">
/// <item><description><b>幽灵卡</b>：<c>CardModel.Pile</c> 按 owner 反查堆，owner 改了之后反查失效。</description></item>
/// <item><description><b>变牌失败</b>：<c>CardCmd.Transform</c> 要求替换卡与原卡 owner 一致，owner 不再唯一对应"牌属于谁"就撞上。</description></item>
/// </list>
/// 所以正确解法是让这条校验失效，owner 保持自然归属。
/// </para>
/// <para>
/// <b>关键点（上一版失败的原因）</b>：<c>Add</c> 是 <c>async</c> 方法，Harmony 的 transpiler
/// 默认打在<b>存根</b>上（只有"创建状态机"那几条指令），真实代码在编译器生成的
/// <c>CardPileCmd+&lt;Add&gt;d__N.MoveNext</c> 里。所以必须显式把目标指到状态机的 <c>MoveNext</c>。
/// （日志里能看到别的 mod 也在打 <c>&lt;Add&gt;d__10.MoveNext</c>，佐证了这一点。）
/// </para>
/// </remarks>
[HarmonyPatch]
internal static class DifferentOwnersCheckPatch
{
    /// <summary>错误信息的关键片段。刻意只匹配片段：本体不同位置的措辞不完全一致。</summary>
    private const string MessageFragment = "different owners";

    [HarmonyTargetMethod]
    private static MethodBase TargetMethod()
    {
        foreach (var nested in typeof(CardPileCmd).GetNestedTypes(AccessTools.all))
        {
            if (!nested.Name.Contains("Add", StringComparison.Ordinal))
            {
                continue;
            }

            var moveNext = AccessTools.Method(nested, "MoveNext");
            if (moveNext is not null && ContainsFragment(moveNext))
            {
                return moveNext;
            }
        }

        throw new InvalidOperationException(
            $"在 CardPileCmd 的所有内嵌状态机里都没找到含 \"{MessageFragment}\" 的 MoveNext。" +
            "本体可能改过这条校验（或它不在状态机里），请重新核对后再启用本补丁。");
    }

    /// <summary>粗查：这条方法的 IL 里是否出现目标字符串（用于挑出正确的状态机）。</summary>
    private static bool ContainsFragment(MethodBase method)
    {
        try
        {
            var body = method.GetMethodBody();
            if (body is null)
            {
                return false;
            }

            foreach (var instruction in PatchProcessor.GetCurrentInstructions(method, out _)
                         ?? Enumerable.Empty<CodeInstruction>())
            {
                if (instruction.opcode == OpCodes.Ldstr
                    && instruction.operand is string text
                    && text.Contains(MessageFragment, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception)
        {
            // 读不出 IL 就当没找到。
        }

        return false;
    }

    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var list = instructions.ToList();

        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].opcode != OpCodes.Ldstr
                || list[i].operand is not string text
                || !text.Contains(MessageFragment, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // 往后找 newobj（构造异常对象）与其后的 throw，允许中间夹 nop 等填充指令。
            for (var j = i + 1; j < Math.Min(i + 8, list.Count); j++)
            {
                if (list[j].opcode != OpCodes.Newobj)
                {
                    continue;
                }

                // newobj 消耗刚 push 的字符串并留下异常对象；换成 pop 保持栈平衡。
                list[j] = new CodeInstruction(OpCodes.Pop);

                for (var k = j + 1; k < Math.Min(j + 4, list.Count); k++)
                {
                    if (list[k].opcode == OpCodes.Throw)
                    {
                        list[k] = new CodeInstruction(OpCodes.Nop);
                        return list;
                    }
                }
            }

            throw new InvalidOperationException(
                $"在 \"{text}\" 附近没找到 newobj + throw 的常规形状，无法安全改写这段 IL。");
        }

        throw new InvalidOperationException(
            $"MoveNext 里没找到含 \"{MessageFragment}\" 的字符串，无法改写。");
    }
}

// ======================================================================
// 合并自 Core/Multiplayer/DiscardDrawTargetFix.cs（2026-09-21 合并文件，正文未改动）
// ======================================================================
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

// ======================================================================
// 合并自 Core/Multiplayer/HandOwnershipInvariantPatch.cs（2026-09-21 合并文件，正文未改动）
// ======================================================================
/// <summary>
/// 手牌归属不变量：<b>在谁手里就归谁</b>。
/// </summary>
/// <remarks>
/// <para>
/// 为什么要在数据层强制这件事：本体不少"手牌类"效果是拿<b>卡牌自己的 owner</b> 去推
/// "这是谁的手牌 / 该谁抽牌"的。例如 <c>CalculatedGamble</c>（计算下注）里
/// <c>PileType.Hand.GetPile(base.Owner).Cards</c> 取的是 owner 的手牌，
/// 而 <c>CardCmd.DiscardAndDraw</c> 干脆用 <c>discardCards[0].Owner</c> 决定"谁抽牌"。
/// </para>
/// <para>
/// 一旦手牌里混进 owner 不是手牌主人的牌（读档恢复、效果搬运等路径都可能这样），
/// 就会出现"p2 打计算下注，却把 p1 的手牌弃掉、并让 p1 抽牌"这种错位 ——
/// 表现就是"p2 的计算下注不能正确抽牌"。
/// </para>
/// <para>
/// 修法是在牌<b>进手牌</b>的那一刻（<c>CardPile.AddInternal</c>）就把 owner 对齐到该手牌的主人。
/// 这里是原地改 owner、<b>不</b>像 <see cref="CardOwnershipImpl.NormalizeForHand" /> 那样先摘牌：
/// 牌已经躺在这口手牌里了，新主人的堆集合里就有这口堆，<c>card.Pile</c> 依然找得到它，
/// 不会出现"牌同时挂在两处"。离手时的归属仍由 <see cref="SharedPileOwnerLateNormalizePatch" /> 处理。
/// </para>
/// </remarks>
[HarmonyPatch(typeof(CardPile), nameof(CardPile.AddInternal))]
internal static class HandOwnershipInvariantPatch
{
    [HarmonyPostfix]
    private static void Postfix(CardPile __instance, CardModel __0)
    {
        if (!TogetherPair.IsActive || __instance.Type != PileType.Hand)
        {
            return;
        }

        if (TogetherPair.Anchor?.RunState is not { } runState
            || CardOwnershipImpl.HandOwnerOf(runState, __instance) is not { } handOwner)
        {
            return;
        }

        // 记下"这张牌最后躺在谁的手牌里"：牌离手后会被归一成锚点归属，而遗物/能力
        // 认领"我的牌"时看的正是 card.Owner（例如金纸 JossPaper）。见 CardLastHandOwner。
        CardLastHandOwner.Remember(__0, handOwner);

        if (ReferenceEquals(__0.Owner, handOwner))
        {
            return;
        }

        CappedLog.Info(
            "hand.owner_fixed",
            $"进手牌归属对齐：card={__0.Id.Entry} → netId={handOwner.NetId}");

        __0.GiveToAnotherPlayer(handOwner);
    }
}

// ======================================================================
// 合并自 Core/Multiplayer/HandReturnOwnershipPatch.cs（2026-09-21 合并文件，正文未改动）
// ======================================================================
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

// ======================================================================
// 合并自 Core/Multiplayer/SharedPileOwnerLateNormalizePatch.cs（2026-09-21 合并文件，正文未改动）
// ======================================================================
/// <summary>
/// 归属改写的**正确时机**：牌彻底离开手牌、且搬运动画已经播完之后，再把共享堆里的牌统一归锚点。
/// </summary>
/// <remarks>
/// <para>
/// 为什么必须"晚"：本体 <c>CardPileCmd.Add</c> 内部的顺序是
/// <c>①从原堆摘除 → ②插入目标堆 → ③播搬运动画（内含移除手牌节点）→ ④派发 AfterCardChangedPiles</c>。
/// 我们原来在 ② 就改 owner（挂在 <c>CardPile.AddInternal</c> 上），而界面在 ③ 才做移除手牌节点的工作，
/// 且它处处依赖 <c>card.Owner</c> / <c>card.Pile</c>：
/// </para>
/// <list type="bullet">
/// <item><description><c>NPlayerHand</c> 里 <c>PileType.Hand.GetPile(card.Owner).Cards</c> —— 按下标摆放手牌。</description></item>
/// <item><description><c>CardPileCmd.MoveCardNodeToNewPileBeforeTween</c> 里 <c>hand.IsAncestorOf(cardNode)</c> 的判断。</description></item>
/// </list>
/// <para>
/// owner 与实际所在手牌对不上 → 界面漏掉这张牌 → 节点残留成"幽灵卡"（点它还能操作真实卡，
/// 因为它持有的 <c>CardModel</c> 是真的，只是 owner 已被改成锚点）。
/// </para>
/// <para>
/// 改到 <b>④ 之后</b>（本补丁的挂点）：动画已播完、节点已搬完，owner 再变就不影响界面；
/// 而共享堆在本轮结束时仍然均匀归锚点，洗牌那条"同批 owner 必须一致"的校验照样能过。
/// </para>
/// <para>
/// 判断条件是**目的堆类型**（Draw / Discard / Exhaust），与来源无关 ——
/// 因为"打出的牌"走的是 Hand → Play → Discard，若只筛"从手牌直接离开"就会漏掉它，
/// 那些牌会保持回声归属，洗牌时再次撞校验。
/// </para>
/// <para>
/// 但<b>光看堆类型不够</b>：3~4 人局里没选共享角色的玩家也有自己的 Draw / Discard / Exhaust，
/// 那些堆<b>不是</b>共享堆，别人的牌落进去不该被改写归属（改了会让 <c>card.Pile</c>
/// 按 owner 反查不到自己的堆 → 那个人的整个牌库也跟着乱）。所以还要确认
/// "这一堆就是锚点那一份"（回声的 getter 重定向后拿到的是同一实例，所以回声的牌也会被正确归一）。
/// </para>
/// <para>
/// 只有自检/诊断不需要它；这里是正常功能，不做开关。
/// </para>
/// </remarks>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardChangedPiles))]
internal static class SharedPileOwnerLateNormalizePatch
{
    /// <summary>位置参数：runState=0, combatState=1, <b>card=2</b>, oldPileType=3, clonedBy=4。</summary>
    [HarmonyPrefix]
    private static void Prefix(CardModel __2)
    {
        if (!TogetherPair.IsActive || TogetherPair.Anchor is not { } anchor)
        {
            return;
        }

        // 已经搬完、动画也播完了：此刻读它的当前堆是稳定的。
        var pile = __2.Pile;
        if (pile is null)
        {
            return;
        }

        if (pile.Type is not (PileType.Draw or PileType.Discard or PileType.Exhaust))
        {
            return;
        }

        // 只处理共享堆（= 锚点的那一份）。非配对玩家自己的堆原样不动。
        if (!IsSharedPile(pile))
        {
            return;
        }

        if (!ReferenceEquals(__2.Owner, anchor))
        {
            __2.GiveToAnotherPlayer(anchor);
        }
    }

    /// <summary>这一堆是不是共享堆（锚点的 Draw / Discard / Exhaust）。</summary>
    private static bool IsSharedPile(CardPile pile)
    {
        if (TogetherPair.Anchor?.PlayerCombatState is not { } anchorState)
        {
            return false;
        }

        return ReferenceEquals(pile, anchorState.DrawPile)
               || ReferenceEquals(pile, anchorState.DiscardPile)
               || ReferenceEquals(pile, anchorState.ExhaustPile);
    }
}
