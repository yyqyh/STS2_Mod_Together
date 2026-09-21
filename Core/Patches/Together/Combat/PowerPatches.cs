using System.Runtime.CompilerServices;
using System.Threading.Tasks;

using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models;
using Together.Core.Combat;
using Together.Core.Patches.Deck;
using Together.Core.Utils;

namespace Together.Core.Patches.Combat;

// ======================================================================
// 合并自 Core/Multiplayer/ImbuedOncePerCombatPatch.cs（2026-09-21 合并文件，正文未改动）
// ======================================================================
/// <summary>
/// 注能（<c>Imbued</c>）：共生体下"每场战斗开始时自动打出"只能发生一次。
/// </summary>
/// <remarks>
/// <para>
/// 本体的判断是：<c>player == Card.Owner &amp;&amp; player.PlayerCombatState.TurnNumber &lt;= 1</c>。
/// 原版多人局里每个人有自己的卡组，两个玩家的"第 1 回合"碰到的是<b>两张不同的牌</b>，所以各打各的没问题。
/// </para>
/// <para>
/// 共生体共享同一副卡组，而 <c>TurnNumber</c> 是<b>逐玩家</b>的（<c>PlayerCombatState.IncrementTurnNumber</c>）：
/// P1 结束回合后 P2 才开始它的第 1 回合，此刻 <b>P1 的 TurnNumber 仍然是 1</b>。
/// 而 <c>CombatManager.RunAutoPrePlayPhase</c> 是<b>对每个玩家各派发一次</b>
/// <c>Hook.AfterAutoPrePlayPhaseEntered</c>，于是 P2 回合开始时那次派发里
/// <c>player</c> 参数正是 P1（卡的归属者）→ 同一张注能牌被自动打出<b>第二次</b>。
/// </para>
/// <para>
/// 注意这和 <see cref="HookListenerDedupePatch" /> 修的不是同一件事：那边修的是"同一张牌在监听表里出现两次"
/// （共享牌堆导致同一口堆被两个玩家各收集一遍），这边是"监听表已经是一份，但两个玩家的
/// pre-play 阶段各命中了同一个归属者"。所以看起来像旧问题复发，其实是另一条路径。
/// </para>
/// <para>只在本局激活共生体时生效；非共生体局完全不碰（保持原版行为）。</para>
/// </remarks>
[HarmonyPatch(typeof(Imbued), nameof(Imbued.AfterAutoPrePlayPhaseEntered))]
internal static class ImbuedOncePerCombatPatch
{
    /// <summary>当前记的是哪一场战斗（换战斗即清空）。</summary>
    private static ICombatState? _combat;

    /// <summary>本场战斗里已经自动打出过的注能牌（按对象引用）。</summary>
    private static readonly HashSet<CardModel> Played = new(ReferenceComparer.Instance);

    [HarmonyPrefix]
    private static bool Prefix(Imbued __instance, Player player, ref Task __result)
    {
        if (!TogetherPair.IsActive)
        {
            return true;
        }

        // 不是卡的归属者：原方法自己就会跳过，交给它，保持原版语义。
        if (!ReferenceEquals(player, __instance.Card.Owner))
        {
            return true;
        }

        if (__instance.Card.CombatState is not { } combat)
        {
            return true;
        }

        if (!ReferenceEquals(combat, _combat))
        {
            _combat = combat;
            Played.Clear();
        }

        if (Played.Add(__instance.Card))
        {
            return true;
        }

        CappedLog.Info(
            "imbued.dedupe",
            $"注能（{__instance.Card.Id.Entry}）本场战斗已经自动打出过，跳过重复派发"
            + $"（派发给={player.NetId}，其回合数={player.PlayerCombatState?.TurnNumber}）");

        __result = Task.CompletedTask;
        return false;
    }

    /// <summary>引用相等的比较器（同名牌是不同对象，不能按值去重）。</summary>
    private sealed class ReferenceComparer : IEqualityComparer<CardModel>
    {
        internal static readonly ReferenceComparer Instance = new();

        public bool Equals(CardModel? x, CardModel? y)
        {
            return ReferenceEquals(x, y);
        }

        public int GetHashCode(CardModel obj)
        {
            return RuntimeHelpers.GetHashCode(obj);
        }
    }
}

