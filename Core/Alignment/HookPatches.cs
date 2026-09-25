using System.Reflection;

using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using Together.Core.Common;
using Together.Core.Diagnostics;
using Together.Core.Foundation;
using Together.Core.Shared.Body;
using Together.Core.Shared.Deck;
using Together.Core.Shared.Gold;
using Together.Core.Shared.Orb;
using Together.Core.Shared.Pet;
using Together.Core.Shared.Power;
using Together.Core.Ui;

namespace Together.Core.Alignment;
/// <summary>钩子监听表去重：共享牌堆 / 共享主卡组里的牌会被当成<b>两个</b>监听者，于是"每回合一次"的
/// 卡牌 / 附魔效果触发两遍 —— <b>只丢掉"同一张牌被枚举两次"的那一份</b>。</summary>
/// <remarks>
/// 两处派发源头都要去重：<c>CombatState.IterateHookListeners</c> 按 creature 逐个收集（powers → 那名玩家的
/// 遗物/药水/宝珠 → <c>player.PlayerCombatState.AllPiles</c> 里每张牌连同它的 Affliction / Enchantment），
/// 而共享牌库下回声的 <c>AllPiles</c> 正指向锚点那几口堆（见 <see cref="SharedPileImpl" />）；
/// <c>RunState.IterateHookListeners</c> 是 <c>foreach (player) foreach (card in player.Deck.Cards)</c>，
/// 而回声的 <c>Deck</c> 重定向到锚点那一份 —— 两边的牌都被收集两次。
/// 实测症状：<c>Imbued</c>（注能）在回合开始把牌自动打出<b>两次</b>；更要命的是第二次派发常常发生在选择界面/
/// 动画中间，把战斗循环卡住（表现为黑屏不动）。
/// 去重必须按<b>引用</b>比对：模型类的 <c>Equals</c> 有可能按 Id 比较，按值去重会把"两张同名牌"错当成一张。
/// 原版各玩家的牌堆互不相交、监听表本来就没有重复项，所以这个补丁在单人 / 原版联机下是空操作。
/// <b>只碰"我们造成的重复"</b>：先数出"被枚举了不止一次的牌"，再对牌本身、以及挂在这张牌上的
/// <c>EnchantmentModel</c> / <c>AfflictionModel</c> 去重；遗物、能力、别的 mod 的模型<b>原样保留、原顺序</b>。
/// 早先那版是把整张监听表按引用 <c>Distinct</c> 掉 —— 那会顺手吞掉"靠重复监听者实现两次效果"的 mod。
/// <b>这里只去重，不过滤"镜像副本"</b>：曾试过在回合族钩子里统一丢掉副本，那会连带丢掉"每回合重置的内部计数"
/// （实测杂耍计数整局不重置），所以那种"会改身体数值"的少数能力改成逐类处理（见 <see cref="MirroredPowerSingleFirePatch" />）。
/// </remarks>
internal static class HookListenerDedupe
{
    /// <summary>前几次顺带数一遍"去掉了多少重复"作为取证，之后走纯去重路径。</summary>
    private static int _reports;

    public static IEnumerable<AbstractModel> Apply(IEnumerable<AbstractModel> listeners)
    {
        var all = listeners as IReadOnlyCollection<AbstractModel> ?? listeners.ToList();

        // ① 先数一遍"哪些牌被枚举了不止一次"（共享牌库把同一张牌收集了两遍）。
        var counts = new Dictionary<CardModel, int>(ReferenceEqualityComparer.Instance);
        foreach (var model in all)
        {
            if (CardOf(model) is { } card)
            {
                counts[card] = counts.TryGetValue(card, out var seen) ? seen + 1 : 1;
            }
        }

        // ② 按顺序输出：属于"重复牌"的监听者（牌本身 / 它的附魔 / 它的灾祸）各只出一次。
        //    ★ 去重键必须是**监听者实例**而不是牌：一次枚举里"牌 + 附魔"是两条，
        //    只按牌去重会把第一份附魔也吞掉，那注能就永远不会自动打出了。
        var emitted = new HashSet<AbstractModel>(ReferenceEqualityComparer.Instance);
        var result = new List<AbstractModel>(all.Count);
        foreach (var model in all)
        {
            if (CardOf(model) is { } card && counts[card] > 1 && !emitted.Add(model))
            {
                continue;
            }

            result.Add(SyncMirrorPayload(model));
        }

        if (_reports < 3 && result.Count != all.Count)
        {
            _reports++;
            CappedLog.Info(
                "hook.dedupe",
                $"钩子监听者去重：{all.Count} → {result.Count} 项"
                + "（共享牌堆 / 共享主卡组被两个成员各枚举了一次）");
        }

        return result;
    }

    /// <summary>这条监听者"依附在哪张牌上"（牌本身 / 它的附魔 / 它的灾祸；其余返回 null）。</summary>
    private static CardModel? CardOf(AbstractModel model)
    {
        return model switch
        {
            CardModel card => card,
            EnchantmentModel enchantment => enchantment.Card,
            AfflictionModel affliction => affliction.Card,
            _ => null,
        };
    }

    /// <summary>枚举到某个监听者时，如果它是镜像副本，先把原件那边的内部数据同步过来。</summary>
    /// <remarks>
    /// 时机很关键：很多能力的内部数据是"施加<b>之后</b>"才由模型自己填的
    /// （夜魇的 <c>SetSelectedCard</c> 就在 <c>PowerCmd.Apply(...)</c> 返回之后），
    /// 而镜像发生在 Apply <b>内部</b> —— 克隆那一刻副本拿到的还是空数据。
    /// 这次枚举正好发生在"钩子即将被调用"之前，所以在这里同步一次最合适。
    /// </remarks>
    private static AbstractModel SyncMirrorPayload(AbstractModel model)
    {
        PowerMirror.SyncMirrorPayload(model);
        return model;
    }

}

/// <summary>监听表去重：战斗级（共享牌堆里的牌）+ 跑局级（共享主卡组里的牌）。</summary>
[HarmonyPatch]
internal static class HookListenerDedupePatches
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(CombatState), nameof(CombatState.IterateHookListeners));
        yield return AccessTools.Method(typeof(RunState), nameof(RunState.IterateHookListeners));
    }

    [HarmonyPostfix]
    private static void Postfix(ref IEnumerable<AbstractModel> __result)
    {
        if (!TogetherPair.IsActive
            || !Together.Core.Settings.TogetherSettingsSync.EffectiveCompatHookDedupe   // ★ 兼容模式开关
            || __result is null)
        {
            return;
        }

        __result = HookListenerDedupe.Apply(__result);
    }
}
