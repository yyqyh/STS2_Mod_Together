using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using Together.Core.Utils;

namespace Together.Core.Combat;

/// <summary>
/// 怪物招式的"目标折叠"：<b>每个共生体组在一次招式里只算一个目标</b>。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要：本体怪物招式的入参是 <c>combatState.PlayerCreatures</c>（<b>全体玩家</b>），
/// 而共享身体下这些 creature 共用一份血量/卡组/球位。于是任何"按 target 遍历去做副作用"的招式
/// 都会把同一份共享资源处理 N 次：
/// </para>
/// <list type="bullet">
/// <item><description>
/// 沙漏（<c>Aeonglass</c>）的 <c>IncreasingIntensityMove</c>：<c>foreach target → AllCards.FakeUpgrade()</c>
/// 会把共享牌堆里的每张凋萎<b>升 N 级</b>，而 <c>WitherUpgradeCount</c> 只 +1 →
/// "凋萎等级不一致"。
/// </description></item>
/// <item><description>
/// 魔骑士（<c>MagiKnight</c>）的 <c>DampenMove</c>、以及 47 处 <c>PowerCmd.Apply(..., targets, ...)</c>：
/// 每个成员各上一次 + 我们的能力镜像再分发一次 → <b>数值翻倍</b>。
/// </description></item>
/// </list>
/// <para>
/// 折叠规则：把入参里"属于同一共生体组"的 creature 收敛成<b>一个代表（锚点）</b>；
/// 不在这局配对里的其他玩家（3~4 人局）原样保留。这样：
/// </para>
/// <list type="bullet">
/// <item><description>遍历共享资源的招式只做一次 ✔</description></item>
/// <item><description>挂在 creature 上的能力只上一次，再由镜像分发到其他成员 ✔</description></item>
/// <item><description>攻击伤害<b>不受影响</b>：<c>FromMonster</c> 走的是
/// <c>AttackCommand.GetPossibleTargets() → PlayerCreatures</c>，绕开这里的 targets ✔</description></item>
/// </list>
/// <para>
/// <b>量不砍半</b>：折叠会让"每个 target 各来一份"的量变成 1 份，而原版多人局里这个量是按人数累加的
/// （例如沙漏的塞牌数、噪声机往共享堆塞的状态牌）。所以对<b>生成进战斗的牌</b>做按组补齐
/// （见 <see cref="CompensateAsync" />）：共享堆补 N-1 份副本，手牌则每个成员各一份。
/// </para>
/// <para>
/// 唯一特例：<b>KnowledgeDemon</b> 的 <c>CurseOfKnowledgeMove</c> 是"每个玩家各自做一次玩家选择"，
/// 折叠会直接丢掉第二个人的选卡界面。本体里只有它一个怪招带玩家选择，所以按用户确认的方式单独排除。
/// </para>
/// </remarks>
internal static class MonsterTargetFold
{
    /// <summary>
    /// 不参与折叠的怪物判据（本体里唯一一个"每玩家各选一次"的怪招）。
    /// </summary>
    /// <remarks>
    /// 同时按"类型名（含基类链）"和"内容 ID"匹配：本体的可变克隆走 <c>MemberwiseClone</c>，
    /// 类型名会保留；而 mod 若派生出自己的知识恶魔，基类链里也仍然带着 <c>KnowledgeDemon</c>。
    /// </remarks>
    private const string ExcludedMarker = "KnowledgeDemon";

    private const string ExcludedIdMarker = "KNOWLEDGE_DEMON";

    /// <summary>一次怪物招式的作用域。</summary>
    internal sealed class Scope
    {
        public required string Monster { get; init; }

        /// <summary>组内成员（锚点在前），用于按组补齐份数。</summary>
        public required IReadOnlyList<Player> Members { get; init; }

        /// <summary>本次招式实际收到的目标（诊断用）。</summary>
        public required IReadOnlyList<Creature> All { get; init; }

        /// <summary>折叠后的目标。过期后由 <see cref="Fold" /> 覆盖。</summary>
        public IReadOnlyList<Creature> Folded { get; set; } = [];

