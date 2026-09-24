using System.Reflection.Emit;
using System.Reflection;
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

/// <summary>
/// M1：卡牌归属（owner）的维护规则。
/// </summary>
/// <remarks>
/// 规则一句话：<b>牌归"它当前所在手牌"的主人；不在手牌里的牌保留自然归属，不再改写。</b>
/// <b>为什么不能把共享堆（抽牌堆/弃牌堆/消耗堆）里的牌统一改成锚点</b>：本体有二十多处逻辑拿
/// <c>card.Owner</c> 认领"这张牌是不是我的"——<c>JossPaper</c>（金纸）、<c>CharonsAshes</c>（卡戎之灰）、
/// <c>ForgottenSoul</c>、<c>BurningSticks</c>、<c>Tingsha</c>、<c>ToughBandages</c>、
/// <c>BansheesCry</c>、<c>PanachePower</c>、<c>GravityPower</c>、<c>JugglingPower</c>、<c>HexPower</c>……
/// 一旦归一，这些"我的牌"判据会统统算到锚点头上：回声的遗物/能力永远不计数、锚点的会多计数。
/// 当初要归一的唯一硬理由，是本体批量 <c>CardPileCmd.Add</c> 里那条"同一次调用里所有牌 owner 必须一致"的校验
/// （洗牌就是"弃牌堆 + 抽牌堆"一次批量 Add，否则抛
/// <c>Tried to add cards with different owners to the same pile!</c>）。那条校验的前提是
/// "每个玩家的牌堆各自独立"，在共享牌库下根本不成立——所以正解是让<b>那条校验失效</b>
/// （见 <see cref="DifferentOwnersCheckPatch" />），而不是反过来改牌的归属去迎合它。
/// 仍然保留的一条是"<b>进手牌就归手牌主人</b>"（见 <see cref="HandOwnershipInvariantPatch" /> 与
/// <see cref="CardOwnershipImpl.NormalizeForHand" />）：本体的手牌类效果大量用 <c>card.Owner</c> 反推
/// "这是谁的手牌 / 该谁抽牌"，手牌里混进别人的牌会直接错位。
/// 改归属走本体自己留的那条路（"有主不可改"的唯一例外）：
/// <c>CardModel.GiveToAnotherPlayer</c>，也就是本体 <c>CardPileCmd.GiveToAnotherPlayer</c> 用的同一套。
/// </remarks>
internal static class CardOwnershipImpl
{
    /// <summary>进手牌：把牌改成手牌主人。</summary>
    /// <remarks>
    /// 必须先摘牌、再改 owner（顺序照抄本体 <c>CardPileCmd.GiveToAnotherPlayer</c>）：反过来 <c>Add</c>
    /// 内部靠 <c>card.Pile</c> 摘除时 owner 已经变了、牌在新 owner 的堆里找不到自己，摘除静默失败
    /// → 牌同时留在原堆和新堆里。
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
/// 打掉批量 <c>CardPileCmd.Add</c> 里那条"同批 owner 必须一致"的校验，让共享牌堆里的牌保持<b>自然归属</b>。
/// </summary>
/// <remarks>
/// <b>配对局里这条是必须装上的</b>。那条校验的前提是"每个玩家的牌堆各自独立"：共享牌库下，
/// 共享的弃牌堆／抽牌堆本来就该同时装着两个人的牌，而<b>洗牌</b>正是"把弃牌堆混进抽牌堆"的批量 Add ——
/// 一旦抛 <c>…different owners…</c>，<b>回合循环直接终止、战斗卡住</b>。
/// 曾经的临时办法是"把共享堆里牌的 owner 统一改成锚点"去迎合这条校验，效果是连带出两个问题：
/// <b>幽灵卡</b>（<c>CardModel.Pile</c> 按 owner 反查堆，owner 被改写之后反查失效）、
/// <b>变牌失败</b>（<c>CardCmd.Transform</c> 要求替换卡与原卡 owner 一致）、
/// <b>"我的牌"判据集体失效</b>（金纸/卡戎之灰/探戈/绷带……全按 <c>card.Owner</c> 认领，归一后算到锚点头上）。
/// 所以归一路线已经撤掉，这里让这条校验失效即为其正解。
/// <b>关键点（上一版失败的原因）</b>：<c>Add</c> 是 <c>async</c> 方法，Harmony 的 transpiler
/// 默认打在<b>存根</b>上（只有"创建状态机"那几条指令），真实代码在编译器生成的
/// <c>CardPileCmd+&lt;Add&gt;d__N.MoveNext</c> 里。所以必须显式把目标指到状态机的 <c>MoveNext</c>。
/// （日志里能看到别的 mod 也在打 <c>&lt;Add&gt;d__10.MoveNext</c>，佐证了这一点。）
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

/// <summary>
/// 手牌归属不变量：<b>在谁手里就归谁</b>。
/// </summary>
/// <remarks>
/// 为什么要在数据层强制这件事：本体不少"手牌类"效果是拿<b>卡牌自己的 owner</b> 去推
/// "这是谁的手牌 / 该谁抽牌"的。例如 <c>CalculatedGamble</c>（计算下注）里
/// <c>PileType.Hand.GetPile(base.Owner).Cards</c> 取的是 owner 的手牌，
/// 而 <c>CardCmd.DiscardAndDraw</c> 干脆用 <c>discardCards[0].Owner</c> 决定"谁抽牌"。
/// 一旦手牌里混进 owner 不是手牌主人的牌（读档恢复、效果搬运等路径都可能这样），
/// 就会出现"p2 打计算下注，却把 p1 的手牌弃掉、并让 p1 抽牌"这种错位 ——
/// 表现就是"p2 的计算下注不能正确抽牌"。
/// 修法是在牌<b>进手牌</b>的那一刻（<c>CardPile.AddInternal</c>）就把 owner 对齐到该手牌的主人。
/// 这里是原地改 owner、<b>不</b>像 <see cref="CardOwnershipImpl.NormalizeForHand" /> 那样先摘牌：
/// 牌已经躺在这口手牌里了，新主人的堆集合里就有这口堆，<c>card.Pile</c> 依然找得到它，
/// 不会出现"牌同时挂在两处"。
/// 这是<b>唯一</b>还在改写 owner 的地方：离手之后一律保留自然归属（"这张牌是谁的"）。
/// 共享堆因此会同时装着两个人的牌，批量 <c>CardPileCmd.Add</c> 的那条同 owner 校验由
/// <see cref="DifferentOwnersCheckPatch" /> 打掉。
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

/// <summary>
/// "从共享堆拿牌回手"这一类效果的归属修正。
/// </summary>
/// <remarks>
/// 症状：捏奥之怒（NeowsFury）/挖掘（Dredge）/全息影像（Hologram）等从弃牌堆选牌回手时，
/// 弃牌堆确实少了几张，但牌<b>没有进自己的手牌</b>。
/// 原因：这类效果的目标手牌是本体<b>用卡牌自己的 owner 推出来的</b> ——
/// CardPileCmd.Add(cards, PileType.Hand, …) 内部走的是
/// <c>PileType.Hand.GetPile(cards.First().Owner)</c>。
/// 而共享牌堆里的牌保留的是<b>自然归属</b>，它不等于"此刻正在操作这批牌的人"：
/// 回声打这类牌时，<c>cards.First().Owner</c> 可能是锚点（或别的成员）→ 目标被推成<b>别人的手牌</b>：
/// 牌从回声的屏幕上消失，却跑进另一个人手里去了。
/// （反过来若进了自己的手但 owner 不是自己，界面又会因为 LocalContext.IsMe(card.Owner)
/// 不为真而不给这张牌建手牌节点 —— 同样是"看不见"。）
/// 解法：在搬运之前，把这批"从共享堆回手"的牌先改成<b>当前正在结算效果的那名玩家</b>，
/// 本体随后用 owner 推出的目标手牌就是他自己那口。判定"正在结算效果的人"用本体自己的
/// CombatManager.BeginCardOrPotionEffect / EndCardOrPotionEffect 深度计数
/// （它就在 finally 里配对，比我们自己去挂"开始出牌/结束出牌"稳），
/// 再要求两名配对玩家中<b>只有一个人</b>在执行效果 —— 嵌套效果（两个都在执行）时不猜，保持原版行为。
/// 只改 <c>PileType.Hand</c> 这一类目标：其余堆（抽/弃/消耗/出牌/卡组）在配对里本来就是同一份，
/// 用谁当 owner 推出来的都是同一个堆，没必要碰。
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

    /// <summary>这批牌要进"当前效果执行者的手牌"→ 先把它们改成那个人，本体随后自己会推出正确的目标堆。</summary>
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

    /// <summary>目标堆是明确给出的手牌堆时，把牌改成该手牌的主人。</summary>
    /// <remarks>
    /// 和单张路径的 CardOwnershipImpl.NormalizeForHand 同一个规则，但<b>只对"不在任何手牌里"的牌动手</b>：
    /// 正在某人手牌里的牌如果被改写 owner，那边的 NPlayerHand 会认不出它（幽灵卡）。
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

/// <summary>记录"最近一次从战斗牌堆里选牌的是谁、选的是哪个堆"。</summary>
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
        if (_player is null || _pile is null || !TogetherPair.IsMember(_player))
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
