using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models;
using Together.Core.Combat;
using Together.Core.Patches.Deck;
using Together.Core.Utils;

namespace Together.Core.Patches.Combat;

/// <summary>注能（<c>Imbued</c>）：共生体下"每场战斗开始时自动打出"只能发生一次。</summary>
/// <remarks>
/// 本体的判断是：<c>player == Card.Owner &amp;&amp; player.PlayerCombatState.TurnNumber &lt;= 1</c>。
/// 原版多人局里每个人有自己的卡组，两个玩家的"第 1 回合"碰到的是<b>两张不同的牌</b>，所以各打各的没问题。
/// 共生体共享同一副卡组，而 <c>TurnNumber</c> 是<b>逐玩家</b>的（<c>PlayerCombatState.IncrementTurnNumber</c>）：
/// P1 结束回合后 P2 才开始它的第 1 回合，此刻 <b>P1 的 TurnNumber 仍然是 1</b>。
/// 而 <c>CombatManager.RunAutoPrePlayPhase</c> 是<b>对每个玩家各派发一次</b>
/// <c>Hook.AfterAutoPrePlayPhaseEntered</c>，于是 P2 回合开始时那次派发里
/// <c>player</c> 参数正是 P1（卡的归属者）→ 同一张注能牌被自动打出<b>第二次</b>。
/// 注意这和 <see cref="HookListenerDedupePatch" /> 修的不是同一件事：那边修的是"同一张牌在监听表里出现两次"
/// （共享牌堆导致同一口堆被两个玩家各收集一遍），这边是"监听表已经是一份，但两个玩家的
/// pre-play 阶段各命中了同一个归属者"。所以看起来像旧问题复发，其实是另一条路径。
/// </remarks>
[HarmonyPatch(typeof(Imbued), nameof(Imbued.AfterAutoPrePlayPhaseEntered))]
internal static class ImbuedOncePerCombatPatch
{
    /// <summary>当前记的是哪一场战斗（换战斗即清空）。</summary>
    private static ICombatState? _combat;

    /// <summary>本场战斗里已经自动打出过的注能牌（按对象引用）。</summary>
    private static readonly HashSet<CardModel> Played = new(ReferenceComparer.Instance);

    [HarmonyPrefix]
    private static bool Prefix(Imbued __instance, Player player, ref Task __result)
    {
        if (!TogetherPair.IsActive)
        {
            return true;
        }

        // 不是卡的归属者：原方法自己就会跳过，交给它，保持原版语义。
        if (!ReferenceEquals(player, __instance.Card.Owner))
        {
            return true;
        }

        if (__instance.Card.CombatState is not { } combat)
        {
            return true;
        }

        if (!ReferenceEquals(combat, _combat))
        {
            _combat = combat;
            Played.Clear();
        }

        if (Played.Add(__instance.Card))
        {
            return true;
        }

        CappedLog.Info(
            "imbued.dedupe",
            $"注能（{__instance.Card.Id.Entry}）本场战斗已经自动打出过，跳过重复派发"
            + $"（派发给={player.NetId}，其回合数={player.PlayerCombatState?.TurnNumber}）");

        __result = Task.CompletedTask;
        return false;
    }

    /// <summary>引用相等的比较器（同名牌是不同对象，不能按值去重）。</summary>
    private sealed class ReferenceComparer : IEqualityComparer<CardModel>
    {
        internal static readonly ReferenceComparer Instance = new();

        public bool Equals(CardModel? x, CardModel? y)
        {
            return ReferenceEquals(x, y);
        }

        public int GetHashCode(CardModel obj)
        {
            return RuntimeHelpers.GetHashCode(obj);
        }
    }
}

