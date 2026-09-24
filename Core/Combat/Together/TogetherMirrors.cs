using System.Reflection;
using System.Runtime.CompilerServices;

using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using Together.Core.Settings;
using Together.Core.Utils;

namespace Together.Core.Combat;

/// <summary>
/// 共享身体（生命 / 最大生命 / 格挡）的镜像逻辑（不含补丁特性）。
/// </summary>
/// <remarks>
/// 引擎强制每人各有一个 <c>Creature</c>（<c>CombatState.Players</c> 由
/// <c>PlayerCreatures.Select(c =&gt; c.Player)</c> 反推），所以"一个身体"靠**镜像**实现：
/// 谁的值变了就推给组里其他所有成员（共生体支持 2~4 人）。
/// 三个值的收口形状一致（<c>Block</c> / <c>CurrentHp</c> / <c>MaxHp</c> 的 private setter +
/// <c>if (旧值 != 新值)</c>），补丁点唯一完备、天然收敛，深度守卫只是兜底。
/// 于是"几个人各起一份护甲"不需要特殊规则：池子累加，正好等于原版多人局的合计格挡。
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

    /// <summary>召唤物专用：只推生命上限 + 当前生命（不做"一死一活拉平"，也不推格挡）。</summary>
    internal static void MirrorPetHp(Creature source, Creature target)
    {
        Mirror(() =>
        {
            PushMaxHp(source, target);
            PushPetHp(source, target);
        });
    }

    internal static void MirrorPetMaxHp(Creature source, Creature target)
    {
        Mirror(() => PushMaxHp(source, target));
    }

    /// <summary>召唤物血量：死→活必须走 <c>HealInternal</c>（补 <c>Revived</c> 事件、界面靠它重新显示），
    /// 直接写字段只会数据活了、画面还是空的。</summary>
    private static void PushPetHp(Creature from, Creature to)
    {
        if (from.CurrentHp == to.CurrentHp)
        {
            return;
        }

        if (to.IsDead && from.IsAlive)
        {
            to.HealInternal(from.CurrentHp - to.CurrentHp);
            RefreshPetNode(to);
            return;
        }

        to.SetCurrentHpInternal(from.CurrentHp);
        RefreshPetNode(to);
    }

    /// <summary>
    /// 让<b>本机</b>的召唤物节点立刻显示"活着 + 血量"（对方召的 / 复活的也一样）。
    /// </summary>
    /// <remarks>
    /// 召唤物只有一个生物节点（共享战斗状态），但存活状态与大小是画面自己缓存的：只在数据层写血，
    /// 另一侧窗口可能还停在"隐藏/空血"。<b>必须延到帧末做</b>：血量 setter 在伤害结算的同步路径里，
    /// 直接动节点（Tween / 重设显示）= 在结算中间插一次 UI 操作 —— 实测（22:15 log）P2 打出第三张牌后
    /// 日志停在 "playing card …" 就没了。用 <c>CallDeferred</c> 后最坏只是晚一帧显示。
    /// 节点找不到时留一条诊断，用于判断"这台机器压根没建这只召唤物的节点"。
    /// </remarks>
    private static void RefreshPetNode(Creature pet)
    {
        try
        {
            Godot.Callable.From(() =>
            {
                try
                {
                    var node = NCombatRoom.Instance?.GetCreatureNode(pet);
                    if (node is null || !Godot.GodotObject.IsInstanceValid(node))
                    {
                        CappedLog.Info("summon.missing", $"本机没有召唤物节点（{pet.LogName}），血量只能在数据层同步");
                        return;
                    }

                    // 死掉的那只不跟血上限变大/缩小（实测：未复活的奥斯提曾跟着一起变大），显示交给"复活"流程。
                    if (pet.IsDead)
                    {
                        return;
                    }

                    node.OstyScaleToSize(pet.MaxHp, 0.2);
                }
                catch (Exception ex)
                {
                    CappedLog.Info("summon.missing", $"刷新召唤物节点失败（忽略）：{ex.GetType().Name}: {ex.Message}");
                }
            }).CallDeferred();
        }
        catch (Exception ex)
        {
            CappedLog.Info("summon.missing", $"安排召唤物节点刷新失败（忽略）：{ex.GetType().Name}: {ex.Message}");
        }
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
    /// 一死一活是<b>不可能态</b>（共享血池血量永远相等、一起死）；真出现说明镜像漏了一步，把低的一方拉平。
    /// </summary>
    /// <remarks>
    /// 用 <c>HealInternal</c> 而非直接写字段：它走"从死到活"的正式流程
    /// （<c>Player.ActivateHooks()</c> + <c>Revived</c>），否则复活的玩家钩子仍然是关的。
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

/// <summary>身体数值（格挡 / 当前生命 / 生命上限）的 setter 一改，就镜像给共生的另一半。</summary>
[HarmonyPatch]
internal static class BodyStatMirrorPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(Creature), "set_Block");
        yield return AccessTools.Method(typeof(Creature), "set_CurrentHp");
        yield return AccessTools.Method(typeof(Creature), "set_MaxHp");
    }

    [HarmonyPostfix]
    private static void Postfix(Creature __instance, MethodBase __originalMethod)
    {
        var setter = __originalMethod.Name;

        foreach (var other in __instance.OthersOrEmpty())
        {
            switch (setter)
            {
                case "set_Block":
                    BodyMirror.MirrorBlock(__instance, other);
                    break;

                case "set_CurrentHp":
                    BodyMirror.MirrorHp(__instance, other);
                    break;

                default:
                    BodyMirror.MirrorMaxHp(__instance, other);
                    break;
            }
        }

        // 召唤物（奥斯提这类"替你去死"的 pet）：两成员各一只，血量必须一致，否则一边死一边活
        // （打向主人的未格挡伤害会被本体改道到它身上）。只推血量/上限；pet 上的格挡是"主人的格挡"，本体自己画。
        // 召唤期间**不推**：这一波由本体自己处理，镜像插手会把"队友那次召唤"的结果抄回去（实测 5→5→5→10 翻倍）。
        if (setter is "set_CurrentHp" or "set_MaxHp"
            && !PetSummonFanoutPatch.IsFanningOut
            && SummonMirror.PartnerOf(__instance) is { } counterpart)
        {
            if (setter == "set_CurrentHp")
            {
                BodyMirror.MirrorPetHp(__instance, counterpart);
            }
            else
            {
                BodyMirror.MirrorPetMaxHp(__instance, counterpart);
            }
        }
    }
}

