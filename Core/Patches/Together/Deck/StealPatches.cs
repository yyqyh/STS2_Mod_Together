using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using Together.Core.Combat;
using Together.Core.Utils;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using Together.Core.Content;

namespace Together.Core.Patches.Deck;

/// <summary>草蜢偷多张牌：把"被偷的牌"从单张改成列表，死亡时逐张还回去。</summary>
/// <remarks>
/// 本体 <c>SwipePower</c> 只有一个 <c>StolenCard</c> 槽：偷第二张会把第一张覆盖掉，
/// 于是显示只看得到最后一张，<c>BeforeDeath</c> 也只还最后一张（其余永久丢失）。
/// 这里用旁表记住每次 <c>Steal</c> 的 (牌, 被偷方)，并接管 <c>BeforeDeath</c>：
/// 对每一张 <c>DeckVersion != null</c> 的牌做"加回卡组 + 取回奖励 + 标记已归还"，
/// 卡不掉的那张（战斗中生成的牌没有 DeckVersion）只记日志。
/// </remarks>
internal static class StolenCards
{
    private static readonly ConditionalWeakTable<SwipePower, List<(CardModel Card, Player? Victim)>> Table = new();

    public static void Add(SwipePower power, CardModel card, Player? victim)
    {
        lock (Table)
        {
            var list = Table.GetOrCreateValue(power);
            list.Add((card, victim));
        }
    }

    public static List<(CardModel Card, Player? Victim)> Get(SwipePower power)
    {
        return Table.TryGetValue(power, out var list) ? list : [];
    }
}

[HarmonyPatch(typeof(SwipePower), nameof(SwipePower.Steal))]
internal static class StealRecordPatch
{
    [HarmonyPostfix]
    private static void Postfix(SwipePower __instance, CardModel card)
    {
        if (!TogetherPair.IsActive || card is null)
        {
            return;
        }

        var victim = __instance.Target?.Player;
        StolenCards.Add(__instance, card, victim);
        CappedLog.Info(
            "steal.list",
            $"记下被偷的牌：{card.Id.Entry}（被偷方 netId{victim?.NetId}）；本次累计 {StolenCards.Get(__instance).Count} 张");
    }
}

/// <summary>草蜢死亡时，把记下的牌**逐张**还回去（本体只还最后一张）。</summary>
[HarmonyPatch(typeof(SwipePower), nameof(SwipePower.BeforeDeath))]
internal static class StealReturnAllPatch
{
    /// <summary>【已退回 A】归还<b>完全交回本体</b>。</summary>
    /// <remarks>
    /// 我们"逐张归还"试过两种入口（<c>RunState.AddCard(card, victim)</c> → 按人登记、与共享卡组对不上；
    /// 改成 <c>CardPileCmd.Add(card, Deck)</c> → 仍然崩），说明问题不止在入口，所以先把归还整段退回本体：
    /// <b>不碰任何记账，只保证不崩</b>。代价是本体只还会它自己记住的那一张（多张里的其余暂时不还）。
    /// </remarks>
    [HarmonyPrefix]
    private static bool Prefix(SwipePower __instance, Creature target)
    {
        if (TogetherPair.IsActive && ReferenceEquals(__instance.Owner, target))
        {
            CappedLog.Info(
                "steal.return",
                $"归还交回本体（本体只还它记住的那一张）；我们这次记下了 {StolenCards.Get(__instance).Count} 张");
        }

        return true;
    }
}

/// <summary>草蜢这次偷牌行动里"每一遍是替谁偷"的队列（两端算出来一致）。</summary>
internal static class StealSession
{
    private static int _index;

    private static bool _active;

    /// <summary>一次偷牌行动开始（草蜢的 ThieveryMove）：重置轮次。</summary>
    public static void Begin()
    {
        _active = true;
        _index = 0;
    }

    /// <summary>第 N 次偷牌该记在谁头上：**按偷牌顺序轮流分配成员**（1→锚点，2→回声…）。</summary>
    /// <remarks>
    /// 不用"取候选时记一笔"那种信号：本体某位成员如果抽/弃牌堆里没牌就会跳过那一遍，
    /// 队列会错位。按"第几次真的偷到了牌"轮流分配，和 `targets` 的顺序天然一致，两端也算得一样。
    /// </remarks>
    public static Player? NextVictim()
    {
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
        if (_index >= members.Count)
        {
            _active = false;   // 一成员一张，偷够就收摊
        }

        return victim;
    }
}

