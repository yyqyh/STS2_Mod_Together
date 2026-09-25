using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
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

namespace Together.Core.Alignment
{
    /// <summary>
    /// 「怪招作用域」：怪物正在执行一次招式的那段时间。
    /// </summary>
    /// <remarks>
    /// <b>它不折叠目标</b>——那会连带改变"怪对每个玩家各施加一次、每个 target 各塞一份牌、AOE 打几个身体"
    /// 这些原版语义，已经撤销。现在只剩一个用途：<b>怪招里"遍历共享卡牌"的幂等性</b>。
    /// 共享卡组下两位成员的 <c>PlayerCombatState.AllCards</c> 指向同一批牌对象，而怪招是
    /// <c>foreach (target in targets)</c> 逐玩家跑的 → 同一批牌被处理 N 次（沙漏的凋萎升级就是这么坏的：
    /// 每张被升 N 级，而怪自己的计数只 +1 → 等级不一致）。判据是"这副卡只算一次"，做法是让
    /// <b>回声那一侧</b>在怪招期间的卡牌视图为空 —— 只影响怪招期间、且只影响回声。
    /// </remarks>
    internal static class MonsterMoveScope
    {
        /// <summary>一次怪招的作用域。</summary>
        internal sealed class Scope
        {
            public required string Monster { get; init; }

            /// <summary>组内成员（锚点在前）。</summary>
            public required IReadOnlyList<Player> Members { get; init; }

            /// <summary>招式跑完就置 false；用它做开关而不是清 AsyncLocal，避免时序问题。</summary>
            public bool Active { get; set; } = true;
        }

        private static readonly AsyncLocal<Scope?> Current = new();

        /// <summary>当前生效的怪招作用域；不在招式里（或已结束）时返回 null。</summary>
        public static Scope? Active => Current.Value is { Active: true } scope ? scope : null;

        /// <summary>进入作用域；不满足条件（非配对局、没有目标）时返回 null。</summary>
        public static Scope? Enter(MonsterModel? monster, IReadOnlyList<Creature> targets)
        {
            if (!TogetherPair.IsActive || monster is null || targets.Count == 0)
            {
                return null;
            }

            var members = TogetherPair.Members().ToList();
            if (members.Count < TogetherPair.MinMembers)
            {
                return null;
            }

            var scope = new Scope
            {
                Monster = monster.GetType().Name,
                Members = members,
            };

            Current.Value = scope;
            return scope;
        }

        /// <summary>招式跑完之后收尾。</summary>
        public static void Exit(Scope? scope)
        {
            if (scope is not null)
            {
                scope.Active = false;
            }
        }

        /// <summary>把收尾挂到招式任务结束之后（<c>PerformMove</c> 是 async，Postfix 拿到的是没跑完的 Task）。</summary>
        public static async Task ExitAfterAsync(Task task, Scope? scope)
        {
            try
            {
                await task;
            }
            finally
            {
                Exit(scope);
            }
        }

        /// <summary>这份战斗状态是不是"回声"那一侧（它的卡牌视图应当按空处理）。</summary>
        public static bool IsEchoState(PlayerCombatState state)
        {
            try
            {
                return TogetherPair.IsEcho(SharedPileImpl.PlayerOf(state));
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}

namespace Together.Core.Alignment
{
    /// <summary>怪招作用域的开关。</summary>
    [HarmonyPatch(typeof(MonsterModel), nameof(MonsterModel.PerformMove))]
    internal static class MonsterMoveScopePatch
    {
        [HarmonyPrefix]
        private static void Prefix(MonsterModel __instance, ref MonsterMoveScope.Scope? __state)
        {
            __state = MonsterMoveScope.Enter(__instance, __instance.CombatState?.PlayerCreatures ?? []);
        }

        [HarmonyPostfix]
        private static void Postfix(ref Task __result, MonsterMoveScope.Scope? __state)
        {
            if (__state is not null && __result is not null)
            {
                __result = MonsterMoveScope.ExitAfterAsync(__result, __state);
            }
        }
    }

    /// <summary>怪招期间：回声那一侧的"共享卡牌视图"返回空 —— 同一批共享卡只被处理一次。</summary>
    [HarmonyPatch(typeof(PlayerCombatState), "get_AllCards")]
    internal static class SharedCardViewScopePatch
    {
        [HarmonyPostfix]
        private static void Postfix(PlayerCombatState __instance, ref IEnumerable<CardModel> __result)
        {
            if (!Together.Core.Settings.TogetherSettingsSync.EffectiveCompatSharedCardView)   // ★ 兼容模式开关
            {
                return;
            }

            if (MonsterMoveScope.Active is not { } scope || !MonsterMoveScope.IsEchoState(__instance))
            {
                return;
            }

            CappedLog.Info(
                "move.shared_cards",
                $"怪招期间：{scope.Monster} 的共享卡牌只算一次（回声那份卡牌视图按空处理）");

            __result = Array.Empty<CardModel>();
        }
    }
}