// ======================================================================
// 合并自 Core/Multiplayer/MirroredTemporaryPowerPatch.cs（2026-09-21 合并文件，正文未改动）
// ======================================================================
/// <summary>
/// "会改身体数值"的回合末能力：只由<b>原件</b>结算一次，镜像副本不重复结算。
/// </summary>
/// <remarks>
/// <para>
/// 这些能力的 <c>AfterSideTurnEnd</c> 是"本回合加的属性/减益层数，回合结束收回一层"：
/// 判定条件是 <c>participants.Contains(Owner)</c> —— 只要自己这个 creature 参与了本回合就生效。
/// 共享身体下能力在每个成员身上各有一份镜像（<see cref="PowerMirror" />，镜像是必要的：
/// 各人算伤害/格挡都要读到同一份数值），而回合一结束时<b>所有成员都是 participants</b> →
/// 每份都扣一次。实测"6 点临时力量，回合结束变成 -6"、减益掉得比原版快一倍，都是这么来的。
/// </para>
/// <para>
/// 判定用 <see cref="PowerMirror.IsMirrorCopy" />（跟着对象走，不受结算顺序影响 ——
/// 第一个副本会把自己和其他镜像一起删掉，用"别人身上还有没有副本"来判断会漏）。
/// </para>
/// <para>
/// <b>为什么只处理这几个类、不做通用过滤</b>：通用过滤（回合族钩子里丢掉镜像副本）会连带丢掉
/// "每回合重置的内部计数"，实测杂耍（<c>JugglingPower</c>）的计数整局不重置。
/// 所以只有"会改身体数值"的这几类走单份，别的一律保持"每人一份"的原版等价语义。
/// 以后如果又发现某个身体类回合效果翻倍（例如恶魔形态每回合加力量），照这个文件加一行即可。
/// </para>
/// <para>
/// 实现要点：这些是 <c>async</c> 方法，Prefix 返回 false 时必须自己把 <c>Task</c> 还回去，
/// 否则调用方拿到 null、在 await 处直接炸。
/// </para>
/// </remarks>
internal static class MirroredTemporaryPowerGuard
{
    /// <summary>这份能力是不是"不该再自己跑一次"的镜像副本。</summary>
    public static bool ShouldSkip(PowerModel power)
    {
        return TogetherPair.IsActive && PowerMirror.IsMirrorCopy(power);
    }
}

/// <summary>临时力量（含药剂/卡牌派生的一堆子类）回合末只收回一次。</summary>
[HarmonyPatch(typeof(TemporaryStrengthPower), nameof(TemporaryStrengthPower.AfterSideTurnEnd))]
internal static class TemporaryStrengthSingleFirePatch
{
    [HarmonyPrefix]
    private static bool Prefix(TemporaryStrengthPower __instance, ref Task __result)
    {
        if (!MirroredTemporaryPowerGuard.ShouldSkip(__instance))
        {
            return true;
        }

        __result = Task.CompletedTask;
        return false;
    }
}

/// <summary>临时敏捷（Fade / Anticipate / SpeedPotion 等）回合末只收回一次。</summary>
[HarmonyPatch(typeof(TemporaryDexterityPower), nameof(TemporaryDexterityPower.AfterSideTurnEnd))]
internal static class TemporaryDexteritySingleFirePatch
{
    [HarmonyPrefix]
    private static bool Prefix(TemporaryDexterityPower __instance, ref Task __result)
    {
        if (!MirroredTemporaryPowerGuard.ShouldSkip(__instance))
        {
            return true;
        }

        __result = Task.CompletedTask;
        return false;
    }
}

/// <summary>临时集中（Hotfix 等）回合末只收回一次。</summary>
[HarmonyPatch(typeof(TemporaryFocusPower), nameof(TemporaryFocusPower.AfterSideTurnEnd))]
internal static class TemporaryFocusSingleFirePatch
{
    [HarmonyPrefix]
    private static bool Prefix(TemporaryFocusPower __instance, ref Task __result)
    {
        if (!MirroredTemporaryPowerGuard.ShouldSkip(__instance))
        {
            return true;
        }

        __result = Task.CompletedTask;
        return false;
    }
}

/// <summary>虚弱：敌方回合结束时只减一层（镜像副本不重复减）。</summary>
[HarmonyPatch(typeof(WeakPower), nameof(WeakPower.AfterSideTurnEnd))]
internal static class WeakSingleFirePatch
{
    [HarmonyPrefix]
    private static bool Prefix(WeakPower __instance, ref Task __result)
    {
        if (!MirroredTemporaryPowerGuard.ShouldSkip(__instance))
        {
            return true;
        }

        __result = Task.CompletedTask;
        return false;
    }
}

/// <summary>易伤：同上。</summary>
[HarmonyPatch(typeof(VulnerablePower), nameof(VulnerablePower.AfterSideTurnEnd))]
internal static class VulnerableSingleFirePatch
{
    [HarmonyPrefix]
    private static bool Prefix(VulnerablePower __instance, ref Task __result)
    {
        if (!MirroredTemporaryPowerGuard.ShouldSkip(__instance))
        {
            return true;
        }

        __result = Task.CompletedTask;
        return false;
    }
}

/// <summary>脆弱：同上。</summary>
[HarmonyPatch(typeof(FrailPower), nameof(FrailPower.AfterSideTurnEnd))]
internal static class FrailSingleFirePatch
{
    [HarmonyPrefix]
    private static bool Prefix(FrailPower __instance, ref Task __result)
    {
        if (!MirroredTemporaryPowerGuard.ShouldSkip(__instance))
        {
            return true;
        }

        __result = Task.CompletedTask;
        return false;
    }
}
