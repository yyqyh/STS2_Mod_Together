using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;

using Together.Core.Utils;

namespace Together.Core.Combat;

/// <summary>
/// 额外回合策略（项目决定）：<b>额外回合不清共享状态</b>（佩尔之眼等）。
/// </summary>
/// <remarks>
/// <para>
/// 本体 <c>Creature.AfterTurnStart</c> 会调 <c>ClearBlock()</c>，而额外回合只会让拿到额外回合的那个人参与
/// StartTurn（<c>CombatManager.IsPartOfPlayerTurn</c> 对其他人返回 false）。共享格挡下这意味着
/// "p1 拿额外回合 → 把 p2 攒的格挡也一起清掉"，所以规则是：<b>有玩家在打额外回合时不清共享格挡</b>。
/// </para>
/// <para>
/// <b>跳过一个 async 方法，必须自己把 Task 还回去</b>：<c>AfterTurnStart</c> / <c>ClearBlock</c> 都是
/// <c>async Task</c>，Harmony 前缀返回 false 时 <c>__result</c> 保持默认值 <c>null</c>，
/// 调用方 <c>await</c> 一个 null Task 立刻 NRE —— 表现就是"额外回合刚开始，回合循环就死了、战斗卡住"
/// （2026-09-22 log：<c>Combat #2 turn loop died … NullReferenceException at CombatManager.StartTurn</c>，
/// 抛在 <c>await item3.AfterTurnStart(...)</c> 这一行）。所以下面两条都自己还 Task。
/// </para>
/// </remarks>
[HarmonyPatch(typeof(Creature), nameof(Creature.AfterTurnStart))]
internal static class ExtraTurnSkipAfterTurnStartPatch
{
    /// <summary>有人在打额外回合 → 这次 <c>AfterTurnStart</c> 整个不做（不清共享格挡，也避开本体在这条路径上的 NRE）。</summary>
    [HarmonyPrefix]
    private static bool Prefix(ref Task __result)
    {
        try
        {
            if (CombatManager.Instance?.PlayersTakingExtraTurn.Count is not > 0)
            {
                return true;
            }

            CappedLog.Info("extra.turn", "额外回合：跳过 AfterTurnStart（共享格挡不清）");
            __result = Task.CompletedTask;
            return false;
        }
        catch (Exception ex)
        {
            CappedLog.Info("extra.turn", $"额外回合判定失败，按本体行为继续：{ex.Message}");
            return true;
        }
    }
}

/// <summary>额外回合（或组里有人不参与本回合）时，<c>ClearBlock</c> 不清共享格挡。</summary>
[HarmonyPatch(typeof(Creature), "ClearBlock")]
internal static class ExtraTurnNoBlockClearPatch
{
    [HarmonyPrefix]
    private static bool Prefix(Creature __instance, ref Task __result)
    {
        try
        {
            // 额外回合：一律不清（共享格挡两个人都在用）。
            if (CombatManager.Instance?.PlayersTakingExtraTurn.Count is > 0)
            {
                CappedLog.Info("extra.turn", "额外回合：跳过清格挡（共享格挡不清）");
                __result = Task.CompletedTask;
                return false;
            }

            if (__instance.Player is not { } player)
            {
                return true;
            }

            // 保险：组里任何一个人没参与本回合 → 也按额外回合处理。
            foreach (var other in TogetherPair.OthersOf(player))
            {
                if (CombatManager.Instance?.IsPartOfPlayerTurn(other) is false)
                {
                    __result = Task.CompletedTask;
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            CappedLog.Info("extra.turn", $"清格挡判定失败，按本体行为继续：{ex.Message}");
            return true;
        }
    }
}
