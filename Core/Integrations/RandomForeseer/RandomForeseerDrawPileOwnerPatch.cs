using System.Reflection;

using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using Together.Core.Foundation;

namespace Together.Core.Integrations.RandomForeseer;

/// <summary>
/// 冻眼（抽牌堆排序 / 洗牌预览）里"这口抽牌堆属于谁"的联动：共享局里固定算成<b>锚点</b>。
/// </summary>
/// <remarks>
/// 对方用 <c>PlayerCombatState.DrawPile == pile</c> 反推抽牌堆主人；共享牌堆下<b>两个成员都满足</b>，
/// <c>FirstOrDefault</c> 会给出列表里的第一个人 —— 未必是锚点。
/// 而真机里洗牌那一步是锚点执行的（RNG 流也是他那份），拿错人会算出不同的洗牌顺序。
/// 所以这里在共享局里把它钉成锚点。
/// </remarks>
[HarmonyPatchCategory(RandomForeseerBridge.PatchCategory)]
[HarmonyPatch]
internal static class RandomForeseerDrawPileOwnerPatch
{
    private static bool Prepare()
    {
        return RandomForeseerBridge.TryResolve();
    }

    private static MethodBase? TargetMethod()
    {
        return RandomForeseerBridge.CardPileUtilsType is { } utils
            ? AccessTools.Method(utils, "TryGetDrawPileOwner")
            : null;
    }

    [HarmonyPostfix]
    private static void Postfix(ref Player? __1, bool __result)
    {
        try
        {
            if (!__result || !RandomForeseerBridge.Enabled)
            {
                return;
            }

            if (__1 is not { } owner || !TogetherPair.IsMember(owner) || TogetherPair.Anchor is not { } anchor)
            {
                return;
            }

            if (!ReferenceEquals(owner, anchor))
            {
                __1 = anchor;
            }
        }
        catch (Exception)
        {
            // 联动失败只是"预测保持原样"。
        }
    }
}
