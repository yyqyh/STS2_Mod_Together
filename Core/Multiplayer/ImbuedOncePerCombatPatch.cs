using System.Runtime.CompilerServices;
using System.Threading.Tasks;

using HarmonyLib;

using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Enchantments;

namespace Together.Core.Multiplayer;

/// <summary>
/// 注能（<c>Imbued</c>）：共生体下"每场战斗开始时自动打出"只能发生一次。
/// </summary>
/// <remarks>
/// <para>
/// 本体的判断是：<c>player == Card.Owner &amp;&amp; player.PlayerCombatState.TurnNumber &lt;= 1</c>。
/// 原版多人局里每个人有自己的卡组，两个玩家的"第 1 回合"碰到的是<b>两张不同的牌</b>，所以各打各的没问题。
/// </para>
/// <para>
/// 共生体共享同一副卡组，而 <c>TurnNumber</c> 是<b>逐玩家</b>的（<c>PlayerCombatState.IncrementTurnNumber</c>）：
/// P1 结束回合后 P2 才开始它的第 1 回合，此刻 <b>P1 的 TurnNumber 仍然是 1</b>。
/// 而 <c>CombatManager.RunAutoPrePlayPhase</c> 是<b>对每个玩家各派发一次</b>
/// <c>Hook.AfterAutoPrePlayPhaseEntered</c>，于是 P2 回合开始时那次派发里
/// <c>player</c> 参数正是 P1（卡的归属者）→ 同一张注能牌被自动打出<b>第二次</b>。
/// </para>
/// <para>
/// 注意这和 <see cref="HookListenerDedupePatch" /> 修的不是同一件事：那边修的是"同一张牌在监听表里出现两次"
/// （共享牌堆导致同一口堆被两个玩家各收集一遍），这边是"监听表已经是一份，但两个玩家的
/// pre-play 阶段各命中了同一个归属者"。所以看起来像旧问题复发，其实是另一条路径。
/// </para>
/// <para>只在本局激活共生体时生效；非共生体局完全不碰（保持原版行为）。</para>
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
