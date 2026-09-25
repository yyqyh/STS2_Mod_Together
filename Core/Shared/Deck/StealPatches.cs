using System.Reflection;

using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
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

/// <summary>草蜢这次偷牌行动里"每一张记在谁头上"的映射（两端算出来一致）。</summary>
internal static class StealSession
{
    /// <summary>这一次行动的身份：用"本次怪招"（<c>MonsterMoveScope.Scope</c> 的对象引用）当 key。</summary>
    /// <remarks>
    /// <c>ThieveryMove</c> 是 <c>async</c> 方法，Harmony 的 Prefix 在状态机上可能被调到不止一次；
    /// 同 token 直接忽略，才不会把已经分配出去的轮次重置。换一个招式 token 自然变，也就自然重置。
    /// </remarks>
    private static object? _token;

    private static int _index;

    private static bool _active;

    /// <summary>本次行动里"哪张牌算谁被偷"（同一张牌问多少次都给同一个答案）。</summary>
    private static readonly Dictionary<CardModel, Player> Assigned = new(ReferenceEqualityComparer.Instance);

    /// <summary>本次行动里"偷牌的那只怪"的身体 —— 卡面就挂在它身上。</summary>
    /// <remarks>
    /// 不能等到 <c>SwipePower.Owner</c>：草蜢是"先 <c>await swipe.Steal(item)</c>、再 <c>PowerCmd.Apply(swipe)</c>"，
    /// 我们挂在 <c>Steal</c> 上的收尾跑在前面，那时这份 SwipePower 还没挂到怪身上（<c>Owner</c> 是 null）。
    /// </remarks>
    public static Creature? Anchor { get; private set; }

    /// <summary>本机已经补画过的牌（同一个方法可能被调到不止一次）。</summary>
    private static readonly HashSet<CardModel> Drawn = new(ReferenceEqualityComparer.Instance);

    /// <summary>一次偷牌行动开始（草蜢的 ThieveryMove）：token 变了才重置轮次。</summary>
    public static void Begin(object? token, Creature? anchor)
    {
        if (token is not null && ReferenceEquals(_token, token))
        {
            return;
        }

        _token = token;
        Anchor = anchor;
        _active = true;
        _index = 0;
        Assigned.Clear();
        Drawn.Clear();
    }

    /// <summary>这张牌该记在谁头上：**按偷牌顺序轮流分配成员**（1→锚点，2→回声…）。</summary>
    /// <remarks>
    /// 不用"取候选时记一笔"那种信号：本体某位成员如果抽/弃牌堆里没牌就会跳过那一遍，
    /// 队列会错位。按"第几张真的偷到了牌"轮流分配，和 `targets` 的顺序天然一致，两端也算得一样。
    /// （本体 `targets` = `combatState.PlayerCreatures`，锚点在成员里排在前面，两条顺序一致。）
    /// 同一个 <paramref name="card" /> 重复问（Harmony 对 async 方法可能跑多次补丁）只消耗一个轮次。
    /// </remarks>
    public static Player? VictimFor(CardModel card)
    {
        if (Assigned.TryGetValue(card, out var cached))
        {
            return cached;
        }

        if (!_active)
        {
            return null;
        }

        var members = TogetherPair.Members().ToList();
        if (members.Count == 0)
        {
            return null;
        }

        var victim = members[_index % members.Count];
        _index++;
        Assigned[card] = victim;

        if (_index >= members.Count)
        {
            _active = false;   // 一成员一张，偷够就收摊
        }

        return victim;
    }

    /// <summary>本机是不是已经给这张牌补过卡面。</summary>
    public static bool AlreadyDrawn(CardModel card)
    {
        return Drawn.Contains(card);
    }

    /// <summary>记下"这张牌的卡面已经补画过"（真正画成功了再调）。</summary>
    public static void MarkDrawn(CardModel card)
    {
        Drawn.Add(card);
    }

}

