using HarmonyLib;

using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace Together.Core.Multiplayer;

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
