using System.Reflection;
using System.Reflection.Emit;
using System.Threading.Tasks;

using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;
using Together.Core.Common;
using Together.Core.Diagnostics;
using Together.Core.Foundation;
using Together.Core.Settings;

namespace Together.Core.Alignment;

/// <summary>「这张牌是我的」判据的组内放宽：让组内每个成员的遗物 / 能力都认这张共享牌。</summary>
/// <remarks>
/// <para>
/// 本体里上百处判据写成 <c>card.Owner == base.Owner</c>（五轮书、金纸、卡戎之灰、无痛、黑暗拥抱、苦无……）。
/// 共享卡组下 <c>card.Owner</c> 只能是一个"约定值"，两边还可能漂 → 同一次事件两端算出的结果不一样（分叉）。
/// </para>
/// <para>
/// <b>做法（不看具体卡牌 / 不看 mod 名字，纯结构判据）</b>：接管这几个钩子的派发，逐个监听者调用；
/// 调用前先判断"这次调用会不会读 <c>card.Owner</c>"（扫该监听者<b>重写</b>的那个方法的 IL，
/// 看有没有 <c>CardModel.get_Owner</c> 调用）—— 会读的，就把牌的 owner 临时设成<b>这个监听者自己的主人</b>，
/// 让它看到"这是我的牌"；调用完立刻还原。不读 owner 的监听者（成就、鼓、午夜这类"看是不是自己这张牌"的）
/// 原样不动，<b>不会被重复触发</b>。
/// </para>
/// <para>
/// <b>为什么不直接"整段派发跑 N 遍"</b>（像 <c>SharedHookOwnerWidenPatch</c> 在"牌进卡组"上那样）：
/// 那个钩子的监听者全按 owner 认领，所以在那种钩子上安全；而"牌被消耗 / 被弃"这些钩子里混着
/// <c>card == this</c>、<c>LocalContext.IsMine(card)</c> 这类判据，整段跑 N 遍会让它们各触发 N 次
/// （实测语义：鼓类卡会多给能量）。
/// </para>
/// </remarks>
internal static class OwnerClaim
{
    private static readonly AccessTools.FieldRef<CardModel, Player?> OwnerField =
        AccessTools.FieldRefAccess<CardModel, Player?>("_owner");

    private static readonly MethodInfo? IterateListeners =
        AccessTools.Method(typeof(Hook), "IterateCombatHookListeners");

    /// <summary>方法 → 会不会读 <c>card.Owner</c>（扫一次缓存一次）。</summary>
    private static readonly Dictionary<MethodBase, bool> ReadsOwnerCache = [];

    /// <summary>本局 + 开关都允许时才动手（复用「遗物钩子组内放宽」那个开关）。</summary>
    public static bool Enabled =>
        TogetherPair.IsActive && TogetherSettingsSync.EffectiveCompatHookWiden;

    /// <summary>这张牌属于组内成员（= 共享卡组的牌）；混合局里普通玩家自己的牌不碰。</summary>
    public static bool IsSharedCard(CardModel? card)
    {
        return card?.Owner is { } owner && TogetherPair.IsMember(owner);
    }

    /// <summary>这个监听者属于组内哪位成员（怪物 / 别的非成员返回 null）。</summary>
    private static Player? MemberOwnerOf(AbstractModel model)
    {
        var owner = model switch
        {
            RelicModel relic => relic.Owner,
            PotionModel potion => potion.Owner,
            OrbModel orb => orb.Owner,
            PowerModel power => power.Owner?.Player,
            CardModel card => card.Owner,
            _ => null,
        };

        return owner is not null && TogetherPair.IsMember(owner) ? owner : null;
    }

