using System.Runtime.CompilerServices;

using HarmonyLib;

using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace Together.Core.Multiplayer;

/// <summary>
/// 状态（powers）镜像的共享逻辑（不含补丁特性）。
/// </summary>
/// <remarks>
/// <para>
/// 关键事实：本体钩子是<b>全量广播</b>——<c>Hook.AfterCardPlayed</c> 就是
/// <c>foreach (var model in combatState.IterateHookListeners())</c>，
/// 每个能力都会收到战斗里发生的每一个事件，然后自己用 owner 判断要不要管，例如
/// <c>PanachePower</c> 里那句 <c>if (cardPlay.Card.Owner != Owner.Player) return;</c>。
/// </para>
/// <list type="bullet">
/// <item><description><see cref="PowerMirrorPolicy.Mirror" />（默认）：组里每个 creature 各挂一份，
/// 每份只数自己的牌、只对自己的回合生效——**等价原版多人局**（每个人各买了一份能力）。</description></item>
/// <item><description><see cref="PowerMirrorPolicy.SingleInstance" />：不复制，只留在被施加的那一侧。</description></item>
/// </list>
/// <para>
/// <b>注意</b>：<c>SingleInstance</c> 只解决"只有一份"，不解决"这份要统计所有人的动作"。
/// 后者要把该能力自己的 owner 判断放开，那是逐 power 的活。
/// </para>
/// </remarks>
internal static class PowerMirror
{
    /// <summary>能力的镜像策略。</summary>
    public enum PowerMirrorPolicy
    {
        /// <summary>每人一份，等价原版多人局。</summary>
        Mirror,

        /// <summary>只留一份。</summary>
        SingleInstance,
    }

    /// <summary>逐能力策略覆写表，默认 <see cref="PowerMirrorPolicy.Mirror" />。</summary>
    private static readonly Dictionary<Type, PowerMirrorPolicy> Overrides = new()
    {
        [typeof(PanachePower)] = PowerMirrorPolicy.SingleInstance,
        [typeof(AfterimagePower)] = PowerMirrorPolicy.SingleInstance,
    };

    private static int _mirrorDepth;

    /// <summary>
    /// "这是镜像出来的副本"的标记表。
    /// </summary>
    /// <remarks>
    /// 用弱键的 <see cref="ConditionalWeakTable{TKey, TValue}" />：副本被回收时条目自动消失，不用手工清理。
    /// <b>为什么需要标记</b>：判断"别人身上有没有对应副本"是不行的——回合末第一个结算的副本会把自己
    /// <b>和其他镜像一起删掉</b>，等派发轮到别的副本时"对应副本"已经空了，于是它照样又结算一次
    /// （实测就是临时敏捷多掉一份、减益多掉一层）。标记是跟着对象走的，不受这种时序影响。
    /// </remarks>
    private static readonly ConditionalWeakTable<PowerModel, object> MirrorCopies = new();

    private static readonly object MirrorMarker = new();

    /// <summary>这份能力是不是从别人身上镜像出来的副本。</summary>
    internal static bool IsMirrorCopy(PowerModel power)
    {
        return MirrorCopies.TryGetValue(power, out _);
    }

    internal static PowerMirrorPolicy PolicyOf(PowerModel power)
    {
        return Overrides.TryGetValue(power.GetType(), out var policy)
            ? policy
            : PowerMirrorPolicy.Mirror;
    }

    // ======================================================================
    // "同一个效果打到组里其他成员"的识别
    // ======================================================================

    /// <summary>一次"效果"的记录：同类型 + 同施加者 + 同层数，以及已经被它命中的成员。</summary>
    private sealed class ApplicationRecord
    {
        public Type? Type { get; set; }

        public Creature? Applier { get; set; }

        public decimal Amount { get; set; }

        public HashSet<Creature> Targets { get; } = [];

        public long Ticks { get; set; }
    }

    private static readonly Lock ApplicationGate = new();

    private static ApplicationRecord? _application;

    /// <summary>认"同一个效果的第二次命中"的时间窗（本体逐个目标 Apply，两次之间只隔一点特效等待）。</summary>
    private const long SameEffectWindowMs = 5000;

    /// <summary>
    /// 记下"这个效果刚命中了某个成员"。
    /// </summary>
    /// <remarks>
    /// 同类型 + 同施加者 + 同层数再次出现时：目标已经在名单里 = 这是**新一轮**效果（重置名单）；
    /// 目标是组里<b>还没被命中过的</b>成员 = 同一个效果继续打到别人身上（只加名单）。
    /// 这样 2~4 人的 AoE 减益都只会算一次。
    /// </remarks>
    internal static void RecordApplication(PowerModel power, decimal amount, Creature? applier, Creature target)
    {
        if (!TogetherPair.IsActive || !TogetherPair.IsMember(target.Player))
        {
            return;
        }

        lock (ApplicationGate)
        {
            var now = Environment.TickCount64;
            var record = _application;

            var sameEffect = record is not null
                             && record.Type == power.GetType()
                             && ReferenceEquals(record.Applier, applier)
                             && record.Amount == amount
                             && now - record.Ticks <= SameEffectWindowMs
                             && !record.Targets.Contains(target);

            if (sameEffect && record is not null && record.Targets.Count < TogetherPair.MemberCount)
            {
                record.Targets.Add(target);
                record.Ticks = now;
                return;
            }

            var fresh = new ApplicationRecord
            {
                Type = power.GetType(),
                Applier = applier,
                Amount = amount,
                Ticks = now,
            };
            fresh.Targets.Add(target);
            _application = fresh;
        }
    }