        /// <summary>招式跑完就置 false；用它做开关而不是清 AsyncLocal，避免时序问题。</summary>
        public bool Active { get; set; } = true;

        /// <summary>
        /// 本次招式是否<b>真的</b>折叠过目标。
        /// </summary>
        /// <remarks>
        /// 按组补齐必须以此为前提：万一折叠那条补丁没装上（Harmony 拒绝、本体改签名……），
        /// 目标仍是"每个成员各一个"，再补一份就变成三份了。这里用"确实折过"当开关，
        /// 补丁装不上时整套逻辑自动退化成原版行为。
        /// </remarks>
        public bool FoldApplied { get; set; }
    }

    private static readonly AsyncLocal<Scope?> Current = new();

    /// <summary>当前生效的招式作用域；不在招式里（或已结束）时返回 null。</summary>
    public static Scope? Active => Current.Value is { Active: true } scope ? scope : null;

    /// <summary>
    /// 便宜的门控：补丁在"包装返回的 Task 之前"先问一次，避免单人局白白多包一层异步。
    /// </summary>
    /// <remarks>
    /// 三个补偿挂点（生成入口 + 两个 PileType 入口）都是"无条件包装 Task"的形状，
    /// 而 <c>CardPileCmd.Add</c> 在整局里被调用得极其频繁；先做一次 <see cref="AsyncLocal{T}" /> 读
    /// （几十纳秒）比每次分配一个异步状态机划算得多。
    /// </remarks>
    public static bool CompensationActive => Active is { FoldApplied: true };

    /// <summary>补份数的重入守卫：我们自己补出来的那些副本不该再触发一次补齐。</summary>
    private static bool _compensating;