    /// <summary>这个监听者重写的那个钩子方法体里，有没有读 <c>card.Owner</c>。</summary>
    /// <remarks>
    /// <b>必须解析到真实方法体</b>：本体的钩子大多写成 <c>async Task</c>，方法自己只剩
    /// "建状态机 + <c>AsyncTaskMethodBuilder.Start</c>"，真正的逻辑在编译器生成的 <c>&lt;X&gt;d__N.MoveNext</c> 里
    /// —— 只扫外层方法会一个都扫不到（实测 `owner.widen` 全是"放宽到组内 0 个监听者"）。
    /// </remarks>
    private static bool ReadsOwner(AbstractModel model, string hookName, Type[] signature)
    {
        MethodInfo? method;
        try
        {
            method = model.GetType().GetMethod(
                hookName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: signature,
                modifiers: null);
        }
        catch (Exception)
        {
            return false;
        }

        if (method is null || method.DeclaringType == typeof(AbstractModel))
        {
            // 没重写（拿到基类空实现）→ 本体什么都没做，谈不上"读 owner"。
            return false;
        }

        var body = BodyOf(method);

        lock (ReadsOwnerCache)
        {
            if (ReadsOwnerCache.TryGetValue(body, out var cached))
            {
                return cached;
            }
        }

        var reads = ScanForOwnerGetter(body);

        lock (ReadsOwnerCache)
        {
            ReadsOwnerCache[body] = reads;
        }

        return reads;
    }

    /// <summary>拿到"真正装代码"的那个方法：<c>async</c> 方法取状态机的 <c>MoveNext</c>，其余取自己。</summary>
    private static MethodBase BodyOf(MethodInfo method)
    {
        try
        {
            var attribute = method.GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>();
            if (attribute?.StateMachineType is { } stateMachine
                && AccessTools.Method(stateMachine, "MoveNext") is { } moveNext)
            {
                return moveNext;
            }
        }
        catch (Exception)
        {
            // 拿不到特性就按同步方法扫。
        }

        return method;
    }

    /// <summary>扫原始 IL 字节：有没有 <c>call/callvirt CardModel.get_Owner</c>。</summary>
    /// <remarks>只认这两个操作码 + 元数据 token，<b>不解析整段指令</b>（沿用 <c>ModelAccess.ContainsStringConstant</c> 的思路）。</remarks>
    private static bool ScanForOwnerGetter(MethodBase method)
    {
        try
        {
            var bytes = method.GetMethodBody()?.GetILAsByteArray();
            if (bytes is null)
            {
                return false;
            }

            for (var i = 0; i + 4 < bytes.Length; i++)
            {
                if (bytes[i] != OpCodes.Call.Value && bytes[i] != OpCodes.Callvirt.Value)
                {
                    continue;
                }

                var token = BitConverter.ToInt32(bytes, i + 1);
                try
                {
                    var target = method.Module.ResolveMethod(token);
                    if (target is not null
                        && target.Name == "get_Owner"
                        && target.DeclaringType == typeof(CardModel))
                    {
                        return true;
                    }
                }
                catch (Exception)
                {
                    // 这个 token 不是方法，继续扫。
                }
            }
        }
        catch (Exception)
        {
            // 读不到方法体（反射限制等）→ 当作"不敏感"，退回原版行为。
        }

        return false;
    }

    /// <summary>按"组内每个成员的『我的牌』判据都成立"的语义派发一次。</summary>
    /// <param name="listeners">这次要派发的监听者（调用方按本体各自用的那份来源取，见 <see cref="Listeners" />）。</param>
    public static async Task Dispatch(
        IEnumerable<AbstractModel> listeners,
        PlayerChoiceContext? choiceContext,   // pushModel: false 的两条钩子本体就不 push，这里可以传 null
        CardModel card,
        string hookName,
        Type[] signature,
        Func<AbstractModel, Task> invoke,
        bool pushModel = true,
        bool popBeforeFinished = false)
    {
        var original = OwnerField(card);
        var widened = 0;

        // 逐个监听者调用：owner 敏感的那些，临时让它看到"自己的牌"。
        foreach (var model in listeners.ToList())
        {
            var view = MemberOwnerOf(model);
            var swapped = view is not null
                          && !ReferenceEquals(original, view)
                          && ReadsOwner(model, hookName, signature);

            if (swapped)
            {
                OwnerField(card) = view;
                widened++;
            }

            try
            {
                if (pushModel)
                {
                    choiceContext!.PushModel(model);
                }

                await invoke(model);

                // 本体的两种收尾顺序都要照抄：卡牌类钩子是"先 InvokeExecutionFinished 再 PopModel"，
                // 伤害钩子（AfterDamageReceived）是反过来。
                if (popBeforeFinished)
                {
                    if (pushModel)
                    {
                        choiceContext!.PopModel(model);
                    }

                    model.InvokeExecutionFinished();
                }
                else
                {
                    model.InvokeExecutionFinished();
                    if (pushModel)
                    {
                        choiceContext!.PopModel(model);
                    }
                }
            }
            finally
            {
                if (swapped)
                {
                    // 断言：临时换视角期间如果有人（本体 / 别的 mod / 我们自己的另一条路径）改过归属，
                    // 我们这一句还原会把它**丢掉**。以前这种"丢掉"是静默的 —— 现在留一行证据。
                    var afterInvoke = OwnerField(card);
                    OwnerField(card) = original;

                    if (!ReferenceEquals(afterInvoke, view))
                    {
                        CappedLog.Info(
                            "drift.widen",
                            $"{hookName}：{card.Id.Entry} 的临时视角期间 owner 被改成 netId{afterInvoke?.NetId}"
                            + $"（视角 netId{view?.NetId}、还原成 netId{original?.NetId} → 那次改写已被丢弃）");
                        TogetherAlert.Notify(
                            "归属视角被覆盖",
                            $"{hookName} 派发 {card.Id.Entry} 时，临时视角期间 owner 被改成 netId{afterInvoke?.NetId}，"
                            + $"还原回 netId{original?.NetId} 时把那次改写丢掉了");
                    }
                }
            }
        }

        CappedLog.Info(
            "owner.widen",
            $"{hookName}：{card.Id.Entry} 的「我的牌」判据放宽到组内 {widened} 个监听者（原 owner=netId{original?.NetId}）");
    }

