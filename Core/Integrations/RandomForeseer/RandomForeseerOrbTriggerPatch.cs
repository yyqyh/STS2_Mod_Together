using System.Reflection;

using HarmonyLib;

namespace Together.Core.Integrations.RandomForeseer;

/// <summary>
/// 回合末的充能球触发：同一口（共享）球队列在<b>一次预测里只跑一次</b>。
/// </summary>
/// <remarks>
/// 模拟器是"逐个结束回合的玩家"跑 <c>DoTurnEnd</c> 的，每个玩家都会调一次
/// <c>OrbQueue.BeforeTurnEnd</c>。共享身体下所有人用的是同一口队列，
/// 不拦就会把球被动算两遍（预测偏高）—— 游戏里由 together 的 <c>OrbTurnHookDedupePatch</c>
/// 保证"只有锚点那份真正触发"，这里就是那条规则在预测侧的对应实现。
/// </remarks>
[HarmonyPatchCategory(RandomForeseerBridge.PatchCategory)]
[HarmonyPatch]
internal static class RandomForeseerOrbTriggerPatch
{
    private static bool Prepare()
    {
        return RandomForeseerBridge.TryResolve();
    }

    private static MethodBase? TargetMethod()
    {
        return RandomForeseerBridge.SimOrbQueueType is { } orbQueue
               && RandomForeseerBridge.SimulatorType is { } simulator
            ? AccessTools.Method(orbQueue, "BeforeTurnEnd", [simulator])
            : null;
    }

    /// <summary>返回 false = 跳过原方法（这次预测里这口队列已经触发过了）。</summary>
    [HarmonyPrefix]
    private static bool Prefix(object __instance, object __0)
    {
        try
        {
            if (!RandomForeseerBridge.Enabled)
            {
                return true;
            }

            if (RandomForeseerBridge.SessionOf(__0) is not { } session)
            {
                return true;
            }

            return session.OrbTriggers.Add(__instance);
        }
        catch (Exception)
        {
            return true;
        }
    }
}
