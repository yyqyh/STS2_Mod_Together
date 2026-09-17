using HarmonyLib;

using MegaCrit.Sts2.Core.Models;

namespace Together.Core.Multiplayer;

/// <summary>
/// 让卡牌之间的排序变成<b>全序</b>，从而让 <c>StableShuffle</c> 真正"与输入顺序无关"。
/// </summary>
/// <remarks>
/// <para>
/// 本体的 <c>StableShuffle</c> 是"先 <c>list.Sort()</c> 抹平顺序，再用 rng 打乱"，但它依赖
/// <see cref="CardModel.CompareTo" />：同名牌同升级时直接返回 0，而 <c>List.Sort</c> 是<b>不稳定排序</b>，
/// 这些"相等"的牌之间的先后仍然取决于输入顺序 —— 共生体两端输入顺序不同，洗牌结果就不同。
/// </para>
/// <para>
/// 这里在原本"相等"的情况下继续按<b>序列化等价键</b>比大小。同键的牌序列化内容完全一样，
/// 互换位置不影响校验和，所以排序结果只取决于集合内容，与输入顺序无关。
/// </para>
/// <para>
/// 这一条同时修好了"从抽牌堆随机取牌"的卡（破灭 <c>HAVOC</c>、灾变 <c>CATASTROPHE</c>、骚动 <c>UPROAR</c>、
/// 先制打击 <c>BEAT_DOWN</c>、寻者之击 <c>SEEKER_STRIKE</c>、能量电池 <c>POWER_CELL</c> 等，
/// 它们都写成 <c>Where(...).ToList().StableShuffle(rng)</c>），以及战斗中"弃牌堆洗回抽牌堆"。
/// 实测症状：打出破灭后主机侧对两只啃咬机各多打了 5 点伤害、客户端没有，随后客户端被主机踢下线。
/// </para>
/// <para>只在本局激活共生体时生效，只影响卡牌之间的比较（遗物 / 地图 / 事件列表不碰）。</para>
/// </remarks>
[HarmonyPatch(typeof(CardModel), nameof(CardModel.CompareTo))]
internal static class DeterministicCardComparePatch
{
    [HarmonyPostfix]
    private static void Postfix(CardModel __instance, AbstractModel? other, ref int __result)
    {
        if (__result != 0 || !TogetherPair.IsActive || other is not CardModel otherCard)
        {
            return;
        }

        __result = string.CompareOrdinal(
            DeterministicCardOrder.SortKey(__instance),
            DeterministicCardOrder.SortKey(otherCard));
    }
}