    /// <summary>取这次派发的监听者。</summary>
    /// <param name="guarded">
    /// <c>true</c> = 走本体的 <c>IterateCombatHookListeners</c>（带"战斗正在结束就不派发"的守卫，
    /// 消耗/弃牌/抽牌那几条钩子用的是它）；<c>false</c> = 直接 <c>combatState.IterateHookListeners()</c>
    /// （"打出牌"那条钩子用的是它）。必须和本体一致，否则要么漏派发、要么多派发。
    /// </param>
    public static IEnumerable<AbstractModel> Listeners(ICombatState combatState, bool guarded)
    {
        if (!guarded)
        {
            return combatState.IterateHookListeners();
        }

        return IterateListeners?.Invoke(null, [combatState]) as IEnumerable<AbstractModel> ?? [];
    }

    /// <summary>跑（可以没有战斗状态）的那份监听者：<c>runState.IterateHookListeners(combatState)</c>。</summary>
    public static IEnumerable<AbstractModel> Listeners(IRunState runState, ICombatState? combatState)
    {
        return runState.IterateHookListeners(combatState);
    }
}

/// <summary>「战斗里生成了牌」的组内放宽（本体这里不带 Push/Pop，照抄）。</summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardGeneratedForCombat))]
internal static class SharedCardGeneratedOwnerWidenPatch
{
    [HarmonyPrefix]
    private static bool Prefix(ICombatState combatState, CardModel card, Player? creator, ref Task __result)
    {
        if (!OwnerClaim.Enabled || !OwnerClaim.IsSharedCard(card))
        {
            return true;
        }

        __result = OwnerClaim.Dispatch(
            OwnerClaim.Listeners(combatState, guarded: true).ToList(),
            null,
            card,
            nameof(AbstractModel.AfterCardGeneratedForCombat),
            [typeof(CardModel), typeof(Player)],
            model => model.AfterCardGeneratedForCombat(card, creator),
            pushModel: false);
        return false;
    }
}

/// <summary>「牌被移出」的组内放宽（跑局级监听者、不带 Push/Pop，照抄）。</summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.BeforeCardRemoved))]
internal static class SharedBeforeCardRemovedOwnerWidenPatch
{
    [HarmonyPrefix]
    private static bool Prefix(IRunState runState, CardModel card, ref Task __result)
    {
        if (!OwnerClaim.Enabled || !OwnerClaim.IsSharedCard(card))
        {
            return true;
        }

        __result = OwnerClaim.Dispatch(
            OwnerClaim.Listeners(runState, null).ToList(),
            null,
            card,
            nameof(AbstractModel.BeforeCardRemoved),
            [typeof(CardModel)],
            model => model.BeforeCardRemoved(card),
            pushModel: false);
        return false;
    }
}

