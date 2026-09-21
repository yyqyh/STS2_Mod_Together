using System.Runtime.CompilerServices;

using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models;
using Together.Core.Settings;
using Together.Core.Utils;

namespace Together.Core.Combat;

// ======================================================================
// 合并自 Core/Multiplayer/BodyMirror.cs（2026-09-21 合并文件，正文未改动）
// ======================================================================
/// <summary>
/// 共享身体（生命 / 最大生命 / 格挡）的镜像逻辑（不含补丁特性）。
/// </summary>
/// <remarks>
/// <para>
/// 引擎强制每个玩家各有一个 <c>Creature</c>（<c>CombatState.Players</c> 是从
/// <c>PlayerCreatures.Select(c =&gt; c.Player)</c> 反推的），所以"一个身体"是靠**镜像**实现的：
/// 谁的值变了，就把新值推给组里**其他所有成员**（共生体支持 2~4 人）。
/// </para>
/// <para>
/// 三个值的收口都是同一种形状——<c>Block</c> / <c>CurrentHp</c> / <c>MaxHp</c> 的 private setter，
/// 且都带 <c>if (旧值 != 新值)</c> 判断。补丁点唯一完备（构造函数是直接写字段、不走 setter），
/// 而且天然收敛：值相等时 setter 直接返回，互相推不会无限递归（另有深度守卫兜底）。
/// </para>
/// <para>
/// 于是"几个人同时起相同护甲"不需要任何特殊规则：各人打防御各加各的，池子累加，
/// 正好等于原版多人局几人合计的格挡，而敌人打的也是同一个池。原版卡一行都不用改。
/// </para>
/// </remarks>
internal static class BodyMirror
{
    /// <summary>重入守卫：镜像动作自己会再触发 setter，用深度计数把手挡掉。</summary>
    private static int _mirrorDepth;

    /// <summary>把锚点的三个数值整份推给组里其他成员（战斗状态重建、读档后对齐、血量提升后用）。</summary>
    public static void SyncAll()
    {
        if (TogetherPair.Anchor?.Creature is not { } from)
        {
            return;
        }

        _mirrorDepth++;
        try
        {
            foreach (var to in from.OthersOrEmpty())
            {
                PushMaxHp(from, to);
                PushHp(from, to);
                PushBlock(from, to);
            }
        }
        finally
        {
            _mirrorDepth--;
        }
    }

    internal static void MirrorBlock(Creature source, Creature target)
    {
        Mirror(() => PushBlock(source, target));
    }

    internal static void MirrorHp(Creature source, Creature target)
    {
        Mirror(() =>
        {
            PushMaxHp(source, target);
            PushHp(source, target);

            // 只可能出现在"镜像失手"的边缘态——一个死了另一个还活着。
            RepairImpossibleLifeState(source, target);
        });
    }

    internal static void MirrorMaxHp(Creature source, Creature target)
    {
        Mirror(() => PushMaxHp(source, target));
    }

    private static void Mirror(Action action)
    {
        if (_mirrorDepth > 0)
        {
            return;
        }

        _mirrorDepth++;
        try
        {
            action();
        }
        finally
        {
            _mirrorDepth--;
        }
    }

    private static void PushMaxHp(Creature from, Creature to)
    {
        if (from.MaxHp != to.MaxHp)
        {
            to.SetMaxHpInternal(from.MaxHp);
        }
    }

    private static void PushHp(Creature from, Creature to)
    {
        if (from.CurrentHp != to.CurrentHp)
        {
            to.SetCurrentHpInternal(from.CurrentHp);
        }
    }

    private static void PushBlock(Creature from, Creature to)
    {
        if (from.Block == to.Block)
        {
            return;
        }

        if (from.Block > to.Block)
        {
            to.GainBlockInternal(from.Block - to.Block);
        }
        else
        {
            to.LoseBlockInternal(to.Block - from.Block);
        }
    }

    /// <summary>
    /// 一个死、一个活是<b>不可能态</b>（共享血池意味着所有人的血量永远相等、一起死）。
    /// 真出现时说明镜像漏了一步，这里做修复：把低的一方拉平到高的一方。
    /// </summary>
    /// <remarks>
    /// 用 <c>HealInternal</c> 而不是直接写字段：它会走"从死到活"的正式流程
    /// （<c>Player.ActivateHooks()</c> + <c>Revived</c> 事件），否则复活的玩家钩子仍然是关的。
    /// </remarks>
    private static void RepairImpossibleLifeState(Creature from, Creature to)
    {
        if (from.IsDead == to.IsDead)
        {
            return;
        }

        var alive = from.IsDead ? to : from;
        var dead = from.IsDead ? from : to;

        Log.Error(
            $"[together] 共享血池出现一死一活（{alive.LogName}={alive.CurrentHp} / {dead.LogName}={dead.CurrentHp}），"
            + "按共享池语义拉平。这通常意味着有一处伤害没有走镜像收口，请带 log 反馈。");

        dead.HealInternal(alive.CurrentHp - dead.CurrentHp);
    }
}

[HarmonyPatch(typeof(Creature), "Block", MethodType.Setter)]
internal static class BlockMirrorPatch
{
    [HarmonyPostfix]
    private static void Postfix(Creature __instance)
    {
        foreach (var other in __instance.OthersOrEmpty())
        {
            BodyMirror.MirrorBlock(__instance, other);
        }
    }
}

[HarmonyPatch(typeof(Creature), "CurrentHp", MethodType.Setter)]
internal static class CurrentHpMirrorPatch
{
    [HarmonyPostfix]
    private static void Postfix(Creature __instance)
    {
        foreach (var other in __instance.OthersOrEmpty())
        {
            BodyMirror.MirrorHp(__instance, other);
        }
    }
}

