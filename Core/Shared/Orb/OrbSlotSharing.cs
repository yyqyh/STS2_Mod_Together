using System.Reflection;

using Godot;
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
using Together.Core.Alignment;
using Together.Core.Common;
using Together.Core.Diagnostics;
using Together.Core.Foundation;
using Together.Core.Shared.Body;
using Together.Core.Shared.Gold;
using Together.Core.Shared.Pet;
using Together.Core.Shared.Power;
using Together.Core.Ui;

namespace Together.Core.Shared.Orb;
/// <summary>球位（orb，故障机器人的那套）共享：两名成员用<b>同一口</b> OrbQueue，
/// 容量 = 各成员基础球位之和（两个故障机器人 = 3 + 3 = 6 格）。</summary>
/// <remarks>
/// 本体球位是逐玩家的（<c>PlayerCombatState.OrbQueue</c>，容量初值来自 <c>Player.BaseOrbSlotCount</c>，
/// 故障机器人 3；其他角色/mod 也一样走这个字段）。做法和"共享卡组"同源：<b>把回声那份队列字段直接换成锚点那一份</b>
/// —— 本体的 <c>OrbCmd.AddOrb / Evoke / AddSlots / RemoveSlots</c> 与界面 <c>NOrbManager</c> 都现读
/// <c>player.PlayerCombatState.OrbQueue</c>，换完两端天然操作 / 显示同一口队列；容量随后补成"各成员基础球位之和"。
/// <b>唯一的坑</b>：<c>AfterTurnStart</c>（闪电/等离子被动）与 <c>BeforeTurnEnd</c>（冰球给格挡）本体是<b>逐玩家</b>
/// 调用的，队列共享后回响那次会撞在同一口队列上 → 双倍触发；所以这两条各加一个前缀，<b>只让队列主人（锚点）跑一次</b>
/// （异步方法还得自己还 Task），判定用本体现成的 <c>HookPlayerChoiceContext.Owner</c>。
/// 上限：本体每口队列写死 10 格，共享局改成 <b>10 × 有球位的成员数</b>，见 <see cref="CapacityCap" />。
/// <b>界面</b>：<c>NOrbManager</c> 不是每帧读队列，而是<b>按节点增量画</b>的（<c>AddOrbAnim</c> 只给"这次抽球的
/// 那个人"加球），所以回声那侧不会自己长出球来 —— 见 <see cref="OrbVisualMirrorPatch" />。
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

    private static readonly AccessTools.FieldRef<NOrbManager, List<NOrb>> ManagerOrbsField =
        AccessTools.FieldRefAccess<NOrbManager, List<NOrb>>("_orbs");

    /// <summary>已经补过容量的那口队列（每场战斗会新建队列，所以按实例记）。</summary>
    private static OrbQueue? _linked;

    private static bool _syncScheduled;

    /// <summary>镜像动画时的重入守卫：镜像调用自己又会被同一个补丁看到，不加守卫会来回翻倍。</summary>
    private static bool _mirroring;

    /// <summary>球位总上限，按设置里的口径算（默认：本体每份 10 格 × 本局"有球位"的成员数）。</summary>
    /// <remarks>
    /// 三种口径见 <see cref="Together.Core.Settings.OrbCapMode" />：
    /// <c>Auto</c> = 10 × 有球位成员数（默认，两个故障机器人 = 20）、
    /// <c>Vanilla</c> = 固定 10、<c>PerMember</c> = 10 × 全部成员数。
    /// 至少给 10 格（本体自己的 <c>OrbQueue.maxCapacity</c>），绝不会比原版更少。
    /// </remarks>
    public static int CapacityCap
    {
        get
        {
            var mode = Together.Core.Settings.TogetherSettingsSync.EffectiveOrbCap;
            if (mode == Together.Core.Settings.OrbCapMode.Vanilla)
            {
                return OrbQueue.maxCapacity;
            }

            var members = TogetherPair.Members().ToList();
            var count = mode == Together.Core.Settings.OrbCapMode.PerMember
                ? members.Count
                : members.Count(member => member.BaseOrbSlotCount > 0);

            return OrbQueue.maxCapacity * Math.Max(1, count);
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

    /// <summary>把这次球位动画照搬到其他成员的节点上。</summary>
    /// <remarks>
    /// 本体的球位界面按节点增量画、只画"这次操作的那个人"：不镜像的话回声那侧既看不到球，
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

/// <summary>每次玩家回合开始都重新收口一次球位共享。</summary>
/// <remarks>
/// 下一场战斗时回声那份 <c>PlayerCombatState</c> 可能被本体<b>沿用</b>（不重新构造），
/// 那样它就不会再走"战斗状态建立"那条替换路径，于是继续指着<b>上一场的那口队列</b> ——
/// 表现就是"战斗结束后球保留到下一次战斗"。这里每回合重新对齐一次：
/// 回声若还指着旧队列就换成锚点当前的队列（旧队列连同里面的球一起丢掉，新战斗不该继承）。
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

/// <summary>球位界面的两个收口：① 动画镜像（本体只给"这次操作的那个人"放动画）；
/// ② 激发动画的保险（节点图像与共享队列不同步时本体必抛，见下）。</summary>
/// <remarks>
/// 保险那条：<c>EvokeOrbAnim</c> 里是 <c>_orbs.Last(n =&gt; n.Model == orb)</c>，找不到就抛
/// <c>Sequence contains no matching element</c> 把整个回合循环打死（实测 20:45 log：破损核心开局给球、
/// 队列满触发激发时就是这么崩的）——补不上就跳过这一次动画，数据层已经激发完了。
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

    [HarmonyPrefix]
    private static bool Prefix(NOrbManager __instance, MethodBase __originalMethod, object[] __args)
    {
        // 只给激发那条上保险；其余四个挂点（加球/清球/加位/减位）走本体的 Postfix 镜像。
        if (__originalMethod.Name != nameof(NOrbManager.EvokeOrbAnim) || __args is not [OrbModel orb])
        {
            return true;
        }

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
        NCombatRoom.Instance?.GetCreatureNode(player.Creature)?.OrbManager?.AddSlotAnim(amount);

        __result = Task.CompletedTask;
        return false;
    }
}

/// <summary>共享球位下，球位的两个回合钩子只跑一次（<c>AfterTurnStart</c> / <c>BeforeTurnEnd</c> 本体逐玩家调用，
/// 队列共享后回响那次会双倍触发：闪电被动打两遍、冰球给两次格挡）。只让"队列主人（锚点）"那次跑。</summary>
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