/// <summary>草蜢偷牌（<c>ThieveryMove</c>）：把 <c>targets</c> 补成"共生体全部成员的 creature"。</summary>
/// <remarks>
/// 本体是 <c>foreach (target in targets)</c> 每人偷一张；共享身体下 targets 通常只有锚点，
/// 所以永远只偷一张、也只记锚点。补成全部成员后：每个成员各偷一张（来源仍是共享抽/弃牌堆，蓝卡优先照旧），
/// 被偷方由下面的"受害者队列"显式指定（不看 <c>card.Owner</c>，避免被归属归一影响）。
/// </remarks>
[HarmonyPatch]
internal static class ThieveryMoveTargetsPatch
{
    /// <summary>草蜢本体 + 所有**自己声明了** <c>ThieveryMove</c> 的派生类型（变体怪会覆写它，只挂基类的话虚分派
    /// 根本走不到我们的补丁 —— 实测"没偷 P2"就是这个原因）。</summary>
    private static IEnumerable<MethodBase> TargetMethods()
    {
        var baseType = typeof(ThievingHopper);

        foreach (var type in baseType.Assembly.GetTypes())
        {
            if (!baseType.IsAssignableFrom(type))
            {
                continue;
            }

            var method = type.GetMethod(
                "ThieveryMove",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

            if (method is not null)
            {
                yield return method;
            }
        }
    }

    [HarmonyPrefix]
    private static void Prefix(ref IReadOnlyList<Creature> targets)
    {
        if (!TogetherPair.IsActive)
        {
            return;
        }

        try
        {
            var members = TogetherPair.Members().ToList();
            var ids = members.Select(m => m.NetId).ToList();
            // 名单来源：Arm 广播的"本局成员"（生命周期与配对一致）；还没收到过就退回本地名单。
            var synced = RunMembersSync.Remote ?? ids.ToArray();

            // 三个条件都满足才敢多偷一张：
            //   ① 这是**联机局**（单人局里 TogetherPair 可能还留着上一局的配对 —— 按设计非共生体局不清配对）；
            //   ② 配对成员**都在本局 RunState.Players 里**（残留配对里的 Player 是上一局的对象，会带着旧 creature）；
            //   ③ 两端成员名单一致（不一致多偷就会差一张牌 → checksum 分叉、P2 被踢）。
            // 少了 ①② 就会把"上一局的 creature"塞进 targets，本体 ThieveryMove 的 foreach 直接 NRE
            //（实测 2026-09-23 11:28 单人局草蜢偷牌崩溃就是这条）。
            var multiplayer = RunManager.Instance?.NetService is { } net && net.Type.IsMultiplayer();
            // 本局 RunState 从"这次战斗里的 creature"拿（比 RunManager/配对更可靠，配对可能是上一局的残留）。
            var runState = targets.Count > 0 ? targets[0].CombatState?.RunState : null;
            var everyoneInRun = runState is not null && members.All(m => runState.Players.Contains(m));
            var consistent = multiplayer && everyoneInRun
                && ids.Count >= 2 && synced.Length == ids.Count && ids.All(synced.Contains);
            CappedLog.Info(
                "steal.victim",
                $"成员核对：联机={multiplayer} 成员都在本局={everyoneInRun}"
                + $" 本地=[{string.Join(",", ids)}] 同步=[{string.Join(",", synced)}]"
                + $" → {(consistent ? "扩 targets（成员各偷一张）" : "退回本体（只偷一张）")}");

            if (!consistent)
            {
                return;
            }

            StealSession.Begin();

            var list = targets.ToList();
            foreach (var member in members)
            {
                if (member.Creature is { } creature && !list.Contains(creature))
                {
                    list.Add(creature);
                }
            }

            targets = list;
        }
        catch (Exception ex)
        {
            CappedLog.Info("steal.victim", $"扩展 targets 失败（忽略）：{ex.GetType().Name}: {ex.Message}");
        }
    }
}

/// <summary>把这次偷牌的"被偷方"改判成队列里那一位成员（不再看牌自己的 owner）。</summary>
[HarmonyPatch(typeof(SwipePower), nameof(SwipePower.Steal))]
internal static class StealVictimApplyPatch
{
    [HarmonyPostfix]
    private static void Postfix(SwipePower __instance, CardModel card)
    {
        try
        {
            if (!TogetherPair.IsActive)
            {
                return;
            }

            if (StealSession.NextVictim()?.Creature is { } creature)
            {
                __instance.Target = creature;
                CappedLog.Info("steal.victim", $"被偷方改判：{card?.Id.Entry} → netId{creature.Player?.NetId}");
            }

            // 显示：本体只在“这张牌的 owner 是本机玩家”时把卡面挂到草蜢身上；共享卡组里 owner 已被归一成锚点，
            // 于是只有锚点窗口能看到被偷的牌。这里在【另一端】补一张同样的卡面（不重复本体那次）。
            if (card is not null && !LocalContext.IsMine(card)
                && NCombatRoom.Instance?.GetCreatureNode(__instance.Owner) is { } hopperNode)
            {
                if (hopperNode.GetSpecialNode<Marker2D>("%StolenCardPos") is { } marker
                    && NCard.Create(card) is { } nCard)
                {
                    marker.AddChildSafely(nCard);
                    nCard.Position += nCard.Size * 0.5f;
                    nCard.UpdateVisuals(PileType.Deck, CardPreviewMode.Normal);
                    CappedLog.Info("steal.display", $"补画被偷的牌：{card.Id.Entry}（本机不是它的 owner）");
                }
            }
        }
        catch (Exception ex)
        {
            CappedLog.Info("steal.victim", $"被偷方改判失败（忽略）：{ex.GetType().Name}: {ex.Message}");
        }
    }
}