[HarmonyPatch(typeof(Creature), "MaxHp", MethodType.Setter)]
internal static class MaxHpMirrorPatch
{
    [HarmonyPostfix]
    private static void Postfix(Creature __instance)
    {
        foreach (var other in __instance.OthersOrEmpty())
        {
            BodyMirror.MirrorMaxHp(__instance, other);
        }
    }
}

// ======================================================================
// 合并自 Core/Multiplayer/PowerMirror.cs（2026-09-21 合并文件，正文未改动）
// ======================================================================
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

// ======================================================================
// 合并自 Core/Multiplayer/GoldMirror.cs（2026-09-21 合并文件，正文未改动）
// ======================================================================
/// <summary>
/// 金币共享（可选）：组内只有一个钱包。
/// </summary>
/// <remarks>
/// <para>
/// 本体的金币收口只有一处 —— <c>Player.Gold</c> 的 setter（<c>PlayerCmd.GainGold</c> / <c>LoseGold</c> /
/// <c>SetGold</c> 最终都是给这个属性赋值，并且会触发 <c>GoldChanged</c> 让顶栏刷新）。
/// 所以这里只挂 setter 的 Postfix：谁的钱变了就把同一个数值推给组里其他人。
/// </para>
/// <para>
/// 收敛性靠本体自己的判断：<c>if (value != Gold)</c> 才赋值，所以"值相同就不再触发"，不会来回推。
/// 另外还有一层重入守卫兜底。
/// </para>
/// <para>
/// <b>新开一局</b>时把所有人的起始金币<b>加起来</b>当共同余额（99 × 人数）；
/// 读档 / 重连只做"取最大值对齐"（存档里本来就是同一份，再求一次和就是每次重连都翻倍）。
/// 关掉这个开关时完全不管金币。
/// </para>
/// </remarks>
internal static class GoldMirror
{
    private static int _depth;

    /// <summary>
    /// 成组时对齐金币。
    /// </summary>
    /// <param name="isNewRun">
    /// 是不是新开一局。新局把所有人的起始金币<b>加起来</b>（99 × 人数）当共同余额；
    /// 读档 / 重连只取组内最大值对齐。
    /// </param>
    public static void OnArm(bool isNewRun)
    {
        if (!TogetherSettingsSync.EffectiveShareGold || !TogetherPair.IsActive)
        {
            return;
        }

        var members = TogetherPair.Members().ToList();
        if (members.Count < 2)
        {
            return;
        }

        // 关键：只有新开一局才"合并"（求和）。读档再求一次和 = 每次重连都翻倍。
        var shared = isNewRun
            ? members.Sum(member => member.Gold)
            : members.Max(member => member.Gold);

        _depth++;
        try
        {
            foreach (var member in members)
            {
                if (member.Gold != shared)
                {
                    member.Gold = shared;
                }
            }
        }
        finally
        {
            _depth--;
        }

        Log.Info(
            $"[together] 共生体金币{(isNewRun ? "合并" : "对齐")}：{members.Count} 人"
            + $"（{(isNewRun ? "起始金币求和" : "取最大值对齐")}）→ 共同余额 {shared}");
    }

    internal static void OnGoldChanged(Player player)
    {
        if (_depth > 0 || !TogetherSettingsSync.EffectiveShareGold)
        {
            return;
        }

        if (!TogetherPair.IsMember(player))
        {
            // 诊断：如果商店/事件拿的是"过期的 Player 对象"，这里会刷出来，一看就知道是对象引用的问题。
            CappedLog.Info(
                "gold.skip",
                $"金币变化没同步（这个对象不在当前共生体里）：netId={player.NetId} gold={player.Gold}");
            return;
        }

        var synced = 0;
        _depth++;
        try
        {
            foreach (var other in TogetherPair.OthersOf(player))
            {
                if (other.Gold != player.Gold)
                {
                    other.Gold = player.Gold;
                }

                synced++;
            }
        }
        finally
        {
            _depth--;
        }

        CappedLog.Info(
            "gold.changed",
            $"金币同步：netId={player.NetId} 变成 {player.Gold}（推给 {synced} 人）");
    }
}

/// <summary>金币变化的唯一收口：<c>Player.Gold</c> 的 setter。</summary>
[HarmonyPatch(typeof(Player), "Gold", MethodType.Setter)]
internal static class GoldMirrorPatch
{
    [HarmonyPostfix]
    private static void Postfix(Player __instance)
    {
        GoldMirror.OnGoldChanged(__instance);
    }
}

/// <summary>
/// 消费路径的双保险：<c>PlayerCmd.LoseGold</c>（商店买卡/删牌、事件扣钱都走它）。
/// </summary>
/// <remarks>
/// 本体所有金币变化的收口确实是 <c>Player.Gold</c> 的 setter，上面那个补丁理论上已经覆盖；
/// 这里再挂一条的原因很实际：<c>LoseGold</c> 是本体的"扣钱"语义入口，一旦将来有哪条扣钱路径
/// 绕过 setter（或者 setter 那条补丁因为别的原因没跑到），这条能兜住。
/// 两个补丁都是幂等的（值相同不会重复推）。
/// </remarks>
[HarmonyPatch(typeof(PlayerCmd), nameof(PlayerCmd.LoseGold))]
internal static class LoseGoldMirrorPatch
{
    [HarmonyPostfix]
    private static void Postfix(Player __1)
    {
        GoldMirror.OnGoldChanged(__1);
    }
}