    /// <summary>
    /// 这次施加是不是"同一个效果紧接着打到组里<b>另一个</b>成员"（是的话应当整个忽略）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 典型场景：同族神官的"脆弱宝珠"是 <c>PowerCmd.Apply&lt;FrailPower&gt;(…, targets, 1, 怪物, null)</c>——
    /// 一次调用、目标里带着所有玩家。共享身体只有一副，这个减益<b>只该算一次</b>；
    /// 但本体是逐个目标 Apply 的：先给锚点上 1 层，镜像把它克隆给其他成员，
    /// 紧接着那些成员又被 Apply 一次——而它们身上已经有我们的克隆副本了，
    /// 于是本体走"已有实例 → 加层数"这条路，变成 2 层（实测"减益双倍"）。
    /// </para>
    /// <para>
    /// 判定用"类型 + 施加者 + 层数 + 目标是不是组里另一个还没被这次效果命中过的成员 + 时间窗"：
    /// 同一位玩家连打两张同名卡时施加者不同、下个回合再被同一种减益打一次则是"新一轮效果"，都不会被误吞。
    /// </para>
    /// </remarks>
    internal static bool IsSecondHitOfSameEffect(PowerModel power, decimal amount, Creature? applier, Creature target)
    {
        if (!TogetherPair.IsActive || !TogetherPair.IsMember(target.Player))
        {
            return false;
        }

        lock (ApplicationGate)
        {
            var record = _application;
            if (record is null
                || record.Type != power.GetType()
                || !ReferenceEquals(record.Applier, applier)
                || record.Amount != amount
                || Environment.TickCount64 - record.Ticks > SameEffectWindowMs)
            {
                return false;
            }

            if (record.Targets.Contains(target) || record.Targets.Count >= TogetherPair.MemberCount)
            {
                // 同一个成员再次被施加 / 这次效果已经覆盖了全组 → 属于新一轮，不吞。
                return false;
            }

            record.Targets.Add(target);
            record.Ticks = Environment.TickCount64;

            CappedLog.Info(
                "power.second_hit",
                $"忽略同一效果的重复命中：{power.GetType().Name} 层数={amount} 目标={target.LogName}");

            return true;
        }
    }

    // ======================================================================
    // 镜像本体
    // ======================================================================

    internal static void OnPowerApplied(Creature owner, PowerModel power)
    {
        if (_mirrorDepth > 0
            || !TogetherPair.IsActive
            || PolicyOf(power) != PowerMirrorPolicy.Mirror
            || owner.Player is not { } player
            || !TogetherPair.IsMember(player))
        {
            return;
        }

        _mirrorDepth++;
        try
        {
            foreach (var other in owner.OthersOrEmpty())
            {
                // MutableClone 会深拷 DynamicVars、把 _internalData 重新初始化、并把 _owner 置空，
                // 所以克隆出来就是一份干净的、可以直接挂到另一个 creature 上的实例。
                if (power.MutableClone() as PowerModel is not { } clone)
                {
                    continue;
                }

                clone.ApplyInternal(other, power.Amount, silent: true);

                // 打上"我是镜像副本"的标记（见 IsMirrorCopy 的注释）。
                MirrorCopies.Add(clone, MirrorMarker);

                // 本体对玩家侧的减益会顺手设 SkipNextDurationTick（"上减益的这一回合先不掉层"）。
                // 克隆体是在那行之前造好的，得自己补上，否则共享身体上的减益会比原版多掉一层。
                clone.SkipNextDurationTick = power.SkipNextDurationTick
                                             || (other.Side == CombatSide.Player && power.Type == PowerType.Debuff);
            }

            // 记一笔，供"同一个效果打到组里其他人"的判定使用。
            RecordApplication(power, power.Amount, power.Applier, owner);
        }
        finally
        {
            _mirrorDepth--;
        }
    }

    internal static void OnPowerRemoved(Creature owner, PowerModel power)
    {
        if (_mirrorDepth > 0
            || !TogetherPair.IsActive
            || PolicyOf(power) != PowerMirrorPolicy.Mirror
            || owner.Player is not { } player
            || !TogetherPair.IsMember(player))
        {
            return;
        }

        _mirrorDepth++;
        try
        {
            foreach (var other in owner.OthersOrEmpty())
            {
                FindCounterpart(owner, other, power)?.RemoveInternal();
            }
        }
        finally
        {
            _mirrorDepth--;
        }
    }

