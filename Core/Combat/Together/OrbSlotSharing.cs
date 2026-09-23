using Godot;
using System.Reflection;

using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Orbs;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Orbs;
using MegaCrit.Sts2.Core.Nodes.Rooms;

using Together.Core.Utils;

namespace Together.Core.Combat;

/// <summary>
/// 球位（orb，故障机器人的那套）共享：两名成员用<b>同一口</b> OrbQueue，
/// 容量 = 各成员基础球位之和（两个故障机器人 = 3 + 3 = 6 格）。
/// </summary>
/// <remarks>
/// <para>
/// 本体球位是逐玩家的：<c>PlayerCombatState.OrbQueue</c>，容量初值来自 <c>Player.BaseOrbSlotCount</c>
/// （故障机器人 3；DLC/其他 mod 角色也一样走这个字段）。共享身体下两个人各有一套球，抽/弹/被动各算各的。
/// </para>
/// <para>
/// 做法和"共享卡组"同一套：<b>把回声那份队列字段直接换成锚点那一份</b>。本体的
/// <c>OrbCmd.AddOrb / Evoke / AddSlots / RemoveSlots</c> 全都是走 <c>player.PlayerCombatState.OrbQueue</c>，
/// 换完两端天然操作同一口队列；界面（<c>NOrbManager</c>）也是现读 <c>Player.PlayerCombatState.OrbQueue</c>，
/// 所以两边的球位显示都会变成这条共享队列。之后再把容量补成"各成员基础球位之和"。
/// </para>
/// <para>
/// <b>唯一的坑</b>：球位有两个"每回合触发一次"的钩子 —— <c>AfterTurnStart</c>（回合开始：闪电/等离子等被动）
/// 和 <c>BeforeTurnEnd</c>（回合结束：冰球给格挡等），本体是<b>逐玩家</b>调用的
/// （<c>CombatManager.StartTurn</c> 里 foreach playersStartingTurn、<c>DoTurnEnd</c> 里 per player）。
/// 队列共享之后回响那次会撞在同一口队列上 → 全部双倍触发。
/// 所以这两条各加一个前缀：<b>只让队列主人（锚点）那一次跑</b>，回响那次直接跳过（异步方法还得自己还 Task）。
/// 判定用本体现成的 <c>HookPlayerChoiceContext.Owner</c>，两端算出来一致。
/// </para>
/// <para>
/// 上限：本体每口队列写死 10 格（<c>OrbQueue.maxCapacity</c> + <c>OrbCmd.AddSlots</c> 里的夹取），
/// 共享局改成 <b>10 × 有球位的成员数</b>（两个故障机器人 = 20），见 <see cref="CapacityCap" />。
/// </para>
/// <para>
/// <b>界面</b>：本体的球位界面（<c>NOrbManager</c>）不是每帧读队列，而是<b>按节点增量画</b>的
/// （<c>AddOrbAnim</c> 只给"这次抽球的那个人"的节点加球）。所以队列共享之后，回声那侧窗口不会自己
/// 长出球来 —— 这里在队列一变时于本帧末按共享队列重建每个成员的球位节点（已经一致的不动，保住本体动画）。
/// </para>
/// </remarks>
internal static class OrbSlotSharing
{
    /// <summary>节点上有没有"这颗球"的图像（<c>EvokeOrbAnim</c> 就是按这个找的）。</summary>
    internal static bool HasOrbNode(NOrbManager manager, OrbModel orb)
    {
        return ManagerOrbsField(manager).Any(node => ReferenceEquals(node.Model, orb));
    }

    /// <summary><c>PlayerCombatState.OrbQueue</c> 是只读自动属性，直接换它的 backing field。</summary>
    private static readonly AccessTools.FieldRef<PlayerCombatState, OrbQueue> QueueField =
        AccessTools.FieldRefAccess<PlayerCombatState, OrbQueue>("<OrbQueue>k__BackingField");

