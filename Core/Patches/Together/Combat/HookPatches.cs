using System.Runtime.CompilerServices;

using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using Together.Core.Combat;
using Together.Core.Patches.Deck;
using Together.Core.Utils;

namespace Together.Core.Patches.Combat;

/// <summary>
/// 钩子监听表去重：共享牌堆 / 共享主卡组里的牌会被当成<b>两个</b>监听者，
/// 于是"每回合一次"的卡牌/附魔效果触发两遍。
/// </summary>
/// <remarks>
/// <para>
/// 两处派发源头都要去重，因为两边都会重复：
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>CombatState.IterateHookListeners</c> 是按 creature 逐个收集的：先加
/// <c>creature.Powers</c>，再加那名玩家的遗物/药水/宝珠，最后把
/// <c>player.PlayerCombatState.AllPiles</c> 里每张牌（连同它的 Affliction / Enchantment）塞进列表。
/// 共享牌库下回声的 <c>AllPiles</c> 指向的正是锚点那几口堆（见 <see cref="SharedPileImpl" />），
/// 所以同一张牌会被收集两次、派发两次。
/// </description></item>
/// <item><description>
/// <c>RunState.IterateHookListeners</c> 里是 <c>foreach (player) foreach (card in player.Deck.Cards)</c>，
/// 而回声的 <c>Deck</c> 重定向到锚点那一份 —— 主卡组的每张牌同样会被收集两次。
/// </description></item>
/// </list>
/// <para>
/// 实测症状：<c>Imbued</c>（注能）在回合开始把牌自动打出<b>两次</b>；
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
internal static class HookListenerDedupe
{
    /// <summary>前几次顺带数一遍"去掉了多少重复"作为取证，之后走纯去重路径。</summary>
    private static int _reports;

    public static IEnumerable<AbstractModel> Apply(IEnumerable<AbstractModel> listeners)
    {
        if (_reports < 3)
        {
            _reports++;

            var all = listeners as IReadOnlyCollection<AbstractModel> ?? listeners.ToList();
            var distinct = all.Distinct<AbstractModel>(ReferenceComparer.Instance).ToList();

            if (distinct.Count != all.Count)
            {
                CappedLog.Info(
                    "hook.dedupe",
                    $"钩子监听者去重：{all.Count} → {distinct.Count} 项"
                    + "（共享牌堆 / 共享主卡组被两个成员各枚举了一次）");
            }

            return distinct;
        }

        return listeners.Distinct<AbstractModel>(ReferenceComparer.Instance);
    }

    /// <summary>引用相等的比较器（理由见类型注释：按 Id 去重会误伤同名牌）。</summary>
    internal sealed class ReferenceComparer : IEqualityComparer<AbstractModel>
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

/// <summary>战斗级监听表去重（共享牌堆里的每张牌）。</summary>
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

        __result = HookListenerDedupe.Apply(__result);
    }
}

/// <summary>跑局级监听表去重（共享主卡组里的每张牌）。</summary>
[HarmonyPatch(typeof(RunState), nameof(RunState.IterateHookListeners))]
internal static class RunHookListenerDedupePatch
{
    [HarmonyPostfix]
    private static void Postfix(ref IEnumerable<AbstractModel> __result)
    {
        if (!TogetherPair.IsActive || __result is null)
        {
            return;
        }

        __result = HookListenerDedupe.Apply(__result);
    }
}
