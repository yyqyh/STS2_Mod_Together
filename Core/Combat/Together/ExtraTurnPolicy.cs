using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;

using Together.Core.Utils;

namespace Together.Core.Combat;

/// <summary>
/// 额外回合策略（项目决定）：<b>额外回合不清共享状态</b>。
/// </summary>
/// <remarks>
/// <para>
/// 本体的 <c>Creature.AfterTurnStart</c> 会调 <c>ClearBlock()</c>，而额外回合（如 <c>PaelsEye</c>）
/// 只会让拿到额外回合的那名玩家参与 <c>StartTurn</c>。共享血池下这意味着
/// "p1 拿额外回合 → 把 p2 攒的格挡也一起清掉"。
/// </para>
/// <para>
/// 判定用本体现成的 <c>CombatManager.IsPartOfPlayerTurn</c>：它明确写着
/// "Returns false if some player is taking an extra turn, and it's not us"。
/// 所以规则就是：<b>只有两人都参与本回合时才清共享格挡</b>。
/// </para>
/// <para>
/// <b>另外这条还兼当"额外回合崩溃"的挡箭牌</b>：实测（2026-09-22 log）额外回合开始时会抛
/// <c>NullReferenceException at Creature.AfterTurnStart(CombatSide)</c>（异常发生在该方法的同步段里，
/// 也就是它调 <c>ClearBlock()</c> 的那一刻），把整个回合循环打死 → 战斗卡住。
/// 所以这里有两条：① 有玩家在打额外回合时<b>整个跳过 AfterTurnStart</b>（等于不清共享格挡，正是本文件既定策略）；
/// ② 两个 Prefix 都套 try/catch，任何意外都退回本体行为，绝不从我们的前缀里往外抛。
/// </para>
/// <para>
/// 注意这条只覆盖格挡。持久状态的"回合数衰减"仍然会跟着额外回合多走一次
/// （本体的能力衰减挂在回合结束，不在这里）。
/// </para>
/// </remarks>
[HarmonyPatch(typeof(Creature), nameof(Creature.AfterTurnStart))]
internal static class ExtraTurnSkipAfterTurnStartPatch
{
    /// <summary>有人在打额外回合 → 这次 <c>AfterTurnStart</c> 整个不做（不清共享格挡，也避开本体在这条路径上的 NRE）。</summary>
    [HarmonyPrefix]
    private static bool Prefix()
    {
        try
        {
            return CombatManager.Instance?.PlayersTakingExtraTurn.Count is not > 0;
        }
        catch (Exception ex)
        {
            CappedLog.Info("extra.turn", $"额外回合判定失败，按本体行为继续：{ex.Message}");
            return true;
        }
    }
}

[HarmonyPatch(typeof(Creature), "ClearBlock")]
internal static class ExtraTurnNoBlockClearPatch
{
    [HarmonyPrefix]
    private static bool Prefix(Creature __instance)
    {
        try
        {
            // 额外回合：一律不清（共享格挡两个人都在用）。
            if (CombatManager.Instance?.PlayersTakingExtraTurn.Count is > 0)
            {
                CappedLog.Info("extra.turn", "额外回合：跳过清格挡（共享格挡不清）");
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