    private static readonly AccessTools.FieldRef<OrbQueue, List<OrbModel>> OrbsField =
        AccessTools.FieldRefAccess<OrbQueue, List<OrbModel>>("_orbs");

    private static readonly AccessTools.FieldRef<NOrbManager, List<NOrb>> ManagerOrbsField =
        AccessTools.FieldRefAccess<NOrbManager, List<NOrb>>("_orbs");

    private static readonly AccessTools.FieldRef<NOrbManager, Control> ManagerContainerField =
        AccessTools.FieldRefAccess<NOrbManager, Control>("_orbContainer");

    /// <summary>已经补过容量的那口队列（每场战斗会新建队列，所以按实例记）。</summary>
    private static OrbQueue? _linked;

    private static bool _syncScheduled;

    /// <summary>镜像动画时的重入守卫：镜像调用自己又会被同一个补丁看到，不加守卫会来回翻倍。</summary>
    private static bool _mirroring;

    /// <summary>球位总上限：本体每份 10 格 × 本局"有球位"的成员数（两个故障机器人 = 20）。</summary>
    public static int CapacityCap
    {
        get
        {
            var withOrbs = 0;
            foreach (var member in TogetherPair.Members())
            {
                if (member.BaseOrbSlotCount > 0)
                {
                    withOrbs++;
                }
            }

            return OrbQueue.maxCapacity * Math.Max(1, withOrbs);
        }
    }

    /// <summary>这口队列是不是"共生体共享队列"。</summary>
    public static bool IsShared(OrbQueue? queue)
    {
        return queue is not null && TogetherPair.IsActive && ReferenceEquals(queue, Master());
    }

    /// <summary>共享队列的主人（锚点）——也就是"该由谁触发球位回合钩子"的那个人。</summary>
    public static Player? OwnerOf(OrbQueue? queue)
    {
        return IsShared(queue) ? TogetherPair.Anchor : null;
    }