    internal static void OnPowerAmountChanged(Creature owner, PowerModel power, bool silent)
    {
        if (_mirrorDepth > 0
            || !TogetherPair.IsActive
            || PolicyOf(power) != PowerMirrorPolicy.Mirror
            || owner.Player is not { } player
            || !TogetherPair.IsMember(player))
        {
            return;
        }

        _mirrorDepth++;
        try
        {
            foreach (var other in owner.OthersOrEmpty())
            {
                if (FindCounterpart(owner, other, power) is { } counterpart
                    && counterpart.Amount != power.Amount)
                {
                    counterpart.SetAmount(power.Amount, silent);
                }
            }
        }
        finally
        {
            _mirrorDepth--;
        }
    }

    /// <summary>
    /// 在 <paramref name="to" /> 身上找 <paramref name="power" /> 对应的"第 N 份副本"。
    /// </summary>
    /// <remarks>
    /// <c>PowerInstanceType.Instanced</c> 的能力可以有多个同类型实例，
    /// 所以按（类型，同类型内第几个）配对，而不是按类型唯一匹配。
    /// </remarks>
    private static PowerModel? FindCounterpart(Creature from, Creature to, PowerModel power)
    {
        var index = 0;
        foreach (var candidate in from.Powers)
        {
            if (ReferenceEquals(candidate, power))
            {
                break;
            }

            if (candidate.GetType() == power.GetType())
            {
                index++;
            }
        }

        var seen = 0;
        foreach (var candidate in to.Powers)
        {
            if (candidate.GetType() != power.GetType())
            {
                continue;
            }

            if (seen == index)
            {
                return candidate;
            }

            seen++;
        }

        return null;
    }
}

[HarmonyPatch(typeof(Creature), nameof(Creature.ApplyPowerInternal))]
internal static class PowerAppliedMirrorPatch
{
    [HarmonyPostfix]
    private static void Postfix(Creature __instance, PowerModel __0)
    {
        PowerMirror.OnPowerApplied(__instance, __0);
    }
}

[HarmonyPatch(typeof(Creature), nameof(Creature.RemovePowerInternal))]
internal static class PowerRemovedMirrorPatch
{
    [HarmonyPostfix]
    private static void Postfix(Creature __instance, PowerModel __0)
    {
        PowerMirror.OnPowerRemoved(__instance, __0);
    }
}

[HarmonyPatch(typeof(Creature), nameof(Creature.InvokePowerModified))]
internal static class PowerAmountMirrorPatch
{
    [HarmonyPostfix]
    private static void Postfix(Creature __instance, PowerModel __0, bool __2)
    {
        PowerMirror.OnPowerAmountChanged(__instance, __0, __2);
    }
}

/// <summary>
/// 叠层路径：同一个效果打到组里其他成员时整个忽略。
/// </summary>
/// <remarks>
/// AoE 减益的后续命中走的是"已有实例 → 加层数"这条路（<c>ModifyAmount</c>），
/// 不拦的话共享身体上的层数会翻倍。前缀同时负责"记一笔"，让下一次调用能认出同组成员。
/// </remarks>
[HarmonyPatch(typeof(PowerCmd), nameof(PowerCmd.ModifyAmount), new[]
{
    typeof(PlayerChoiceContext), typeof(PowerModel), typeof(decimal),
    typeof(Creature), typeof(CardModel), typeof(bool),
})]
internal static class PowerSecondHitModifyAmountPatch
{
    [HarmonyPrefix]
    private static bool Prefix(PowerModel __1, decimal __2, Creature? __3, ref Task<int> __result)
    {
        if (PowerMirror.IsSecondHitOfSameEffect(__1, __2, __3, __1.Owner))
        {
            __result = Task.FromResult(0);
            return false;
        }

        PowerMirror.RecordApplication(__1, __2, __3, __1.Owner);
        return true;
    }
}

/// <summary>
/// 新建实例路径：同样是"同一个效果的另一半/其他人"就整个忽略
/// （万一镜像没成功、别人身上还没有副本，这一步能避免本体再加一份实例）。
/// </summary>
[HarmonyPatch(typeof(PowerCmd), nameof(PowerCmd.Apply), new[]
{
    typeof(PlayerChoiceContext), typeof(PowerModel), typeof(Creature), typeof(decimal),
    typeof(Creature), typeof(CardModel), typeof(bool),
})]
internal static class PowerSecondHitApplyPatch
{
    [HarmonyPrefix]
    private static bool Prefix(PowerModel __1, Creature __2, decimal __3, Creature? __4, ref Task __result)
    {
        if (PowerMirror.IsSecondHitOfSameEffect(__1, __3, __4, __2))
        {
            __result = Task.CompletedTask;
            return false;
        }

        return true;
    }
}