/// <summary>"会改身体数值"的回合末能力（临时力量/敏捷/集中、虚弱/易伤/脆弱）：只由<b>原件</b>结算一次，
/// 镜像副本不重复结算。</summary>
/// <remarks>
/// 这类能力的 <c>AfterSideTurnEnd</c> 判定条件是 <c>participants.Contains(Owner)</c>（自己参与了本回合就生效）；
/// 共享身体下每个成员身上各有一份镜像（镜像是必要的：各人算伤害/格挡都要读到同一份数值），
/// 回合结束时所有成员都是 participants → 每份都扣一次（实测"6 点临时力量，回合结束变成 -6"）。
/// 判定用 <see cref="PowerMirror.IsMirrorCopy" />：跟着对象走，不受结算顺序影响。
/// <b>为什么只处理这几个类、不做通用过滤</b>：通用过滤（回合族钩子里丢掉镜像副本）会连带丢掉
/// "每回合重置的内部计数"，实测杂耍（<c>JugglingPower</c>）的计数整局不重置。以后又发现某个身体类
/// 回合效果翻倍（例如恶魔形态每回合加力量），往 <see cref="TargetMethods" /> 加一行即可。
/// 这些是 <c>async</c> 方法：Prefix 返回 false 时必须自己把 <c>Task</c> 还回去，否则调用方 await 会炸。
/// </remarks>
[HarmonyPatch]
internal static class MirroredPowerSingleFirePatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(TemporaryStrengthPower), nameof(TemporaryStrengthPower.AfterSideTurnEnd));
        yield return AccessTools.Method(typeof(TemporaryDexterityPower), nameof(TemporaryDexterityPower.AfterSideTurnEnd));
        yield return AccessTools.Method(typeof(TemporaryFocusPower), nameof(TemporaryFocusPower.AfterSideTurnEnd));
        yield return AccessTools.Method(typeof(WeakPower), nameof(WeakPower.AfterSideTurnEnd));
        yield return AccessTools.Method(typeof(VulnerablePower), nameof(VulnerablePower.AfterSideTurnEnd));
        yield return AccessTools.Method(typeof(FrailPower), nameof(FrailPower.AfterSideTurnEnd));
    }

    [HarmonyPrefix]
    private static bool Prefix(PowerModel __instance, ref Task __result)
    {
        if (!TogetherPair.IsActive || !PowerMirror.IsMirrorCopy(__instance))
        {
            return true;
        }

        __result = Task.CompletedTask;
        return false;
    }
}

/// <summary>镜像副本产出的牌进手牌时，<b>目标手牌以镜像副本的宿主（<c>creator</c>）为准</b>。</summary>
/// <remarks>
/// 本体：<c>AddGeneratedCardToCombat(card, Hand, creator)</c> → <c>Add(card, Hand.GetPile(card.Owner), …)</c>
/// —— <b>目标手牌由卡的 owner 决定</b>，<c>creator</c> 只进历史/钩子。本体每种效果各自"先按 owner 建牌再传 creator"，
/// 所以两者平时相等；<b>只有镜像副本会让它们分家</b>：副本的载荷是从原件搬来的，里面的牌属于<b>原件宿主</b>，
/// 而副本跑的时候"该给谁补牌"是<b>副本自己的宿主</b>。后果（实测夜宴）：p2 那份副本的载荷里的
/// 牌 owner 是 p1 → 3 张复制牌全被塞进 <b>p1 的手牌</b>，p2 一张拿不到（若两端算出的目标手牌不同还会直接分叉掉线）。
/// <b>判据</b>：只认"<c>creator</c> 身上那份镜像副本的载荷里确实引用着这张牌（或它的克隆来源）"
/// —— 见 <see cref="PowerMirror.MirrorOriginOfGeneratedCard" />。这样两件相反的事实都能保住：
/// 镜像副本产出的牌落到宿主手里；而本体<b>故意给队友塞牌</b>的效果（<c>CunningPotion</c> / <c>PotOfGhouls</c> 传
/// owner=队友、creator=自己）因为牌不是从载荷来的，<b>不动</b>。
/// <b>为什么不用"当前正在派发谁"来认</b>（上一版就是这么写的，实测只对第一张生效）：那是个 <c>static</c> 字段，
/// 而夜宴是在 <c>await</c> 循环里一张张加牌 —— 第一张加完后本体自己会派发 <c>AfterCardGeneratedForCombat</c> 等钩子，
/// 枚举监听者时就把这个字段覆盖掉了（枚举到非能力模型时会被置成 <c>null</c>），于是后两张又按 owner 走。
/// </remarks>
[HarmonyPatch(typeof(CardPileCmd), nameof(CardPileCmd.AddGeneratedCardsToCombat))]
internal static class GeneratedCardHandTargetPatch
{
    [HarmonyPrefix]
    private static void Prefix(IEnumerable<CardModel> __0, PileType __1, Player? __2)
    {
        if (!TogetherPair.IsActive
            || __1 != PileType.Hand
            || __2 is not { } creator
            || !TogetherPair.IsMember(creator))
        {
            return;
        }

        foreach (var card in __0 as IReadOnlyList<CardModel> ?? __0.ToList())
        {
            if (card is null || ReferenceEquals(card.Owner, creator) || !TogetherPair.IsMember(card.Owner))
            {
                continue;
            }

            // 生成牌此刻还没有堆（本体紧接着会校验 card.Pile == null），所以直接改归属是安全的。
            try
            {
                if (PowerMirror.MirrorOriginOfGeneratedCard(creator, card) is not { } origin)
                {
                    continue;
                }

                var from = card.Owner;
                card.GiveToAnotherPlayer(creator);
                CappedLog.Info(
                    "gen_card.retarget",
                    $"生成牌落点改判：{DeterministicCardOrder.DescribeCards([card], 1)}"
                    + $" netId{from?.NetId} → netId{creator.NetId}"
                    + $"（来自 {creator.NetId} 身上那份镜像副本 {origin.GetType().Name}）");
            }
            catch (Exception ex)
            {
                Main.Logger.Warn($"[together] 生成牌手牌落点修正失败：{ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