    /// <summary>
    /// 把成员们的球位并到同一口队列上（每次 <c>PlayerCombatState</c> 建好都会调一次，幂等）。
    /// </summary>
    internal static void Link(Player? player)
    {
        try
        {
            if (!TogetherPair.IsActive || !TogetherPair.IsMember(player))
            {
                return;
            }

            // 锚点的战斗状态还没建好时先不动：等它建好那次调用会把所有人一起链接（同牌堆的收尾方式）。
            if (Master() is not { } shared)
            {
                return;
            }

            // 只有"第一次看到这口队列"才搬球 + 补容量；之后的收口只把字段重新对齐。
            // 很关键：下一场战斗回声那份 PlayerCombatState 可能被本体沿用，那时它仍指着上一场的队列
            // （球还在），收口必须把那份旧队列直接丢掉，而不是把球搬过来。
            var firstPass = !ReferenceEquals(_linked, shared);
            var taken = 0;
            foreach (var member in TogetherPair.Members())
            {
                if (member.PlayerCombatState is not { } state)
                {
                    continue;
                }

                var own = QueueField(state);
                if (ReferenceEquals(own, shared))
                {
                    continue;
                }

                // 注意：**不搬球**。接管时机已经挪到 PopulateCombatState（在遗物/卡牌给球之前），
                // 所以"抢在我们之前加球"的情况不存在；而下一场战斗回声那份旧队列里留的正是上一场的球 ——
                // 搬过来就变成"战斗结束后球保留到下一次战斗"（实测 22:04 log 第 1986 行）。
                QueueField(state) = shared;
                taken++;
            }

            if (!firstPass)
            {
                if (taken > 0)
                {
                    CappedLog.Info("orb.relink", $"共享球位：收口 {taken} 份队列（丢掉回声上一场遗留的队列）");
                }

                return;
            }

            _linked = shared;

            var total = 0;
            var members = 0;
            foreach (var member in TogetherPair.Members())
            {
                total += Math.Max(0, member.BaseOrbSlotCount);
                members++;
            }

            // 只补"差多少"，不重复加；本体上限 10。
            var extra = Math.Min(CapacityCap, total) - shared.Capacity;
            if (extra > 0)
            {
                shared.AddCapacity(extra);
            }

            Log.Info(
                $"[together] 共享球位：{members} 人并到同一口队列（基础球位合计 {total} → 容量 {shared.Capacity}"
                + $"，上限 {CapacityCap}；本次接管 {taken} 份队列）");

            ScheduleReconcile();
        }
        catch (Exception ex)
        {
            Log.Warn($"[together] 共享球位链接失败（忽略）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static OrbQueue? Master()
    {
        return TogetherPair.Anchor?.PlayerCombatState is { } state ? QueueField(state) : null;
    }

    /// <summary>把 <paramref name="from" /> 里的球搬进共享队列（只搬数据；界面由 <see cref="ScheduleVisualSync" /> 重建）。</summary>
    private static void MoveOrbs(OrbQueue from, OrbQueue to, Player member)
    {
        var orbs = OrbsField(from);
        if (orbs.Count == 0)
        {
            return;
        }

        var moved = 0;
        foreach (var orb in orbs.ToList())
        {
            if (orbs.Remove(orb))
            {
                OrbsField(to).Add(orb);
                moved++;
            }
        }

        Log.Info($"[together] 共享球位：把 netId{member.NetId} 自己那口队列里的 {moved} 颗球并进共享队列");
    }

    /// <summary>本帧末对齐一次"节点上少画的球"（换字段时搬过来的球、以及战斗开始时两边的起点差）。</summary>
    private static void ScheduleReconcile()
    {
        if (_syncScheduled || NCombatRoom.Instance is not { } room || !GodotObject.IsInstanceValid(room))
        {
            return;
        }

        _syncScheduled = true;
        Callable.From(() =>
        {
            _syncScheduled = false;
            ReconcileOrbs();
        }).CallDeferred();
    }

    private static void ReconcileOrbs()
    {
        if (Master() is not { } shared || !IsShared(shared))
        {
            return;
        }

        foreach (var member in TogetherPair.Members())
        {
            var manager = NCombatRoom.Instance?.GetCreatureNode(member.Creature)?.OrbManager;
            if (manager is null || !GodotObject.IsInstanceValid(manager))
            {
                continue;
            }

            try
            {
                // 节点上已画的球比共享队列少 → 用本体的"补一颗球"动画补上（多了的情况交给动画镜像去减）。
                var drawn = ManagerOrbsField(manager).Count(node => node.Model is not null);
                for (var i = drawn; i < shared.Orbs.Count; i++)
                {
                    manager.AddOrbAnim();
                }
            }
            catch (Exception ex)
            {
                CappedLog.Info("orb.sync", $"球位界面补齐失败（忽略）：{ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 把这次球位动画照搬到其他成员的节点上。
    /// </summary>
    /// <remarks>
    /// 本体的球位界面是按节点增量画的，只画"这次操作的那个人"：不镜像的话，回声那侧既看不到球，
    /// 而且它自己激发时本体会在 <c>NOrbManager.EvokeOrbAnim</c> 里找不到对应节点而抛
    /// <c>Sequence contains no matching element</c>（实测 18:44 log 的 DUALCAST 崩就是这条）。
    /// </remarks>
    internal static void MirrorAnim(NOrbManager source, string method, object[] args)
    {
        try
        {
            if (_mirroring || !TogetherPair.IsActive || NCombatRoom.Instance is not { } room)
            {
                return;
            }

            if (source.GetParent() is not NCreature { Entity.Player: { } from } || !TogetherPair.IsMember(from))
            {
                return;
            }

            if (!IsShared(from.PlayerCombatState?.OrbQueue))
            {
                return;
            }

            _mirroring = true;
            try
            {
                foreach (var member in TogetherPair.Members())
                {
                    if (ReferenceEquals(member, from))
                    {
                        continue;
                    }

                    var manager = room.GetCreatureNode(member.Creature)?.OrbManager;
                    if (manager is null || !GodotObject.IsInstanceValid(manager) || ReferenceEquals(manager, source))
                    {
                        continue;
                    }

                    try
                    {
                        switch (method)
                        {
                            case nameof(NOrbManager.AddOrbAnim):
                                manager.AddOrbAnim();
                                break;

                            case nameof(NOrbManager.EvokeOrbAnim) when args is [OrbModel orb]:
                                manager.EvokeOrbAnim(orb);
                                break;

                            case nameof(NOrbManager.ClearOrbs):
                                manager.ClearOrbs();
                                break;

                            case nameof(NOrbManager.AddSlotAnim) when args is [int add]:
                                manager.AddSlotAnim(add);
                                break;

                            case nameof(NOrbManager.RemoveSlotAnim) when args is [int remove]:
                                manager.RemoveSlotAnim(remove);
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        CappedLog.Info("orb.sync", $"球位动画镜像失败（忽略）：{ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
            finally
            {
                _mirroring = false;
            }
        }
        catch (Exception)
        {
            // 镜像只影响观感，绝不往外抛。
        }
    }
}

/// <summary>
/// 每次玩家回合开始都重新收口一次球位共享。
/// </summary>
/// <remarks>
/// 下一场战斗时回声那份 <c>PlayerCombatState</c> 可能被本体<b>沿用</b>（不重新构造），
/// 那样它就不会再走"战斗状态建立"那条替换路径，于是继续指着<b>上一场的那口队列</b> ——
/// 表现就是"战斗结束后球保留到下一次战斗"。这里每回合重新对齐一次：
/// 回声若还指着旧队列就换成锚点当前的队列（旧队列连同里面的球一起丢掉，新战斗不该继承）。
/// 顺带也把"战斗中途状态重建"这类情况一起兜住。
/// </remarks>
[HarmonyPatch(typeof(CombatManager), "StartTurn")]
internal static class OrbRelinkOnTurnStartPatch
{
    [HarmonyPrefix]
    private static void Prefix()
    {
        if (!TogetherPair.IsActive)
        {
            return;
        }

        try
        {
            OrbSlotSharing.Link(TogetherPair.Anchor);
        }
        catch (Exception ex)
        {
            CappedLog.Info("orb.relink", $"重新收口球位失败（忽略）：{ex.GetType().Name}: {ex.Message}");
        }
    }
}

/// <summary>球位动画镜像：本体给"这次操作的那个人"放动画时，同一套动画也放到其他成员节点上。</summary>
/// <remarks>
/// 另外在这里给 <c>EvokeOrbAnim</c> 加一道保险：本体是 <c>_orbs.Last(n =&gt; n.Model == orb)</c>，
/// 一旦这个节点的图像和共享队列不同步（镜像/补齐没跟上），本体就会抛
/// <c>Sequence contains no matching element</c> 把整个回合循环打死 —— 实测（20:45 log）破损核心开局给球、
/// 队列满触发激发时就是这么崩的。这里先尝试补齐，补不上就跳过这一次动画，绝不让本体抛。
/// </remarks>
[HarmonyPatch]
internal static class OrbVisualMirrorPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(NOrbManager), nameof(NOrbManager.AddOrbAnim));
        yield return AccessTools.Method(typeof(NOrbManager), nameof(NOrbManager.EvokeOrbAnim), new[] { typeof(OrbModel) });
        yield return AccessTools.Method(typeof(NOrbManager), nameof(NOrbManager.ClearOrbs));
        yield return AccessTools.Method(typeof(NOrbManager), nameof(NOrbManager.AddSlotAnim), new[] { typeof(int) });
        yield return AccessTools.Method(typeof(NOrbManager), nameof(NOrbManager.RemoveSlotAnim), new[] { typeof(int) });
    }

    [HarmonyPostfix]
    private static void Postfix(NOrbManager __instance, MethodBase __originalMethod, object[] __args)
    {
        OrbSlotSharing.MirrorAnim(__instance, __originalMethod.Name, __args);
    }

}

/// <summary>
/// 激发动画的保险：本体 <c>EvokeOrbAnim</c> 里是 <c>_orbs.Last(n =&gt; n.Model == orb)</c>，
/// 节点图像一旦和共享队列不同步就会抛 <c>Sequence contains no matching element</c>、把回合循环打死
/// （实测 20:45 log：破损核心开局给球、队列满触发激发时崩的就是这里）。补不上就跳过这一次动画。
/// </summary>
[HarmonyPatch(typeof(NOrbManager), nameof(NOrbManager.EvokeOrbAnim), new[] { typeof(OrbModel) })]
internal static class OrbEvokeAnimGuardPatch
{
    [HarmonyPrefix]
    private static bool Prefix(NOrbManager __instance, OrbModel orb)
    {
        try
        {
            if (OrbSlotSharing.HasOrbNode(__instance, orb))
            {
                return true;
            }

            // 节点上缺这颗球：先按共享队列补一颗，再复核；还是找不到就跳过这次动画（数据层已经激发完了）。
            __instance.AddOrbAnim();
            if (OrbSlotSharing.HasOrbNode(__instance, orb))
            {
                return true;
            }

            CappedLog.Info("orb.sync", "激发动画跳过：本机节点上没有这颗球（数据层已正常激发）");
            return false;
        }
        catch (Exception ex)
        {
            CappedLog.Info("orb.sync", $"激发动画保护失败（跳过动画）：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }
}

/// <summary>加球位：共享局把本体写死的"10 格上限"换成 <see cref="OrbSlotSharing.CapacityCap" />（10 × 机器人数量）。</summary>
[HarmonyPatch(typeof(OrbCmd), nameof(OrbCmd.AddSlots))]
internal static class OrbAddSlotsCapPatch
{
    [HarmonyPrefix]
    private static bool Prefix(Player player, int amount, ref Task __result)
    {
        if (!TogetherPair.IsActive || !TogetherPair.IsMember(player) || player.PlayerCombatState is not { } state)
        {
            return true;
        }

        if (CombatManager.Instance.IsOverOrEnding)
        {
            __result = Task.CompletedTask;
            return false;
        }

        var queue = state.OrbQueue;
        amount = Math.Min(OrbSlotSharing.CapacityCap - queue.Capacity, amount);
        queue.AddCapacity(amount);
        NCombatRoom.Instance?.GetCreatureNode(player.Creature).OrbManager?.AddSlotAnim(amount);

        __result = Task.CompletedTask;
        return false;
    }
}

/// <summary>
/// 共享球位下，球位的两个回合钩子只跑一次。
/// </summary>
/// <remarks>
/// <c>AfterTurnStart</c> / <c>BeforeTurnEnd</c> 本体是逐玩家调用的；队列共享后回响那次会双倍触发
/// （闪电被动打两遍、冰球给两次格挡）。这里只让"队列主人（锚点）"那次跑，回响那次跳过。
/// </remarks>
[HarmonyPatch]
internal static class OrbTurnHookDedupePatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(OrbQueue), nameof(OrbQueue.AfterTurnStart));
        yield return AccessTools.Method(typeof(OrbQueue), nameof(OrbQueue.BeforeTurnEnd));
    }

    [HarmonyPrefix]
    private static bool Prefix(
        OrbQueue __instance,
        PlayerChoiceContext choiceContext,
        MethodBase __originalMethod,
        ref Task __result)
    {
        if (!OrbSlotSharing.IsShared(__instance))
        {
            return true;
        }

        if (choiceContext is not HookPlayerChoiceContext { Owner: { } caller }
            || ReferenceEquals(caller, OrbSlotSharing.OwnerOf(__instance)))
        {
            return true;
        }

        CappedLog.Info(
            "orb.dedupe",
            $"共享球位：跳过 {__originalMethod.Name} 的重复触发（发起者=netId{caller.NetId}）");

        __result = Task.CompletedTask;
        return false;
    }
}