/// <summary>「受到伤害」的组内放宽：判据是 <c>cardSource.Owner</c> 的那几件（假人 / 迷你加农 / 神秘打火机 / 佩尔军团）。</summary>
/// <remarks>
/// 本体是两轮（<c>AfterDamageReceived</c> → <c>AfterDamageReceivedLate</c>），
/// 监听者来自 <c>runState.IterateHookListeners(combatState)</c>，收尾顺序是 <b>先 PopModel 再 InvokeExecutionFinished</b>。
/// 只有"这次伤害有卡来源、且那张卡是共享卡组的牌"才接管 —— 没卡来源（怪物平砍、中毒）直接交回本体。
/// </remarks>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterDamageReceived))]
internal static class SharedDamageReceivedOwnerWidenPatch
{
    private static bool _reentrant;

    [HarmonyPrefix]
    private static bool Prefix(
        PlayerChoiceContext choiceContext,
        IRunState runState,
        ICombatState? combatState,
        Creature target,
        DamageResult result,
        ValueProp props,
        Creature? dealer,
        CardModel? cardSource,
        ref Task __result)
    {
        if (_reentrant || !OwnerClaim.Enabled || cardSource is null || !OwnerClaim.IsSharedCard(cardSource))
        {
            return true;
        }

        _reentrant = true;
        __result = RunAsync(choiceContext, runState, combatState, target, result, props, dealer, cardSource);
        return false;
    }

    private static async Task RunAsync(
        PlayerChoiceContext choiceContext,
        IRunState runState,
        ICombatState? combatState,
        Creature target,
        DamageResult result,
        ValueProp props,
        Creature? dealer,
        CardModel cardSource)
    {
        var signature = new[]
        {
            typeof(PlayerChoiceContext), typeof(Creature), typeof(DamageResult),
            typeof(ValueProp), typeof(Creature), typeof(CardModel),
        };

        try
        {
            await OwnerClaim.Dispatch(
                OwnerClaim.Listeners(runState, combatState).ToList(),
                choiceContext,
                cardSource,
                nameof(AbstractModel.AfterDamageReceived),
                signature,
                model => model.AfterDamageReceived(choiceContext, target, result, props, dealer, cardSource),
                popBeforeFinished: true);

            await OwnerClaim.Dispatch(
                OwnerClaim.Listeners(runState, combatState).ToList(),
                choiceContext,
                cardSource,
                nameof(AbstractModel.AfterDamageReceivedLate),
                signature,
                model => model.AfterDamageReceivedLate(choiceContext, target, result, props, dealer, cardSource),
                popBeforeFinished: true);
        }
        finally
        {
            _reentrant = false;
        }
    }
}

/// <summary>「牌被消耗」的组内放宽（金纸 / 卡戎之灰 / 遗忘之魂 / 无痛 / 黑暗拥抱……）。</summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardExhausted))]
internal static class SharedCardExhaustedOwnerWidenPatch
{
    private static bool _reentrant;

    [HarmonyPrefix]
    private static bool Prefix(
        ICombatState combatState,
        PlayerChoiceContext choiceContext,
        CardModel card,
        bool causedByEthereal,
        ref Task __result)
    {
        if (_reentrant || !OwnerClaim.Enabled || !OwnerClaim.IsSharedCard(card))
        {
            return true;
        }

        _reentrant = true;
        __result = RunAsync(combatState, choiceContext, card, causedByEthereal);
        return false;
    }

    private static async Task RunAsync(ICombatState combatState, PlayerChoiceContext ctx, CardModel card, bool causedByEthereal)
    {
        try
        {
            await OwnerClaim.Dispatch(
                OwnerClaim.Listeners(combatState, guarded: true).ToList(),
                ctx,
                card,
                nameof(AbstractModel.AfterCardExhausted),
                [typeof(PlayerChoiceContext), typeof(CardModel), typeof(bool)],
                model => model.AfterCardExhausted(ctx, card, causedByEthereal));
        }
        finally
        {
            _reentrant = false;
        }
    }
}

/// <summary>「牌被弃掉」的组内放宽（绷带 / 钉沙……）。</summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardDiscarded))]
internal static class SharedCardDiscardedOwnerWidenPatch
{
    private static bool _reentrant;

    [HarmonyPrefix]
    private static bool Prefix(ICombatState combatState, PlayerChoiceContext choiceContext, CardModel card, ref Task __result)
    {
        if (_reentrant || !OwnerClaim.Enabled || !OwnerClaim.IsSharedCard(card))
        {
            return true;
        }

        _reentrant = true;
        __result = RunAsync(combatState, choiceContext, card);
        return false;
    }

