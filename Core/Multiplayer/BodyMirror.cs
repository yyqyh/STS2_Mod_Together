using HarmonyLib;

using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Logging;

namespace Together.Core.Multiplayer;

/// <summary>
/// 共享身体（生命 / 最大生命 / 格挡）的镜像逻辑（不含补丁特性）。
/// </summary>
/// <remarks>
/// <para>
/// 引擎强制每个玩家各有一个 <c>Creature</c>（<c>CombatState.Players</c> 是从
/// <c>PlayerCreatures.Select(c =&gt; c.Player)</c> 反推的），所以"一个身体"是靠**镜像**实现的：
/// 谁的值变了，就把新值推给组里**其他所有成员**（共生体支持 2~4 人）。
/// </para>
/// <para>
/// 三个值的收口都是同一种形状——<c>Block</c> / <c>CurrentHp</c> / <c>MaxHp</c> 的 private setter，
/// 且都带 <c>if (旧值 != 新值)</c> 判断。补丁点唯一完备（构造函数是直接写字段、不走 setter），
/// 而且天然收敛：值相等时 setter 直接返回，互相推不会无限递归（另有深度守卫兜底）。
/// </para>
/// <para>
/// 于是"几个人同时起相同护甲"不需要任何特殊规则：各人打防御各加各的，池子累加，
/// 正好等于原版多人局几人合计的格挡，而敌人打的也是同一个池。原版卡一行都不用改。
/// </para>
/// </remarks>
internal static class BodyMirror
{
    /// <summary>重入守卫：镜像动作自己会再触发 setter，用深度计数把手挡掉。</summary>
    private static int _mirrorDepth;

    /// <summary>把锚点的三个数值整份推给组里其他成员（战斗状态重建、读档后对齐、血量提升后用）。</summary>
    public static void SyncAll()
    {
        if (TogetherPair.Anchor?.Creature is not { } from)
        {
            return;
        }

        _mirrorDepth++;
        try
        {
            foreach (var to in from.OthersOrEmpty())
            {
                PushMaxHp(from, to);
                PushHp(from, to);
                PushBlock(from, to);
            }
        }
        finally
        {
            _mirrorDepth--;
        }
    }

    internal static void MirrorBlock(Creature source, Creature target)
    {
        Mirror(() => PushBlock(source, target));
    }

    internal static void MirrorHp(Creature source, Creature target)
    {
        Mirror(() =>
        {
            PushMaxHp(source, target);
            PushHp(source, target);

            // 只可能出现在"镜像失手"的边缘态——一个死了另一个还活着。
            RepairImpossibleLifeState(source, target);
        });
    }

    internal static void MirrorMaxHp(Creature source, Creature target)
    {
        Mirror(() => PushMaxHp(source, target));
    }

    private static void Mirror(Action action)
    {
        if (_mirrorDepth > 0)
        {
            return;
        }

        _mirrorDepth++;
        try
        {
            action();
        }
        finally
        {
            _mirrorDepth--;
        }
    }

    private static void PushMaxHp(Creature from, Creature to)
    {
        if (from.MaxHp != to.MaxHp)
        {
            to.SetMaxHpInternal(from.MaxHp);
        }
    }

    private static void PushHp(Creature from, Creature to)
    {
        if (from.CurrentHp != to.CurrentHp)
        {
            to.SetCurrentHpInternal(from.CurrentHp);
        }
    }

    private static void PushBlock(Creature from, Creature to)
    {
        if (from.Block == to.Block)
        {
            return;
        }

        if (from.Block > to.Block)
        {
            to.GainBlockInternal(from.Block - to.Block);
        }
        else
        {
            to.LoseBlockInternal(to.Block - from.Block);
        }
    }

    /// <summary>
    /// 一个死、一个活是<b>不可能态</b>（共享血池意味着所有人的血量永远相等、一起死）。
    /// 真出现时说明镜像漏了一步，这里做修复：把低的一方拉平到高的一方。
    /// </summary>
    /// <remarks>
    /// 用 <c>HealInternal</c> 而不是直接写字段：它会走"从死到活"的正式流程
    /// （<c>Player.ActivateHooks()</c> + <c>Revived</c> 事件），否则复活的玩家钩子仍然是关的。
    /// </remarks>
    private static void RepairImpossibleLifeState(Creature from, Creature to)
    {
        if (from.IsDead == to.IsDead)
        {
            return;
        }

        var alive = from.IsDead ? to : from;
        var dead = from.IsDead ? from : to;

        Log.Error(
            $"[together] 共享血池出现一死一活（{alive.LogName}={alive.CurrentHp} / {dead.LogName}={dead.CurrentHp}），"
            + "按共享池语义拉平。这通常意味着有一处伤害没有走镜像收口，请带 log 反馈。");

        dead.HealInternal(alive.CurrentHp - dead.CurrentHp);
    }
}

[HarmonyPatch(typeof(Creature), "Block", MethodType.Setter)]
internal static class BlockMirrorPatch
{
    [HarmonyPostfix]
    private static void Postfix(Creature __instance)
    {
        foreach (var other in __instance.OthersOrEmpty())
        {
            BodyMirror.MirrorBlock(__instance, other);
        }
    }
}

[HarmonyPatch(typeof(Creature), "CurrentHp", MethodType.Setter)]
internal static class CurrentHpMirrorPatch
{
    [HarmonyPostfix]
    private static void Postfix(Creature __instance)
    {
        foreach (var other in __instance.OthersOrEmpty())
        {
            BodyMirror.MirrorHp(__instance, other);
        }
    }
}

[HarmonyPatch(typeof(Creature), "MaxHp", MethodType.Setter)]
internal static class MaxHpMirrorPatch
{
    [HarmonyPostfix]
    private static void Postfix(Creature __instance)
    {
        foreach (var other in __instance.OthersOrEmpty())
        {
            BodyMirror.MirrorMaxHp(__instance, other);
        }
    }
}
