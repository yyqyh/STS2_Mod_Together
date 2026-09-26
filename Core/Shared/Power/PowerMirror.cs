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
using Together.Core.Alignment;
using Together.Core.Common;
using Together.Core.Diagnostics;
using Together.Core.Foundation;
using Together.Core.Settings;
using Together.Core.Shared.Body;
using Together.Core.Shared.Gold;
using Together.Core.Shared.Orb;
using Together.Core.Shared.Pet;
using Together.Core.Ui;

namespace Together.Core.Shared.Power;

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

    /// <summary>注册一条逐能力覆写（对外接口用；见 <c>TogetherApi.RegisterPowerMirrorOverride</c>）。</summary>
    internal static void RegisterOverride(Type powerType, PowerMirrorPolicy policy)
    {
        Overrides[powerType] = policy;
    }

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

    /// <summary>这次施加会不会被镜像 —— 与 <see cref="PolicyOf" /> 同一套判据，但允许用"调用参数里的施加者"兜底。</summary>
    /// <remarks>
    /// <b>为什么不能直接用 <see cref="PolicyOf" />：</b><c>PowerCmd.Apply&lt;T&gt;(context, target, …)</c> 是
    /// <b>每个目标新建一份 mutable power</b>，而 <c>power.Applier = applier</c> 要等进到 <c>Apply</c> 方法体里才赋值 ——
    /// 我们的补丁是 <c>Apply</c> 的 <b>前缀</b>，那一刻 <c>power.Applier</c> 还是 null，于是怪物施加的减益会被误判成"可镜像"，
    /// 第二次施加照样被吞（实测：<c>FRAIL_POWER=2</c> 只挂在锚点身上，回声 <c>powers=[]</c>）。
    /// 调用参数里的 <c>applier</c> 是现成的，用它判断就对了。
    /// </remarks>
    private static bool WillMirror(PowerModel power, Creature? applier)
    {
        if (Overrides.TryGetValue(power.GetType(), out var policy))
        {
            return policy == PowerMirrorPolicy.Mirror;
        }

        return power.Applier is not { IsMonster: true } && applier is not { IsMonster: true };
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

    /// <summary>"同一个效果"的记录，**按玩家选择上下文分槽**。</summary>
    /// <remarks>
    /// 本体的多目标施加是"同一个 <c>choiceContext</c> 逐个目标 Apply"（见 <c>PowerCmd.Apply&lt;T&gt;(context, IEnumerable&lt;Creature&gt;…)</c>，
    /// 内部把同一个 context 透传给单目标重载，单目标重载再转 <c>ModifyAmount</c>）；而出牌是<b>每次新建一个 context</b>
    /// （<c>PlayCardAction</c> 里 <c>New GameActionPlayerChoiceContext(this)</c>）。所以 context 引用就是"这是不是同一次效果"的天然身份，
    /// 比纯时间窗准得多：同一个施加者在 5 秒内用两张<b>不同的牌</b>给组里不同成员上同层数效果时，context 不同 → 不再被误吞。
    /// <b>分槽而不是只留最近一条</b>：两人同时吃的两个不同减益互不覆盖，否则后一个会把前一个的记录挤掉、前一个的第二个目标就不吞了。
    /// 拿不到 context 的路径（怪招内部直接 <c>ApplyInternal</c> 等）落进匿名槽，退回原来的"时间窗"判据。
    /// </remarks>
    private static readonly ConditionalWeakTable<PlayerChoiceContext, ApplicationRecord> Applications = new();

    private static ApplicationRecord? _anonymousApplication;

    /// <summary>认"同一个效果的第二次命中"的时间窗（本体逐个目标 Apply，两次之间只隔一点特效等待）。</summary>
    private const long SameEffectWindowMs = 5000;

    /// <summary>取这次施加对应的记录槽（context 拿不到时用匿名槽）。</summary>
    private static ApplicationRecord? RecordOf(PlayerChoiceContext? context)
    {
        return context is null
            ? _anonymousApplication
            : Applications.TryGetValue(context, out var record) ? record : null;
    }

    private static void SetRecord(PlayerChoiceContext? context, ApplicationRecord record)
    {
        if (context is null)
        {
            _anonymousApplication = record;
            return;
        }

        // ConditionalWeakTable 没有索引器，只能先摘再挂（key 是引用类型，按引用比较）。
        Applications.Remove(context);
        Applications.Add(context, record);
    }

    /// <summary>记下"这个效果刚命中了某个成员"。</summary>
    /// <remarks>
    /// 再次遇到"同 context + 同类型 + 同施加者 + 同层数"时：目标已在名单里 = **新一轮**（重置名单）；
    /// 目标是组里还没被命中过的成员 = 同一个效果继续打到别人身上（只加名单）。这样 2~4 人的 AoE 减益只算一次。
    /// <b>只对"会镜像"的能力做这套收口</b>（见 <see cref="IsSecondHitOfSameEffect" /> 的说明）：不镜像的能力
    /// （<c>PolicyOf</c> 判成 <see cref="PowerMirrorPolicy.SingleInstance" />，典型是<b>怪物施加的</b>减益）
    /// 本来就靠本体"逐目标各施加一次"来给到每个成员，吞第二次等于把回声那份抹掉。
    /// </remarks>
    internal static void RecordApplication(
        PowerModel power,
        decimal amount,
        Creature? applier,
        Creature target,
        PlayerChoiceContext? context)
    {
        if (!TogetherPair.IsActive
            || !WillMirror(power, applier)
            || !TogetherPair.IsMember(target.Player))
        {
            return;
        }

        lock (ApplicationGate)
        {
            var now = Environment.TickCount64;
            var record = RecordOf(context);

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

            // Apply 前缀已经记过这一笔，紧接着 ApplyInternal 的收口又会回调一次"同目标同效果"：
            // 这里续一下时间戳就够，别把带 context 的那条记录降级成匿名槽。
            if (record is not null
                && record.Type == power.GetType()
                && ReferenceEquals(record.Applier, applier)
                && record.Amount == amount
                && record.Targets.Contains(target))
            {
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
            SetRecord(context, fresh);
        }
    }

    /// <summary>这次施加是不是"同一个效果紧接着打到组里<b>另一个</b>成员"（是的话整个忽略）。</summary>
    /// <remarks>
    /// 典型场景：同族神官的"脆弱宝珠"一次 <c>PowerCmd.Apply&lt;FrailPower&gt;(…, targets, 1, 怪物, null)</c>
    /// 带着所有玩家。共享身体只有一副，这个减益<b>只该算一次</b>；但本体逐个目标 Apply：给锚点上 1 层、
    /// 镜像克隆给其他成员、紧接着成员又被 Apply 一次 → 本体走"已有实例 → 加层数"变成 2 层（实测"减益双倍"）。
    /// 判定用"<b>同一个 choiceContext</b> + 类型 + 施加者 + 层数 + 目标是组里另一个还没被这次效果命中的成员"。
    /// context 拿不到时才退回时间窗（<see cref="SameEffectWindowMs" />）。
    /// 所以连打两张同名卡（两个 context）、下回合再吃同一减益（新一轮）都不会被误吞。
    /// <para>
    /// <b>前置条件：这次施加会被镜像。</b>不镜像的能力（<c>PolicyOf</c> = <see cref="PowerMirrorPolicy.SingleInstance" />，
    /// 主要是"施加者本身就是怪物"的那些）走的是另一条路：撤销目标折叠之后，本体怪招本来就是
    /// <c>foreach (target in targets)</c> 对每个玩家各施加一次 —— 共享身体下这就是"每人各一份"，
    /// <b>不需要镜像</b>。若在这里把第二次也吞掉，回声那份就没了（实测：缩小/虚弱只挂在锚点身上，
    /// 两端 <c>powers=</c> 一边有一边空，表现就是"怪物没有给每个人施加"）。
    /// </para>
    /// </remarks>
    internal static bool IsSecondHitOfSameEffect(
        PowerModel power,
        decimal amount,
        Creature? applier,
        Creature target,
        PlayerChoiceContext? context)
    {
        if (!TogetherPair.IsActive
            || !WillMirror(power, applier)
            || !TogetherPair.IsMember(target.Player))
        {
            return false;
        }

        lock (ApplicationGate)
        {
            var record = RecordOf(context);
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
                // 要不要在目标身上**新增一份副本** —— 严格照本体的叠层语义（PowerCmd.FindExistingInstanceForStacking）：
                //   None                → 有同 Id 就叠层（不新增；层数由 OnPowerAmountChanged 的既有收口同步）
                //   Instanced           → 每次施加都是新实例（同一份原件只镜像一次）
                //   InstancedPerApplier → 同 applier 叠层、**不同 applier 各一份**
                // 少了这条分支判断的后果：要么本体直抛 `Trying to add multiple instances of a non-instanced power…`
                // （实测：拦截的 InterceptPower 把整次出牌打断过），要么 InstancedPerApplier 的"第二个施加者"永远少一份。
                if (!NeedsNewInstanceOn(other, power))
                {
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
                // L4-ii：**关系型**（"我施加给队友"，Owner != Applier）的副本从"另一方视角"复述这段关系：
                // 原件是 `applier=P1 → owner=P2`，副本就是 `applier=P2 → owner=P1`（双向对称）。
                // 典型：灵魂绑定（原件：P1 生成 Soul 时给 P2 加一张；副本：P2 生成 Soul 时给 P1 加一张）、
                // 拦截（原件在被掩护者身上、Applier=掩护者；副本反过来）。
                var relational = power.Applier is { } applier2 && !ReferenceEquals(applier2, owner);
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

                // L3-b：**关系型**副本要补跑一次 AfterApplied —— 它得用"翻转后的视角"把那边的关系建起来
                // （拦截：原件在 P1 上建 InterceptPower 覆盖 P2；副本就在 P2 上建一份覆盖 P1）。
                // 非关系型不重放：它们的衍生能力由正常镜像机制送过去，再重放就是一边双份（FlexPotion 那类）。
                if (relational)
                {
                    ScheduleReplay(clone);
                }

                CappedLog.Info(
                    "power.mirror",
                    $"{power.GetType().Name} 镜像：原件 netId{owner.Player?.NetId} → 副本 netId{other.Player?.NetId}"
                    + $"（{power.InstanceType}，层数={power.Amount}，applier=netId{power.Applier?.Player?.NetId}）");
            }

            // 记一笔，供"同一个效果打到组里其他人"的判定使用。
            // 这条路径拿不到 choiceContext（ApplyInternal 的签名里没有），落匿名槽，仍走时间窗判据。
            RecordApplication(power, power.Amount, power.Applier, owner, null);
        }
        finally
        {
            _mirrorDepth--;
        }
    }

    /// <summary>让关系型镜像副本补跑一次"施加流程"（异步，失败只记日志）。</summary>
    /// <remarks>
    /// <b>为什么不能直接在钩子里 await</b>：我们挂在 <c>Creature.ApplyPowerInternal</c> 的同步后缀上，拿不到异步上下文；
    /// 而且本体的那份施加本来就要等原件这一轮走完（<c>PowerCmd.Apply</c> 是 async 的）。
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

    /// <summary>
    /// 在 <paramref name="target" /> 上要不要<b>新增一份</b> <paramref name="power" /> 的镜像副本？
    /// </summary>
    /// <remarks>
    /// 严格照本体的叠层语义（<c>PowerCmd.FindExistingInstanceForStacking</c>）：
    /// <list type="bullet">
    /// <item><c>None</c>：目标有同 Id 就叠在上面 → <b>不新增</b>（层数由 <see cref="OnPowerAmountChanged" /> 同步）；</item>
    /// <item><c>Instanced</c>：每次施加都是新实例 → 只要"这份原件"还没镜像过就新增；</item>
    /// <item><c>InstancedPerApplier</c>：同 applier 叠层、<b>不同 applier 各一份</b> → 目标上既没有这份原件的副本、
    /// 也没有"同 applier 的同类型实例"时才新增。</item>
    /// </list>
    /// 早先那版只写"不是 Instanced 且已有同 Id 就跳过"，把 <c>InstancedPerApplier</c> 当成 <c>None</c>：
    /// 第二个施加者的那一份永远建不出来（本体那边可是新建了实例的）→ 两端各自少一份、层数/移除配对也跟着错。
    /// </remarks>
    private static bool NeedsNewInstanceOn(Creature target, PowerModel power)
    {
        var needs = power.InstanceType switch
        {
            PowerInstanceType.Instanced => !HasCounterpartFor(target, power),
            PowerInstanceType.InstancedPerApplier =>
                !HasCounterpartFor(target, power) && !HasSameApplierInstance(target, power),
            _ => target.GetPower(power.Id) is null,
        };

        CappedLog.Info(
            "power.instance",
            $"{power.GetType().Name}（{power.InstanceType}）→ netId{target.Player?.NetId} "
            + $"{(needs ? "新增副本" : "叠在已有那份上（不新增）")}"
            + $"，applier=netId{power.Applier?.Player?.NetId}");

        return needs;
    }

    /// <summary>目标身上是否已经有"<b>这份原件</b>"的镜像副本（用副本→原件表反查）。</summary>
    private static bool HasCounterpartFor(Creature target, PowerModel source)
    {
        foreach (var pair in MirrorSources)
        {
            if (ReferenceEquals(pair.Value, source) && ReferenceEquals(pair.Key.Owner, target))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>目标身上是否已有"同类型 + 同施加者"的那一份（<c>InstancedPerApplier</c> 的叠层判据）。</summary>
    /// <remarks>只用于<b>非关系型</b>：关系型副本的 <c>Applier</c> 会被复述改写，两边视角不同名，按它比会误判。</remarks>
    private static bool HasSameApplierInstance(Creature target, PowerModel power)
    {
        if (power.Applier is not { } applier)
        {
            return false;
        }

        return target.GetPowerInstances(power.Id).Any(candidate => ReferenceEquals(candidate.Applier, applier));
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
/// <remarks>判据里的"同一个效果"用 <c>PlayerChoiceContext</c> 引用认（见 <see cref="PowerMirror.RecordOf" />）：
/// 一次多目标施加会带着同一个 context 逐个目标进来，两张不同的牌则是两个 context。</remarks>
[HarmonyPatch(typeof(PowerCmd), nameof(PowerCmd.ModifyAmount), new[]
{
    typeof(PlayerChoiceContext), typeof(PowerModel), typeof(decimal),
    typeof(Creature), typeof(CardModel), typeof(bool),
})]
internal static class PowerSecondHitModifyAmountPatch
{
    [HarmonyPrefix]
    private static bool Prefix(PlayerChoiceContext __0, PowerModel __1, decimal __2, Creature? __3, ref Task<int> __result)
    {
        if (PowerMirror.IsSecondHitOfSameEffect(__1, __2, __3, __1.Owner, __0))
        {
            __result = Task.FromResult(0);
            return false;
        }

        PowerMirror.RecordApplication(__1, __2, __3, __1.Owner, __0);
        return true;
    }
}

/// <summary>新建实例路径（<c>Apply</c>）：同样是"同一效果的另一半/其他人"就整个忽略
/// （万一镜像没成功，这一步避免本体再加一份实例）。</summary>
/// <remarks>这条路径自己也要"记一笔"（把 context 带进记录），否则记录只能靠 <c>ApplyInternal</c> 的收口写到匿名槽，
/// 精度会退化成时间窗。</remarks>
[HarmonyPatch(typeof(PowerCmd), nameof(PowerCmd.Apply), new[]
{
    typeof(PlayerChoiceContext), typeof(PowerModel), typeof(Creature), typeof(decimal),
    typeof(Creature), typeof(CardModel), typeof(bool),
})]
internal static class PowerSecondHitApplyPatch
{
    [HarmonyPrefix]
    private static bool Prefix(
        PlayerChoiceContext __0,
        PowerModel __1,
        Creature __2,
        decimal __3,
        Creature? __4,
        ref Task __result)
    {
        if (PowerMirror.IsSecondHitOfSameEffect(__1, __3, __4, __2, __0))
        {
            __result = Task.CompletedTask;
            return false;
        }

        PowerMirror.RecordApplication(__1, __3, __4, __2, __0);
        return true;
    }
}

