using HarmonyLib;

using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;

using Together.Core.Diagnostics;
using Together.Core.Foundation;

namespace Together.Core.Shared.Deck;

/// <summary>
/// 变牌（<c>CardCmd.Transform</c>）前的 owner 对齐：<b>替换卡一律跟随原卡的归属</b>。
/// </summary>
/// <remarks>
/// <para>
/// <b>本体事实</b>：<c>CardCmd</c> 的变牌循环里有一条硬校验（<c>CardCmd.cs:427</c>）：
/// <c>replacement.Owner != original.Owner</c> → 抛
/// <c>Attempting to transform card X to Y, but the replacement has a different owner!</c>。
/// 这条校验的前提是"每个玩家的卡组各自独立"，共享卡组下不成立 —— 只要<b>别的 mod 把"
/// 从共享卡组里挑出来的一张牌"当成替换卡</c>（实测：幻术师「召唤」把卡组里的 SIC_EM 变出来），
/// 替换卡的 owner 就跟着<b>那张卡</b>走，而原卡是"为出牌者当场生成"的 → 两端算出的人不同，
/// <b>只有一端抛异常</b>：那张牌卡在 Play 区、后续效果（上 buff / 进弃牌堆）整段不跑 → checksum 分歧
/// （2026-09-25 分歧 #110）。
/// </para>
/// <para>
/// <b>挂 <c>CardTransformation.GetReplacement</c> 而不是 <c>Transform</c> 的重载</b>：
/// 本体所有变牌路径（单张 / 批量 / <c>TransformTo&lt;T&gt;</c> / <c>TransformToRandom</c>）最后都汇聚到
/// 这里取"替换卡"，挂这一处就全覆盖了；而且此刻替换卡还没进牌堆，改归属不会留幽灵卡。
/// </para>
/// <para>
/// <b>为什么改"跟随原卡"而不是"跟随操作者"</b>：原卡的归属是两端都算得出的同一份数据
/// （共享局里已由 <see cref="SharedDeckOwnership.Repair" /> 对齐），所以这个对齐在两端是<b>确定性</b>的；
/// 结果是变出来的牌归"它替换掉的那张牌的主人"，语义上也最自然（变牌不换主人）。
/// </para>
/// </remarks>
[HarmonyPatch(typeof(CardTransformation), nameof(CardTransformation.GetReplacement), new[] { typeof(Rng) })]
internal static class TransformOwnerAlignPatch
{
    [HarmonyPostfix]
    private static void Postfix(CardTransformation __instance, ref CardModel? __result)
    {
        try
        {
            if (!TogetherPair.IsActive || __result is null)
            {
                return;
            }

            var original = SharedDeckOwnership.OwnerOf(__instance.Original);
            var replacement = SharedDeckOwnership.OwnerOf(__result);
            if (original is null || ReferenceEquals(original, replacement))
            {
                return;
            }

            SharedDeckOwnership.SetOwner(__result, original, "transform_align");
            CappedLog.Info(
                "own.transform",
                $"变牌前对齐归属：{__instance.Original.Id.Entry} → {__result.Id.Entry}"
                + $" 归 netId{original.NetId}（替换卡原本 netId{replacement?.NetId}）");
        }
        catch (Exception)
        {
            // 对齐失败最多是"这次不修"，绝不能让原方法受影响（本体随后会自己抛它那条校验）。
        }
    }
}
