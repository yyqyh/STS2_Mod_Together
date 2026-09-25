using System.Reflection;

using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;

namespace Together.Core.Integrations.RandomForeseer;

/// <summary>给"一次预测"打上会话标识：模拟器一构造就登记到当前线程。</summary>
/// <remarks>
/// 之后懒创建的 <c>SimPlayerCombatState</c> 就能落进同一张会话表 ——
/// 同一台模拟器里所有成员共用同一份模拟牌堆 / 球队列副本。
/// </remarks>
[HarmonyPatchCategory(RandomForeseerBridge.PatchCategory)]
[HarmonyPatch]
internal static class RandomForeseerSessionPatch
{
    private static bool Prepare()
    {
        return RandomForeseerBridge.TryResolve();
    }

    private static MethodBase? TargetMethod()
    {
        return RandomForeseerBridge.SimulatorType is { } simulator
            ? AccessTools.Constructor(simulator, [typeof(ICombatState)])
            : null;
    }

    [HarmonyPostfix]
    private static void Postfix(object __instance)
    {
        try
        {
            RandomForeseerBridge.BeginSession(__instance);
        }
        catch (Exception)
        {
            // 联动失败只是"预测保持原样"，绝不能影响对方的预测流程。
        }
    }
}
