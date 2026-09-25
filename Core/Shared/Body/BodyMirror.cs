using System.Reflection;
using System.Runtime.CompilerServices;

using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using Together.Core.Alignment;
using Together.Core.Common;
using Together.Core.Diagnostics;
using Together.Core.Foundation;
using Together.Core.Settings;
using Together.Core.Shared.Gold;
using Together.Core.Shared.Orb;
using Together.Core.Shared.Pet;
using Together.Core.Shared.Power;
using Together.Core.Ui;

namespace Together.Core.Shared.Body;

/// <summary>
/// 共享身体（生命 / 最大生命 / 格挡）的镜像逻辑（不含补丁特性）。
/// </summary>
/// <remarks>
/// 引擎强制每人各有一个 <c>Creature</c>（<c>CombatState.Players</c> 由
/// <c>PlayerCreatures.Select(c =&gt; c.Player)</c> 反推），所以"一个身体"靠**镜像**实现：
/// 谁的值变了就推给组里其他所有成员（共生体支持 2~4 人）。
/// 三个值的收口形状一致（<c>Block</c> / <c>CurrentHp</c> / <c>MaxHp</c> 的 private setter +
/// <c>if (旧值 != 新值)</c>），补丁点唯一完备、天然收敛，深度守卫只是兜底。
/// 于是"几个人各起一份护甲"不需要特殊规则：池子累加，正好等于原版多人局的合计格挡。
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

    /// <summary>召唤物专用：只推生命上限 + 当前生命（不做"一死一活拉平"，也不推格挡）。</summary>
    internal static void MirrorPetHp(Creature source, Creature target)
    {
        Mirror(() =>
        {
            PushMaxHp(source, target);
            PushPetHp(source, target);
        });
    }

    internal static void MirrorPetMaxHp(Creature source, Creature target)
    {
        Mirror(() => PushMaxHp(source, target));
    }

    /// <summary>召唤物血量：死→活必须走 <c>HealInternal</c>（补 <c>Revived</c> 事件、界面靠它重新显示），
    /// 直接写字段只会数据活了、画面还是空的。</summary>
    private static void PushPetHp(Creature from, Creature to)
    {
        if (from.CurrentHp == to.CurrentHp)
        {
            return;
        }

        if (to.IsDead && from.IsAlive)
        {
            to.HealInternal(from.CurrentHp - to.CurrentHp);
            RefreshPetNode(to);
            return;
        }

        to.SetCurrentHpInternal(from.CurrentHp);
        RefreshPetNode(to);
    }

    /// <summary>
    /// 让<b>本机</b>的召唤物节点立刻显示"活着 + 血量"（对方召的 / 复活的也一样）。
    /// </summary>
    /// <remarks>
    /// 召唤物只有一个生物节点（共享战斗状态），但存活状态与大小是画面自己缓存的：只在数据层写血，
    /// 另一侧窗口可能还停在"隐藏/空血"。<b>必须延到帧末做</b>：血量 setter 在伤害结算的同步路径里，
    /// 直接动节点（Tween / 重设显示）= 在结算中间插一次 UI 操作 —— 实测（22:15 log）P2 打出第三张牌后
    /// 日志停在 "playing card …" 就没了。用 <c>CallDeferred</c> 后最坏只是晚一帧显示。
    /// 节点找不到时留一条诊断，用于判断"这台机器压根没建这只召唤物的节点"。
    /// </remarks>
    private static void RefreshPetNode(Creature pet)
    {
        try
        {
            Godot.Callable.From(() =>
            {
                try
                {
                    var node = NCombatRoom.Instance?.GetCreatureNode(pet);
                    if (node is null || !Godot.GodotObject.IsInstanceValid(node))
                    {
                        CappedLog.Info("summon.missing", $"本机没有召唤物节点（{pet.LogName}），血量只能在数据层同步");
                        return;
                    }

                    // 死掉的那只不跟血上限变大/缩小（实测：未复活的奥斯提曾跟着一起变大），显示交给"复活"流程。
                    if (pet.IsDead)
                    {
                        return;
                    }

                    node.OstyScaleToSize(pet.MaxHp, 0.2);
                }
                catch (Exception ex)
                {
                    CappedLog.Info("summon.missing", $"刷新召唤物节点失败（忽略）：{ex.GetType().Name}: {ex.Message}");
                }
            }).CallDeferred();
        }
        catch (Exception ex)
        {
            CappedLog.Info("summon.missing", $"安排召唤物节点刷新失败（忽略）：{ex.GetType().Name}: {ex.Message}");
        }
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
    /// 一死一活是<b>不可能态</b>（共享血池血量永远相等、一起死）；真出现说明镜像漏了一步，把低的一方拉平。
    /// </summary>
    /// <remarks>
    /// 用 <c>HealInternal</c> 而非直接写字段：它走"从死到活"的正式流程
    /// （<c>Player.ActivateHooks()</c> + <c>Revived</c>），否则复活的玩家钩子仍然是关的。
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

/// <summary>身体数值（格挡 / 当前生命 / 生命上限）的 setter 一改，就镜像给共生的另一半。</summary>
[HarmonyPatch]
internal static class BodyStatMirrorPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(Creature), "set_Block");
        yield return AccessTools.Method(typeof(Creature), "set_CurrentHp");
        yield return AccessTools.Method(typeof(Creature), "set_MaxHp");
    }

    [HarmonyPostfix]
    private static void Postfix(Creature __instance, MethodBase __originalMethod)
    {
        var setter = __originalMethod.Name;

        foreach (var other in __instance.OthersOrEmpty())
        {
            switch (setter)
            {
                case "set_Block":
                    BodyMirror.MirrorBlock(__instance, other);
                    break;

                case "set_CurrentHp":
                    BodyMirror.MirrorHp(__instance, other);
                    break;

                default:
                    BodyMirror.MirrorMaxHp(__instance, other);
                    break;
            }
        }

        // 召唤物（奥斯提这类"替你去死"的 pet）：两成员各一只，血量必须一致，否则一边死一边活
        // （打向主人的未格挡伤害会被本体改道到它身上）。只推血量/上限；pet 上的格挡是"主人的格挡"，本体自己画。
        // 召唤期间**不推**：这一波由本体自己处理，镜像插手会把"队友那次召唤"的结果抄回去（实测 5→5→5→10 翻倍）。
        if (setter is "set_CurrentHp" or "set_MaxHp"
            && !PetSummonFanoutPatch.IsFanningOut
            && SummonMirror.PartnerOf(__instance) is { } counterpart)
        {
            if (setter == "set_CurrentHp")
            {
                BodyMirror.MirrorPetHp(__instance, counterpart);
            }
            else
            {
                BodyMirror.MirrorPetMaxHp(__instance, counterpart);
            }
        }
    }
}

