using HarmonyLib;

using MegaCrit.Sts2.Core.Runs;

namespace Together.Core.Multiplayer;

/// <summary>
/// 新跑局：等 <c>RunState</c> 完全构造完之后再激活共享配对。
/// </summary>
/// <remarks>
/// 绝不能提前激活——<c>CreateShared</c> 会在设置 <c>player.RunState</c> 之后
/// 遍历该玩家的卡组给每张卡设 owner，提前激活会让第二次遍历读到被重定向的卡组，
/// 从而"同一张牌设两次 owner"抛异常（实测开局黑屏就是这么来的）。见 <see cref="TogetherPair.Arm" />。
/// </remarks>
[HarmonyPatch(typeof(RunState), nameof(RunState.CreateForNewRun))]
internal static class RunCreatedPatch
{
    [HarmonyPostfix]
    private static void Postfix(RunState __result)
    {
        TogetherPair.Arm(__result, isNewRun: true);
    }
}

/// <summary>读档 / 重连：同样在 <c>RunState</c> 构造完之后激活。</summary>
[HarmonyPatch(typeof(RunState), nameof(RunState.FromSerializable))]
internal static class RunLoadedPatch
{
    [HarmonyPostfix]
    private static void Postfix(RunState __result)
    {
        // 读档 / 重连：不能再加血量上限（存档里已经有了）。
        TogetherPair.Arm(__result, isNewRun: false);
    }
}