/// <summary>草蜢偷牌（<c>ThieveryMove</c>）：开一次"被偷方轮转"会话。</summary>
/// <remarks>
/// <b>这里不再改 <c>targets</c>。</b>本体 <c>MonsterModel.PerformMove</c> 传进来的就是
/// <c>combatState.PlayerCreatures</c>（全场玩家的 creature），共享身体下两个人本来就在里面，
/// 本体那句 <c>foreach (target in targets)</c> 已经"每人各偷一张"（来源是同一口共享抽/弃牌堆，蓝卡优先照旧）。
/// 早期版本会在这里把 targets 补成"组内全部成员"——那是"怪物目标重定向"还在的时候；那条已经撤销，
/// 补全现在是空操作，所以删掉。真正还需要补的是<b>归还对象</b>：共享卡组的 owner 被归一成锚点之后，
/// <c>SwipePower.Steal</c> 里那句 <c>Target = card.Owner.Creature</c> 每次都指向锚点 → 两张牌都还给 P1。
/// 于是这里只负责"标记一次偷牌行动开始了"，由下面的受害者队列把 Target 改判成对应成员。
/// </remarks>
[HarmonyPatch]
internal static class ThieveryMoveTargetsPatch
{
    /// <summary>草蜢本体 + 所有**自己声明了** <c>ThieveryMove</c> 的派生类型（变体怪会覆写它，只挂基类的话虚分派
    /// 根本走不到我们的补丁 —— 实测"没偷 P2"就是这个原因）。</summary>
    private static IEnumerable<MethodBase> TargetMethods()
    {
        var baseType = typeof(ThievingHopper);
        var signature = new[] { typeof(IReadOnlyList<Creature>) };

        foreach (var type in baseType.Assembly.GetTypes())
        {
            if (!baseType.IsAssignableFrom(type))
            {
                continue;
            }

            MethodInfo? method;
            try
            {
                // 按签名取：派生怪若声明了同名不同签名的 ThieveryMove，这里拿到 null → 跳过它，
                // 而不是整个补丁类抛 AmbiguousMatchException（那会让启动行出现 failed=1，
                // 后面所有联机结论都不可信）。
                method = type.GetMethod(
                    "ThieveryMove",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                    binder: null,
                    types: signature,
                    modifiers: null);
            }
            catch (Exception ex)
            {
                CappedLog.Info("steal.victim", $"跳过 {type.Name}.ThieveryMove（反射失败）：{ex.GetType().Name}: {ex.Message}");
                continue;
            }

            if (method is not null)
            {
                yield return method;
            }
        }
    }

    /// <summary>行动开始：核对名单后开一次轮转会话。<c>__0</c> 是位置绑定（派生怪可能给这个参数改过名）。</summary>
    [HarmonyPrefix]
    private static void Prefix(ThievingHopper __instance, IReadOnlyList<Creature> __0)
    {
        if (!TogetherPair.IsActive)
        {
            return;
        }

        try
        {
            var members = TogetherPair.Members().ToList();
            var ids = members.Select(m => m.NetId).ToList();

            // 四个条件都满足才敢开轮转（改被偷方）：
            //   ① 这是**联机局**（单人局里 TogetherPair 可能还留着上一局的配对 —— 按设计非共生体局不清配对）；
            //   ② 配对成员**都在本局 RunState.Players 里**（残留配对里的 Player 是上一局的对象，会带着旧 creature）；
            //   ③ 两端成员名单一致（不一致就会把牌还给错的人 → 存档里牌去向分叉）；
            //   ④ 至少两个人。
            // 少了 ①② 会把"上一局的 Player"当成被偷方，改判出来的 Target 指向作废对象。
            var multiplayer = RunManager.Instance?.NetService is { } net && net.Type.IsMultiplayer();
            // 本局 RunState 从"这次偷牌的目标 creature"拿（比 RunManager/配对更可靠，配对可能是上一局的残留）。
            var runState = __0.Count > 0 ? __0[0].CombatState?.RunState : null;
            // 名单来源：本局 RunSavedData 槽位里的名单（开局快照数据，两端必然同一份）；
            // 拿不到（理论上不该发生）就退回本地配对成员，此时 ③ 的一致性检查会失败 → 不开轮转，保持原版行为。
            var synced = runState is not null && CoopLobbyData.RosterOf(runState) is { Length: > 0 } roster
                ? roster
                : ids.ToArray();
            var everyoneInRun = runState is not null && members.All(m => runState.Players.Contains(m));
            var consistent = multiplayer && everyoneInRun
                && ids.Count >= 2 && synced.Length == ids.Count && ids.All(synced.Contains);
            CappedLog.Info(
                "steal.victim",
                $"成员核对：联机={multiplayer} 成员都在本局={everyoneInRun}"
                + $" 本地=[{string.Join(",", ids)}] 同步=[{string.Join(",", synced)}]"
                + $" → {(consistent ? "开轮转（被偷方按序分给成员）" : "退回本体（不轮转）")}");

            if (!consistent)
            {
                return;
            }

            // 用"本次怪招"当身份：同一个招式里 Prefix 被调到多次也不会重置轮次。
            // 顺手把怪的身体记下来 —— 收尾画卡面时 SwipePower 还没挂到它身上，拿不到 Owner。
            StealSession.Begin(MonsterMoveScope.Active, __instance.Creature);
        }
        catch (Exception ex)
        {
            CappedLog.Info("steal.victim", $"开轮转失败（忽略）：{ex.GetType().Name}: {ex.Message}");
        }
    }
}