/// <summary>
/// 状态（powers）镜像的共享逻辑（不含补丁特性）。
/// </summary>
/// <remarks>
/// 关键事实：本体钩子是<b>全量广播</b>——<c>Hook.AfterCardPlayed</c> 就是
/// <c>foreach (var model in combatState.IterateHookListeners())</c>，
/// 每个能力都会收到战斗里发生的每一个事件，然后自己用 owner 判断要不要管，例如
/// <c>PanachePower</c> 里那句 <c>if (cardPlay.Card.Owner != Owner.Player) return;</c>。
/// <see cref="PowerMirrorPolicy.Mirror" />（默认）= 组里每个 creature 各挂一份、各数各的牌，等价原版多人局；
/// <see cref="PowerMirrorPolicy.SingleInstance" /> = 只留一份。
/// 注意后者只解决"只有一份"，不解决"这份要统计所有人的动作"（那要逐个放开该能力自己的 owner 判断）。
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

    /// <summary>
    /// 逐能力策略覆写表（纠错出口），默认 <see cref="PowerMirrorPolicy.Mirror" />，<b>现在是空的</b>：
    /// 带内部数据的能力（夜魇等）改由 <see cref="PowerPayload" /> 搬运，于是"所有能力都能镜像"。
    /// 只有实测某能力镜像后<b>语义确实不对</b>时才往这里加一条。
    /// </summary>
    private static readonly Dictionary<Type, PowerMirrorPolicy> Overrides = [];

    private static int _mirrorDepth;

    /// <summary>
    /// "这是镜像出来的副本"的标记表（弱键 <see cref="ConditionalWeakTable{TKey, TValue}" />，副本回收即自动消失）。
    /// </summary>
    /// <remarks>
    /// <b>不能用"别人身上有没有对应副本"来判断</b>：回合末第一个结算的副本会把自己和其他镜像一起删掉，
    /// 轮到别的副本时"对应副本"已经空了 → 它照样又结算一次（实测临时敏捷多掉一份）。标记跟着对象走，不受时序影响。
    /// </remarks>
    private static readonly ConditionalWeakTable<PowerModel, object> MirrorCopies = new();

    private static readonly object MirrorMarker = new();

    /// <summary>镜像副本 → 原件（派发前用它把原件的内部数据同步过来，见 <see cref="SyncMirrorPayload" />）。</summary>
    /// <remarks>
    /// 很多能力的数据是"施加<b>之后</b>"才由模型自己填的（夜魇的 <c>SetSelectedCard</c>），
    /// 而镜像发生在 Apply 内部，克隆那一刻只能拿到空数据。
    /// </remarks>
    private static readonly ConditionalWeakTable<PowerModel, PowerModel> MirrorSources = new();

    /// <summary>这份能力是不是从别人身上镜像出来的副本。</summary>
    internal static bool IsMirrorCopy(PowerModel power)
    {
        return MirrorCopies.TryGetValue(power, out _);
    }

    /// <summary>派发钩子前刷新镜像副本的内部数据（对非副本、无原件的模型是空操作）。</summary>
    internal static void SyncMirrorPayload(AbstractModel? model)
    {
        if (model is not PowerModel power || !MirrorSources.TryGetValue(power, out var source))
        {
            return;
        }

        PowerPayload.Sync(source, power);
    }

    /// <summary><paramref name="creator" /> 身上那个"产出了这张牌"的镜像副本（没有就返回 <c>null</c>）。</summary>
    /// <remarks>
    /// 用在"生成牌落到谁手里"的判定上（见 <c>GeneratedCardHandTargetPatch</c>）：镜像副本的内部数据是从原件
    /// 搬来的，里面记的牌<b>属于原件宿主</b>，而本体 <c>AddGeneratedCardToCombat</c> 是按 <c>card.Owner</c>
    /// 选目标堆的 —— 于是镜像副本产出的牌会跑进原件宿主的怀里（实测夜宴：p2 那份的 3 张复制牌全进 p1 手）。
    /// 判据看的是<b>耐久事实</b>（这份是镜像副本 + 载荷里真的引用着这张牌），
    /// <b>不是</b>"当前正在派发谁"——后者是个跨 <c>await</c> 会失真的瞬时状态（实测只对第一张生效，见补丁注释）。
    /// </remarks>
    internal static PowerModel? MirrorOriginOfGeneratedCard(Player creator, CardModel card)
    {
        if (creator.Creature is not { } creature)
        {
            return null;
        }

        // 顺着克隆链往上找：夜宴是"载荷里的那张牌 → 再克隆一张"，所以牌本身就是 CloneOf 那一环。
        var chain = new List<CardModel>(4);
        for (CardModel? current = card; current is not null && chain.Count < 4; current = current.CloneOf)
        {
            chain.Add(current);
        }

        foreach (var power in creature.Powers)
        {
            if (!IsMirrorCopy(power))
            {
                continue;
            }

            foreach (var candidate in chain)
            {
                if (PowerPayload.References(power, candidate))
                {
                    return power;
                }
            }
        }

        return null;
    }

    internal static PowerMirrorPolicy PolicyOf(PowerModel power)
    {
        // ① 手工名单优先（Overrides：明确该不镜像 / 明确该镜像的例外）
        if (Overrides.TryGetValue(power.GetType(), out var policy))
        {
            return policy;
        }

        // ② 怪施加的能力不镜像：本体怪招本来就"对每个玩家各施加一次"（撤销目标折叠后已恢复原状），
        //    再镜像一份等于翻倍层数/持续时间；而且怪的能力也不需要"两人共用"。
        if (power.Applier is { IsMonster: true })
        {
            return PowerMirrorPolicy.SingleInstance;
        }

        // ③ 其余（玩家打出的能力牌，含资源类）= 两人共用：镜像给组内每个成员。
        //    资源类（下回合加费、往手牌补牌…）靠"两份各自消耗、不联删"（见 OnPowerRemoved）让双方都有收益，
        //    内部数据由 PowerPayload 在派发前从原件补齐。
        return PowerMirrorPolicy.Mirror;
    }

    /// <summary>类型 → 是否重写了"私有资源族"钩子。</summary>
    private static readonly Dictionary<Type, bool> PrivateResourceCache = [];

    /// <summary>这个能力改的是不是"每个玩家自己的资源"（能量 / 抽牌 / 手牌）＝一次性、各自消耗的那一类。</summary>
    /// <remarks>
    /// 判据 = 该类型<b>自己重写</b>了 <c>ModifyMaxEnergy</c> / <c>AfterEnergyReset</c> / <c>ModifyHandDraw</c> /
    /// <c>AfterModifyingHandDraw</c> / <c>BeforeHandDraw</c> 之一（这些钩子逐个玩家调用）。
    /// 它<b>不</b>决定"要不要镜像"（资源类也镜像），只决定：<b>不联删</b>（否则先开始回合的人把收益拿走）、
    /// <b>不跟着改层数</b>（层数在施加那刻定下，同步会变成两边互相扣）。
    /// </remarks>
    private static bool TouchesPrivateResources(PowerModel power)
    {
        var type = power.GetType();
        if (PrivateResourceCache.TryGetValue(type, out var cached))
        {
            return cached;
        }

        bool touches;
        try
        {
            touches = PrivateResourceHooks.Any(hook =>
                type.GetMethod(hook, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    is { DeclaringType: { } declaring } && declaring == type);
        }
        catch (Exception)
        {
            touches = false;
        }

        PrivateResourceCache[type] = touches;

        if (touches)
        {
            Log.Info($"[together] power.mirror：{type.Name} → 资源类（两份各自消耗，不联删、不同步层数）");
        }

        return touches;
    }

    /// <summary>私有资源族的钩子名（逐个玩家调用）。</summary>
    private static readonly string[] PrivateResourceHooks =
    [
        "ModifyMaxEnergy",
        "AfterEnergyReset",
        "ModifyHandDraw",
        "AfterModifyingHandDraw",
        "BeforeHandDraw",
    ];

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

    /// <summary>记下"这个效果刚命中了某个成员"。</summary>
    /// <remarks>
    /// 再次遇到"同类型 + 同施加者 + 同层数"时：目标已在名单里 = **新一轮**（重置名单）；
    /// 目标是组里还没被命中过的成员 = 同一个效果继续打到别人身上（只加名单）。这样 2~4 人的 AoE 减益只算一次。
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

    /// <summary>这次施加是不是"同一个效果紧接着打到组里<b>另一个</b>成员"（是的话整个忽略）。</summary>
    /// <remarks>
    /// 典型场景：同族神官的"脆弱宝珠"一次 <c>PowerCmd.Apply&lt;FrailPower&gt;(…, targets, 1, 怪物, null)</c>
    /// 带着所有玩家。共享身体只有一副，这个减益<b>只该算一次</b>；但本体逐个目标 Apply：给锚点上 1 层、
    /// 镜像克隆给其他成员、紧接着成员又被 Apply 一次 → 本体走"已有实例 → 加层数"变成 2 层（实测"减益双倍"）。
    /// 判定用"类型 + 施加者 + 层数 + 目标是组里另一个还没被这次效果命中的成员 + 时间窗"，
    /// 所以连打两张同名卡（施加者不同）、下回合再吃同一减益（新一轮）都不会被误吞。
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

        // "能力自己衍生出来的跨成员施加"不镜像：没有卡/药水来源、又施加在别人身上（拦截的 CoveredPower 在
        // AfterApplied 里给掩护者挂 InterceptPower 就是这种）。镜像它只会得到"自己掩护自己"这类自相矛盾的关系。
        if (power.Applier is { } source && !ReferenceEquals(source, owner) && !PowerApplySource.HasActionSource(power))
        {
            CappedLog.Info(
                "power.mirror.skip",
                $"{power.GetType().Name} 是能力自己衍生的跨成员施加（无卡/药水来源）→ 不镜像，避免复制出矛盾关系");
            return;
        }

        _mirrorDepth++;
        try
        {
            foreach (var other in owner.OthersOrEmpty())
            {
                // 非 Instanced 的能力在同一个 creature 上只能有一份（本体在 PowerCmd.Apply 里先做
                // FindExistingInstanceForStacking，第二份走"改层数"）。我们绕过了那条路，所以这里自己判：
                // 目标已经有同一份就**不再添加** —— 层数由 OnPowerAmountChanged 的既有收口同步。
                // 不判的后果是本体直抛 `Trying to add multiple instances of a non-instanced power to a creature.`
                // （实测：拦截的 InterceptPower 就是这样把整次出牌打断的。）
                if (power.InstanceType != PowerInstanceType.Instanced && other.GetPower(power.Id) is not null)
                {
                    CappedLog.Info(
                        "power.mirror.skip",
                        $"{power.GetType().Name} 在 netId{other.Player?.NetId} 上已有一份（非 Instanced）→ 跳过添加，层数由既有收口同步");
                    continue;
                }

                // MutableClone 深拷 DynamicVars、重初始化 _internalData 并置空 _owner，可直接挂到另一个 creature。
                if (power.MutableClone() as PowerModel is not { } clone)
                {
                    continue;
                }

                // 本体的契约是"内部数据在克隆时被重置"（DeepCloneFields 里 `_internalData = InitInternalData()`），
                // 而我们是替另一个宿主造副本、没有"重演填充过程"的机会 → 把原件那份搬过去（PowerPayload）。
                // 搬不动就跳过这一份：宁可少一份，也不让副本拿着空数据去 NRE。
                if (!PowerPayload.TryCopy(power, clone))
                {
                    continue;
                }

                // L1：身份对齐。本体的 `Applier` / `Target` 是在 PowerCmd.Apply 里设的，而我们绕过它直接 ApplyInternal
                // → 这两个只能自己补（否则副本不知道自己是谁施加的、指向谁，日志/判据都会错）。
                // L4-ii：**关系型**（"我施加给队友"，Owner != Applier）的副本要从"另一方视角"复述这段关系 ——
                // Applier 换成原件宿主，于是拦截那类能力在两边各自成立（原件：我掩护你；副本：你掩护我）。
                var relational = power.Applier is { } applier && !ReferenceEquals(applier, owner);
                clone.Applier = relational ? owner : power.Applier;
                clone.Target = ReferenceEquals(power.Target, owner)
                    ? other
                    : power.Target;

                clone.ApplyInternal(other, power.Amount, silent: true);

                // 自身字段（派生类那些非基础设施字段）在 ApplyInternal 之后补：Redirect 需要副本的 Owner 已经就位。
                PowerPayload.SyncFields(power, clone);

                // 打上"我是镜像副本"的标记（见 IsMirrorCopy 的注释）。
                MirrorCopies.Add(clone, MirrorMarker);

                // 记住原件：副本的内部数据要在每次派发钩子前从它同步（PowerPayload.Sync）。
                MirrorSources.Add(clone, power);

                // 本体对玩家侧的减益会顺手设 SkipNextDurationTick（"上减益这回合先不掉层"），克隆体造在那行之前，得自己补上。
                clone.SkipNextDurationTick = power.SkipNextDurationTick
                                             || (other.Side == CombatSide.Player && power.Type == PowerType.Debuff);

                // L3-b：**关系型**副本要补跑一次 AfterApplied —— 因为它的衍生施加（拦截的 CoveredPower→InterceptPower）
                // 被"无来源的跨成员施加不镜像"那条守卫拦住了，只能由副本自己建。
                // 非关系型不重放：它们的衍生能力由正常镜像机制送过去，再重放就是一边双份（FlexPotion 那类）。
                if (relational)
                {
                    ScheduleReplay(clone);
                }
            }

            // 记一笔，供"同一个效果打到组里其他人"的判定使用。
            RecordApplication(power, power.Amount, power.Applier, owner);
        }
        finally
        {
            _mirrorDepth--;
        }
    }

    /// <summary>让镜像副本补跑一次"施加流程"（异步，失败只记日志）。</summary>
    /// <remarks>
    /// <b>为什么不能直接在钩子里 await</b>：我们挂在 <c>Creature.ApplyPowerInternal</c> 的同步后缀上，拿不到异步上下文；
    /// 而且本体的副本重放本来就要等原件这一轮施加走完（<c>PowerCmd.Apply</c> 是 async 的）。
    /// <b>为什么不广播 Hook</b>：广播会让"这份能力被施加了"在战斗里被算两次（怪、遗物、别的 mod 都会多响应一次），
    /// 而副本需要的只是<b>能力自己</b>的初始化/收尾。
    /// <b>为什么只重放 <c>AfterApplied</c></b>：<c>BeforeApplied</c> 里做的事大多是"给自己加一份别的能力"
    /// （临时力量 → 力量），而那份衍生能力本来就会被镜像机制送到两边 —— 再重放一次会变成一边双份（FlexPotion 那类）。
    /// 需要重放 <c>BeforeApplied</c> 时把下面那行放开，并逐个能力验证。
    /// </remarks>
    private static void ScheduleReplay(PowerModel clone)
    {
        try
        {
            TaskHelper.RunSafely(ReplayAsync(clone));
        }
        catch (Exception ex)
        {
            CappedLog.Info("power.replay", $"镜像副本重放施加流程失败（忽略）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task ReplayAsync(PowerModel clone)
    {
        try
        {
            if (clone.Owner is not { } host)
            {
                return;
            }

            var applier = clone.Applier;

            // 需要时放开这一行（默认关：见上文的"只重放 AfterApplied"）：
            // await clone.BeforeApplied(host, clone.Amount, applier, null);

            // 重放期间屏蔽镜像：副本在这条异步链上派生出来的施加（例如它自己该建的那份 InterceptPower）
            // 不再往回镜像 —— 关系是"一边一条"，副本补自己那条就够了。
            _mirrorDepth++;
            try
            {
                await clone.AfterApplied(applier, null);
            }
            finally
            {
                _mirrorDepth--;
            }

            CappedLog.Info(
                "power.replay",
                $"镜像副本补跑施加流程：{clone.GetType().Name}"
                + $"（宿主=netId{host.Player?.NetId} 施加者=netId{applier?.Player?.NetId}；只重放 AfterApplied）");
        }
        catch (Exception ex)
        {
            // 重放失败只影响这一份副本的"首次初始化"，绝不能往外抛（它在副本自己那条异步链上）。
            CappedLog.Info(
                "power.replay",
                $"镜像副本补跑施加流程失败（忽略）：{clone.GetType().Name}：{ex.GetType().Name}: {ex.Message}");
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

        // "每个玩家自己的资源"类能力（下回合加费、往手牌补牌…）是一次性的：一份被消耗不该连删另一份，
        // 否则变成"先开始回合的人把收益拿走"（实测 p2 打「下回合加费」变成 p1 加费）。两份各自消耗 = 双方都有收益。
        if (TouchesPrivateResources(power))
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

        // 资源类（一次性）不跟着改层数：层数只在"施加"那刻定下、之后各份各自消耗，同步会变成"两边互相扣"。
        if (TouchesPrivateResources(power))
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

    /// <summary>在 <paramref name="to" /> 身上找 <paramref name="power" /> 对应的"第 N 份副本"。</summary>
    /// <remarks>
    /// <c>PowerInstanceType.Instanced</c> 的能力可以有多个同类型实例，
    /// 所以按（类型，同类型内第几个）配对，而不是按类型唯一匹配。
    /// </remarks>
    internal static PowerModel? FindCounterpart(Creature from, Creature to, PowerModel power)
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

/// <summary>能力三个入口的收口：施加 / 移除 / 改层数，统统交给 <see cref="PowerMirror" />。</summary>
[HarmonyPatch]
internal static class PowerMirrorPatches
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(Creature), nameof(Creature.ApplyPowerInternal));
        yield return AccessTools.Method(typeof(Creature), nameof(Creature.RemovePowerInternal));
        yield return AccessTools.Method(typeof(Creature), nameof(Creature.InvokePowerModified));
    }

    [HarmonyPostfix]
    private static void Postfix(Creature __instance, PowerModel __0, MethodBase __originalMethod, object[] __args)
    {
        switch (__originalMethod.Name)
        {
            case nameof(Creature.ApplyPowerInternal):
                PowerMirror.OnPowerApplied(__instance, __0);
                break;

            case nameof(Creature.RemovePowerInternal):
                PowerMirror.OnPowerRemoved(__instance, __0);
                break;

            // InvokePowerModified(power, change, silent)
            default:
                PowerMirror.OnPowerAmountChanged(__instance, __0, __args.Length > 2 && __args[2] is true);
                break;
        }
    }
}

/// <summary>叠层路径（<c>ModifyAmount</c>）：同一个效果打到组里其他成员时整个忽略，否则共享身体层数翻倍。
/// 前缀同时负责"记一笔"，让下一次调用能认出同组成员。</summary>
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

/// <summary>新建实例路径（<c>Apply</c>）：同样是"同一效果的另一半/其他人"就整个忽略
/// （万一镜像没成功，这一步避免本体再加一份实例）。</summary>
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

/// <summary>金币共享（可选）：组内只有一个钱包。</summary>
/// <remarks>
/// 金币收口只有一处 —— <c>Player.Gold</c> 的 setter（<c>GainGold</c>/<c>LoseGold</c>/<c>SetGold</c>
/// 最终都给它赋值并触发 <c>GoldChanged</c> 刷新顶栏），所以只挂 setter 的 Postfix：谁的钱变了就推给组里其他人。
/// 收敛性靠本体的 <c>if (value != Gold)</c>，另有一层重入守卫兜底。
/// <b>新开一局</b>把所有人起始金币<b>加起来</b>当共同余额（99 × 人数）；读档/重连只"取最大值对齐"
/// （存档里本来就是同一份，再求和会每次重连都翻倍）。开关关掉时完全不管金币。
/// </remarks>
internal static class GoldMirror
{
    private static int _depth;

    /// <summary>成组时对齐金币。<paramref name="isNewRun" /> = 新局求和（99 × 人数），否则取组内最大值对齐。</summary>
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

/// <summary>消费路径的双保险：<c>PlayerCmd.LoseGold</c>（商店买卡/删牌、事件扣钱都走它）。</summary>
/// <remarks>
/// setter 那条理论上已覆盖，这里再挂一条是兜底：将来若有扣钱路径绕过 setter（或 setter 补丁没跑到）也能接住。
/// 两个补丁都幂等（值相同不重复推）。
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