    private static async Task RunAsync(ICombatState combatState, PlayerChoiceContext ctx, CardModel card)
    {
        try
        {
            await OwnerClaim.Dispatch(
                OwnerClaim.Listeners(combatState, guarded: true).ToList(),
                ctx,
                card,
                nameof(AbstractModel.AfterCardDiscarded),
                [typeof(PlayerChoiceContext), typeof(CardModel)],
                model => model.AfterCardDiscarded(ctx, card));
        }
        finally
        {
            _reentrant = false;
        }
    }
}

/// <summary>「有牌被打出」的组内放宽（苦无 / 手里剑 / 笔尖 / 象牙 / 信条 / 万花筒 / 重力 / 暴风 / 主计划者……）。</summary>
/// <remarks>本体这里是两轮派发（<c>AfterCardPlayed</c> → <c>AfterCardPlayedLate</c>），照抄两轮，各自换算视角。</remarks>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardPlayed))]
internal static class SharedCardPlayedOwnerWidenPatch
{
    private static bool _reentrant;

    [HarmonyPrefix]
    private static bool Prefix(
        ICombatState combatState,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        ref Task __result)
    {
        if (_reentrant
            || !OwnerClaim.Enabled
            || cardPlay?.Card is not { } card
            || !OwnerClaim.IsSharedCard(card))
        {
            return true;
        }

        _reentrant = true;
        __result = RunAsync(combatState, choiceContext, cardPlay, card);
        return false;
    }

    private static async Task RunAsync(ICombatState combatState, PlayerChoiceContext ctx, CardPlay cardPlay, CardModel card)
    {
        try
        {
            await OwnerClaim.Dispatch(
                OwnerClaim.Listeners(combatState, guarded: false).ToList(),
                ctx,
                card,
                nameof(AbstractModel.AfterCardPlayed),
                [typeof(PlayerChoiceContext), typeof(CardPlay)],
                model => model.AfterCardPlayed(ctx, cardPlay));

            await OwnerClaim.Dispatch(
                OwnerClaim.Listeners(combatState, guarded: false).ToList(),
                ctx,
                card,
                nameof(AbstractModel.AfterCardPlayedLate),
                [typeof(PlayerChoiceContext), typeof(CardPlay)],
                model => model.AfterCardPlayedLate(ctx, cardPlay));
        }
        finally
        {
            _reentrant = false;
        }
    }
}

/// <summary>「抽到牌」的组内放宽（自动化 / 混乱 / 锁链束缚 / 腐蚀波 / 迭代 / 速行者 / 虚空……）。</summary>
/// <remarks>本体这里是两轮派发（<c>AfterCardDrawnEarly</c> → <c>AfterCardDrawn</c>），照抄两轮。</remarks>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardDrawn))]
internal static class SharedCardDrawnOwnerWidenPatch
{
    private static bool _reentrant;

    [HarmonyPrefix]
    private static bool Prefix(
        ICombatState combatState,
        PlayerChoiceContext choiceContext,
        CardModel card,
        bool fromHandDraw,
        ref Task __result)
    {
        if (_reentrant || !OwnerClaim.Enabled || !OwnerClaim.IsSharedCard(card))
        {
            return true;
        }

        _reentrant = true;
        __result = RunAsync(combatState, choiceContext, card, fromHandDraw);
        return false;
    }

    private static async Task RunAsync(ICombatState combatState, PlayerChoiceContext ctx, CardModel card, bool fromHandDraw)
    {
        try
        {
            await OwnerClaim.Dispatch(
                OwnerClaim.Listeners(combatState, guarded: true).ToList(),
                ctx,
                card,
                nameof(AbstractModel.AfterCardDrawnEarly),
                [typeof(PlayerChoiceContext), typeof(CardModel), typeof(bool)],
                model => model.AfterCardDrawnEarly(ctx, card, fromHandDraw));

            await OwnerClaim.Dispatch(
                OwnerClaim.Listeners(combatState, guarded: true).ToList(),
                ctx,
                card,
                nameof(AbstractModel.AfterCardDrawn),
                [typeof(PlayerChoiceContext), typeof(CardModel), typeof(bool)],
                model => model.AfterCardDrawn(ctx, card, fromHandDraw));

        }
        finally
        {
            _reentrant = false;
        }
    }
}
