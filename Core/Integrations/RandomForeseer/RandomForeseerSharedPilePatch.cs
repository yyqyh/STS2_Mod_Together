using System.Reflection;

using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;

namespace Together.Core.Integrations.RandomForeseer;

/// <summary>
/// 共享牌堆 / 共享球位的联动：模拟战斗状态一构造出来，就把它的四口共享战斗堆 + 球队列
/// 换成"本次预测里的唯一那份副本"。
/// </summary>
/// <remarks>
/// <para>
/// 对方的模拟状态是<b>按玩家</b>建的（每人一份），每份都会把该玩家的 <c>DrawPile/DiscardPile/ExhaustPile/PlayPile</c>
/// 各自快照一次。共享身体下这几个实例在真实世界里<b>是同一份</b>，于是同一张牌被快照成两份、
/// 抽牌时"移走"的那份和"读顶"的那份错开 —— 这就是"连续都是第一张牌"的来源。
/// </para>
/// <para>
/// 手牌不换（没共享）；球位换成同一份，对应游戏里"所有人共用一口队列、只有锚点那份真正触发"。
/// </para>
/// </remarks>
[HarmonyPatchCategory(RandomForeseerBridge.PatchCategory)]
[HarmonyPatch]
internal static class RandomForeseerSharedPilePatch
{
    private static bool Prepare()
    {
        return RandomForeseerBridge.TryResolve();
    }

    private static MethodBase? TargetMethod()
    {
        return RandomForeseerBridge.PlayerStateType is { } playerState
            ? AccessTools.Constructor(playerState, [typeof(PlayerCombatState)])
            : null;
    }

    [HarmonyPostfix]
    private static void Postfix(object __instance, PlayerCombatState __0)
    {
        try
        {
            if (!RandomForeseerBridge.Enabled || __0 is null)
            {
                return;
            }

            RandomForeseerBridge.AliasSharedPiles(__instance, __0);
        }
        catch (Exception)
        {
            // 联动失败只是"预测保持原样"，绝不能影响对方的预测流程。
        }
    }
}