    /// <summary>
    /// 进入招式作用域。不需要折叠时返回 null（调用方据此跳过收尾动作）。
    /// </summary>
    public static Scope? Enter(MonsterModel? monster, IReadOnlyList<Creature> targets)
    {
        if (!TogetherPair.IsActive || monster is null || targets.Count == 0)
        {
            return null;
        }

        if (IsExcluded(monster))
        {
            CappedLog.Info(
                "monster.fold_skip",
                $"招式折叠：跳过 {monster.GetType().Name}（本体唯一一个每玩家各选一次的怪招）");
            return null;
        }

        var members = TogetherPair.Members().ToList();
        if (members.Count < TogetherPair.MinMembers || TogetherPair.Anchor?.Creature is not { } anchorCreature)
        {
            return null;
        }

        var scope = new Scope
        {
            Monster = monster.GetType().Name,
            Members = members,
            All = targets,
            Folded = BuildFolded(targets, anchorCreature),
        };

        Current.Value = scope;

        if (scope.Folded.Count != targets.Count)
        {
            CappedLog.Info(
                "monster.fold",
                $"招式折叠：{scope.Monster} 目标 {targets.Count} → {scope.Folded.Count}"
                + $"（组内 {members.Count} 人共用一个目标）");
        }

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

    /// <summary>把 <paramref name="scope" /> 的收尾挂到招式任务结束之后。</summary>
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

    /// <summary>把目标列表折叠成"每组一个代表"。不在招式里时原样返回。</summary>
    public static IEnumerable<Creature> Fold(IEnumerable<Creature> targets)
    {
        if (Active is not { } scope)
        {
            return targets;
        }

        var incoming = targets as IReadOnlyList<Creature> ?? targets.ToList();
        if (TogetherPair.Anchor?.Creature is not { } anchorCreature)
        {
            return incoming;
        }

        scope.Folded = BuildFolded(incoming, anchorCreature);
        scope.FoldApplied = scope.Folded.Count != incoming.Count;
        return scope.Folded;
    }

    /// <summary>
    /// 视觉类接口（<c>VfxCmd.PlayOnCreatures</c> 等）把目标还原成全部成员：
    /// 表现不该因为折叠而少画。
    /// </summary>
    public static IEnumerable<Creature> ExpandForVisuals(IEnumerable<Creature> targets)
    {
        if (Active is not { FoldApplied: true } scope)
        {
            return targets;
        }

        var expanded = targets.ToList();
        foreach (var member in scope.Members)
        {
            if (member.Creature is { } creature && !expanded.Contains(creature))
            {
                expanded.Add(creature);
            }
        }

        return expanded;
    }

    /// <summary>
    /// 折叠作用域内"生成进战斗"的牌按组补齐份数。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 原版多人局里，一个"给每个玩家塞 N 张"的招式在整组层面的总量是 <c>N × 人数</c>。
    /// 折叠之后只剩一个目标，如果不补齐就会砍半（沙漏的凋萎张数就是这么掉下去的）。
    /// </para>
    /// <para>
    /// 补齐规则：
    /// </para>
    /// <list type="bullet">
    /// <item><description>目标是<b>共享战斗堆</b>（抽/弃/消耗/出牌/卡组）→ 再补 N-1 份同样的副本。</description></item>
    /// <item><description>目标是<b>手牌</b> → 每个成员各一份，副本归属改成对应成员（各自拿到自己的那张）。</description></item>
    /// </list>
    /// </remarks>
    public static async Task<CardPileAddResult> CompensateAsync(
        Task<CardPileAddResult> original,
        CardModel card,
        PileType pileType,
        Player? creator,
        CardPilePosition position)
    {
        var result = await original;

        if (_compensating
            || Active is not { FoldApplied: true } scope
            || !result.success
            || card is null)
        {
            return result;
        }

        var owners = ExtraOwners(scope, pileType, card);
        if (owners.Count == 0)
        {
            return result;
        }

        if (!await AddCloneCopiesAsync(card, pileType, position, owners, creator, asGenerated: true, targetClonedBy: null))
        {
            return result;
        }

        LogFill(scope, pileType, card, owners.Count);

        return result;
    }

    /// <summary>
    /// 折叠作用域内，把"刚生成、但没走生成入口"的牌按组还原份数。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 为什么单独需要它：本体的生成入口 <c>AddGeneratedCardToCombat(s)</c> 走的是
    /// <c>CardPileCmd.Add</c> 的 <b>CardPile 重载</b>（已由 <see cref="CompensateAsync" /> 处理）；
    /// 而"自己 <c>CreateCard</c> 完再塞进牌堆"的写法走的是 <b>PileType 重载</b>，原来没人补。
    /// 两条路互不重叠，所以不会重复计数。
    /// </para>
    /// <para>
    /// <b>只补"这次刚进战斗"的牌</b>（<c>oldPile == null</c>）：洗牌、出牌结果堆、弃牌这些
    /// "移动已有牌"的操作一律不碰 —— 那不是"每目标各一份"的量，克隆它只会凭空造牌。
    /// </para>
    /// </remarks>
    public static async Task<CardPileAddResult> CompensateFreshCardAsync(
        Task<CardPileAddResult> original,
        PileType pileType,
        CardPilePosition position,
        AbstractModel? clonedBy)
    {
        var result = await original;
        await CompensateFreshAsync([result], pileType, position, clonedBy);
        return result;
    }

    /// <summary>批量版本；见 <see cref="CompensateFreshCardAsync" />。</summary>
    public static async Task<IReadOnlyList<CardPileAddResult>> CompensateFreshCardsAsync(
        Task<IReadOnlyList<CardPileAddResult>> original,
        PileType pileType,
        CardPilePosition position,
        AbstractModel? clonedBy)
    {
        var results = await original;
        await CompensateFreshAsync(results, pileType, position, clonedBy);
        return results;
    }

    private static async Task CompensateFreshAsync(
        IReadOnlyList<CardPileAddResult> results,
        PileType pileType,
        CardPilePosition position,
        AbstractModel? clonedBy)
    {
        if (_compensating
            || Active is not { FoldApplied: true } scope
            || !pileType.IsCombatPile())
        {
            return;
        }

        var extra = 0;
        CardModel? sample = null;

        foreach (var result in results)
        {
            if (!result.success || result.oldPile is not null || result.cardAdded is not { } card)
            {
                continue;
            }

            var owners = ExtraOwners(scope, pileType, card);
            if (owners.Count == 0)
            {
                continue;
            }

            if (!await AddCloneCopiesAsync(
                    card, pileType, position, owners, creator: null, asGenerated: false, clonedBy))
            {
                return;
            }

            extra += owners.Count;
            sample ??= card;
        }

        if (extra > 0 && sample is not null)
        {
            LogFill(scope, pileType, sample, extra);
        }
    }

    /// <summary>按 <paramref name="owners" /> 补副本；返回 false 表示中途失败（已记日志）。</summary>
    private static async Task<bool> AddCloneCopiesAsync(
        CardModel card,
        PileType pileType,
        CardPilePosition position,
        IReadOnlyList<Player?> owners,
        Player? creator,
        bool asGenerated,
        AbstractModel? targetClonedBy)
    {
        if (card.CombatState is not { } combatState)
        {
            return false;
        }

        foreach (var owner in owners)
        {
            CardModel clone;
            try
            {
                clone = combatState.CloneCard(card);
                if (owner is not null && !ReferenceEquals(clone.Owner, owner))
                {
                    clone.GiveToAnotherPlayer(owner);
                }
            }
            catch (Exception ex)
            {
                Main.Logger.Warn($"[together] 招式折叠补齐：克隆卡牌失败（{ex.GetType().Name}: {ex.Message}）");
                return false;
            }

            _compensating = true;
            try
            {
                // 走哪条入口取决于"原始那张"走的是哪条：生成入口的副本也走生成入口（钩子对称），
                // 普通 Add 的副本也走普通 Add（不引入本体没有的"生成"钩子）。
                if (asGenerated)
                {
                    await CardPileCmd.AddGeneratedCardToCombat(clone, pileType, creator, position);
                }
                else
                {
                    await CardPileCmd.Add(clone, pileType, position, targetClonedBy);
                }
            }
            catch (Exception ex)
            {
                Main.Logger.Warn($"[together] 招式折叠补齐：加牌失败（{ex.GetType().Name}: {ex.Message}）");
                return false;
            }
            finally
            {
                _compensating = false;
            }
        }

        return true;
    }

    /// <summary>
    /// 措辞刻意写成"折叠后 X 份 + 按组还原 Y 份 = Z 份"，避免读成"临时打补丁"。
    /// </summary>
    private static void LogFill(Scope scope, PileType pileType, CardModel card, int extra)
    {
        CappedLog.Info(
            "monster.fold_fill",
            pileType == PileType.Hand
                ? $"招式折叠：{scope.Monster} 往手牌塞 {card.Id.Entry}"
                  + $" —— 组内 {scope.Members.Count} 人各 1 份"
                : $"招式折叠：{scope.Monster} 往 {pileType} 塞 {card.Id.Entry}"
                  + $" —— 折叠后 1 份 + 按组还原 {extra} 份 = {extra + 1} 份"
                  + $"（组内 {scope.Members.Count} 人）");
    }

    /// <summary>枚举"还需要补哪些归属的副本"。<c>null</c> 表示沿用原卡的归属。</summary>
    private static IReadOnlyList<Player?> ExtraOwners(Scope scope, PileType pileType, CardModel card)
    {
        var count = scope.Members.Count;
        if (count <= 1)
        {
            return [];
        }

        if (pileType == PileType.Hand)
        {
            return scope.Members
                .Where(member => !ReferenceEquals(member, card.Owner))
                .Cast<Player?>()
                .ToList();
        }

        return Enumerable.Repeat<Player?>(null, count - 1).ToList();
    }

    private static IReadOnlyList<Creature> BuildFolded(IReadOnlyList<Creature> targets, Creature anchorCreature)
    {
        var folded = new List<Creature>(targets.Count);
        var anchorAdded = false;

        foreach (var target in targets)
        {
            if (TogetherPair.IsMember(target.Player))
            {
                if (!anchorAdded)
                {
                    folded.Add(anchorCreature);
                    anchorAdded = true;
                }

                continue;
            }

            folded.Add(target);
        }

        return folded;
    }

    /// <summary>类型名（含基类链）或内容 ID 命中即视为需要排除。</summary>
    private static bool IsExcluded(MonsterModel monster)
    {
        for (var type = monster.GetType(); type is not null; type = type.BaseType)
        {
            if (type.Name.Contains(ExcludedMarker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        try
        {
            return monster.Id.Entry.Contains(ExcludedIdMarker, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