/// <summary>卡面归属修正：让本机只看到"记在自己头上"的那张被偷的牌。</summary>
/// <remarks>
/// <para>
/// 本体的画法是 <c>if (LocalContext.IsMine(item)) 画</c>，判据是<b>这张牌的 owner</b>；而共享卡组的 owner 被归一在
/// 某一个人身上（重建后是锚点，实战里可能是任何一个成员），于是：
/// owner 那一端会把<b>所有</b>被偷的牌都画出来，另一端一张都看不到。
/// </para>
/// <para>
/// <b>这里只在草蜢自己的调用链上动手，不碰任何本体公共路径</b>：
/// 按 <see cref="StealSession.VictimFor" />（"第几张偷到的牌记在谁头上"）判断本机该不该看见这张 ——
/// 该看见而本体没画（本机不是 owner）就补一张；不该看见而本体画了（本机是 owner）就把那张卡面节点摘掉。
/// 同名牌是不同对象，按 <c>NCard.Model</c> 引用比对，不会误删另一张。
/// </para>
/// <para>
/// 挂点还是 <c>SwipePower.Steal</c>（草蜢专属）：它跑在"本体已经画完、正要 <c>Apply</c> 这份 SwipePower"之间，
/// 卡面节点已经存在，够我们补 / 删。<b>不能用补丁参数</b> —— 这是个 async 方法，按名字绑参数并不可靠，
/// 所以从实例读 <c>StolenCard</c>、从会话读怪的身体。
/// </para>
/// </remarks>
[HarmonyPatch(typeof(SwipePower), nameof(SwipePower.Steal))]
internal static class StealCardFacePatch
{
    [HarmonyPostfix]
    private static void Postfix(SwipePower __instance)
    {
        try
        {
            if (!TogetherPair.IsActive)
            {
                return;
            }

            // async 方法：按名字绑参数拿不到 card，从实例读（Steal 第一句就赋值）。
            if (__instance.StolenCard is not { } card)
            {
                return;
            }

            // 只处理"来自共享牌堆"的牌：它们的 owner 一定是组内成员。
            // 混合局里普通玩家（没加入合作模式）自己的牌 owner 就是自己，本体画得本来就对，不碰。
            if (card.Owner is not { } owner || !TogetherPair.IsMember(owner))
            {
                return;
            }

            if (StealSession.VictimFor(card) is not { } victim)
            {
                return;
            }

            var marker = NCombatRoom.Instance?.GetCreatureNode(StealSession.Anchor)
                ?.GetSpecialNode<Marker2D>("%StolenCardPos");
            if (marker is null)
            {
                return;
            }

            if (victim.NetId == LocalContext.NetId)
            {
                DrawMine(card, marker);
            }
            else
            {
                EraseForeign(card, marker, victim);
            }
        }
        catch (Exception ex)
        {
            CappedLog.Info("steal.victim", $"卡面归属修正失败（忽略）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>这张记在本机头上：本体没画（本机不是 owner）就补一张。</summary>
    private static void DrawMine(CardModel card, Marker2D marker)
    {
        if (LocalContext.IsMine(card) || StealSession.AlreadyDrawn(card))
        {
            return;   // 本体自己画过了 / 我们已经补过
        }

        if (NCard.Create(card) is not { } nCard)
        {
            return;
        }

        StealSession.MarkDrawn(card);
        marker.AddChildSafely(nCard);
        nCard.Position += nCard.Size * 0.5f;
        nCard.UpdateVisuals(PileType.Deck, CardPreviewMode.Normal);
        CappedLog.Info("steal.display", $"补画被偷的牌：{card.Id.Entry}（本机不是它的 owner，但这次记在本机头上）");
    }

    /// <summary>这张记在别人头上：本体因为是本机 owner 把别人的牌也画了，摘掉它。</summary>
    private static void EraseForeign(CardModel card, Marker2D marker, Player victim)
    {
        foreach (var child in marker.GetChildren())
        {
            if (child is not NCard drawn || !ReferenceEquals(drawn.Model, card))
            {
                continue;
            }

            CappedLog.Info(
                "steal.display",
                $"摘掉不该显示的卡面：{card.Id.Entry}（本体按 owner 画的，但这次记在 netId{victim.NetId} 头上）");
            drawn.QueueFree();
        }
    }
}

/// <summary>归还前的兜底：怪物死亡时再确认一次"这张牌该还给谁"。</summary>
/// <remarks>
/// 本体归还走的是 <c>SwipePower.BeforeDeath</c> → <c>base.Target.Player</c>。正常情况下
/// <c>SwipePower.Steal</c> 里那句 <c>Target = card.Owner.Creature</c> 会把牌判给"共享卡组的 owner"（= 某一个人），
/// 所以两张牌都会还给同一个人。这个挂点是<b>非 async</b> 的，一定会在归还前跑到，
/// 用同一份轮转映射把 <c>Target</c> 钉成真正的那位。<see cref="StealCardFacePatch" /> 只管卡面，不管归还。
/// <see cref="StealSession.VictimFor" /> 对同一张牌是幂等的，不会重复消耗轮次。
/// </remarks>
[HarmonyPatch(typeof(SwipePower), nameof(SwipePower.BeforeDeath))]
internal static class StealReturnTargetPatch
{
    [HarmonyPrefix]
    private static void Prefix(SwipePower __instance)
    {
        try
        {
            if (!TogetherPair.IsActive || __instance.StolenCard is not { } card)
            {
                return;
            }

            if (StealSession.VictimFor(card)?.Creature is { } creature)
            {
                __instance.Target = creature;
            }
        }
        catch (Exception ex)
        {
            CappedLog.Info("steal.victim", $"归还对象兜底失败（忽略）：{ex.GetType().Name}: {ex.Message}");
        }
    }
}
