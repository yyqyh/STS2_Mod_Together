using HarmonyLib;

using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;

namespace Together.Core.Multiplayer;

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
/// 注意这条只覆盖格挡。持久状态的"回合数衰减"仍然会跟着额外回合多走一次
/// （本体的能力衰减挂在回合结束，不在这里）。
/// </para>
/// </remarks>
[HarmonyPatch(typeof(Creature), "ClearBlock")]
internal static class ExtraTurnNoBlockClearPatch
{
    [HarmonyPrefix]
    private static bool Prefix(Creature __instance)
    {
        if (__instance.Player is not { } player)
        {
            return true;
        }

        // 组里任何一个人没参与本回合 → 这是额外回合 → 不清共享格挡。
        foreach (var other in TogetherPair.OthersOf(player))
        {
            if (!CombatManager.Instance.IsPartOfPlayerTurn(other))
            {
                return false;
            }
        }

        return true;
    }
}
