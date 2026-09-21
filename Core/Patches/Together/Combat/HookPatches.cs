using System.Runtime.CompilerServices;

using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models;
using Together.Core.Combat;
using Together.Core.Patches.Deck;

namespace Together.Core.Patches.Combat;

/// <summary>
/// 钩子监听表去重：共享牌堆里的牌会被当成<b>两个</b>监听者，于是"每回合一次"的卡牌/附魔效果触发两遍。
/// </summary>
/// <remarks>
/// <para>
/// <c>CombatState.IterateHookListeners</c> 是按 creature 逐个收集的：先加
/// <c>creature.Powers</c>，再加那名玩家的遗物/药水/宝珠，最后把
/// <c>player.PlayerCombatState.AllPiles</c> 里每张牌（连同它的 Affliction / Enchantment）塞进列表。
/// </para>
/// <para>
/// 共享牌库下回声的 <c>AllPiles</c> 指向的正是锚点那几口堆（见 <see cref="SharedPileImpl" />），
/// 所以同一张牌会被收集两次、派发两次。实测症状：<c>Imbued</c>（注能）在回合开始把牌自动打出<b>两次</b>；
/// 更要命的是第二次派发常常发生在选择界面/动画中间，把战斗循环卡住（表现为黑屏不动）。
/// </para>
/// <para>
/// 去重必须按<b>引用</b>比对：模型类的 <c>Equals</c> 有可能按 Id 比较，
/// 按值去重会把"两张同名牌"错当成一张（那会漏派发一张牌的所有钩子）。
/// 原版对局里各玩家的牌堆互不相交、监听表本来就没有重复项，所以这个补丁在单人/原版联机下是空操作。
/// </para>
/// <para>
/// <b>注意</b>：这里<b>只</b>去重，不去过滤"镜像副本"。
/// 曾经试过在回合族钩子里统一丢掉镜像副本，但那会连带把"每回合重置的内部计数"也一起丢掉
/// （实测杂耍计数整局不重置），所以那种"会改身体数值"的少数能力改成逐类处理
/// （见 <see cref="MirroredTemporaryPowerGuard" />）。
/// </para>
/// </remarks>
[HarmonyPatch(typeof(CombatState), nameof(CombatState.IterateHookListeners))]
internal static class HookListenerDedupePatch
{
    [HarmonyPostfix]
    private static void Postfix(ref IEnumerable<AbstractModel> __result)
    {
        if (!TogetherPair.IsActive || __result is null)
        {
            return;
        }

        __result = Filter(__result);
    }

    private static IEnumerable<AbstractModel> Filter(IEnumerable<AbstractModel> source)
    {
        var seen = new HashSet<AbstractModel>(ReferenceComparer.Instance);

        foreach (var model in source)
        {
            if (model is null || !seen.Add(model))
            {
                continue;
            }

            yield return model;
        }
    }

    /// <summary>引用相等的比较器（理由见类型注释：按 Id 去重会误伤同名牌）。</summary>
    private sealed class ReferenceComparer : IEqualityComparer<AbstractModel>
    {
        internal static readonly ReferenceComparer Instance = new();

        public bool Equals(AbstractModel? x, AbstractModel? y)
        {
            return ReferenceEquals(x, y);
        }

        public int GetHashCode(AbstractModel obj)
        {
            return RuntimeHelpers.GetHashCode(obj);
        }
    }
}
