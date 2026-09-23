using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using Together.Core.Utils;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace Together.Core.Combat;

/// <summary>
/// 共享局里的"扇形召唤"：任意成员召唤时，顺手对队友也各调一次本体入口 <c>OstyCmd.Summon</c>。
/// </summary>
/// <remarks>
/// <para>
/// 项目决定（用户 2026-09-23）：<b>平时（血量/上限变化）用镜像同步；召唤走扇形</b>。
/// 也就是"召唤"这件事只由本体入口负责（节点/血条/复活动画/替身能力都在流程里），
/// 扇形的意义是让队友也各走一遍完整召唤；之后两只之间的数值一致由召唤物镜像维持。
/// </para>
/// <para>
/// <b>已知副作用（用户已知悉）</b>：本体自己就会"给所有人召唤"的效果（例如 2 费稀有那张
/// "所有人召唤 6"）会和扇形叠加 —— 队友先被我们扇形建了一只，轮到它自己那次本体又会
/// <c>GainMaxHp</c> 加一份。若以后要收掉这个叠加，就在本前缀里加"同 source 同帧第二次 → 跳过"的判据。
/// </para>
/// <para>
/// 只在共享局生效（<c>TogetherPair.IsActive</c> 已统一收口：联机 + 配对成功）。<c>_fanOut</c> 防重入。
/// </para>
/// </remarks>
[HarmonyPatch(typeof(OstyCmd), nameof(OstyCmd.Summon))]
internal static class PetSummonFanoutPatch
{
    private static bool _fanOut;

    /// <summary>本机此刻是不是正在"扇形召唤"里（召唤物镜像据此在召唤期间不推送数值）。</summary>
    internal static bool IsFanningOut => _fanOut;

    [HarmonyPrefix]
    private static bool Prefix(PlayerChoiceContext choiceContext, Player summoner, decimal amount, AbstractModel? source)
    {
        if (_fanOut || !TogetherPair.IsActive || amount <= 0m || !TogetherPair.IsMember(summoner))
        {
            return true;
        }

        try
        {
            _fanOut = true;

            var sent = 0;
            foreach (var other in TogetherPair.OthersOf(summoner))
            {
                _ = OstyCmd.Summon(choiceContext, other, amount, source);
                sent++;
            }

            CappedLog.Info(
                "summon.fanout",
                $"扇形召唤：netId{summoner.NetId} 的召唤×{amount} 也发给 {sent} 位队友");
        }
        catch (Exception ex)
        {
            CappedLog.Info("summon.fanout", $"扇形召唤失败（忽略）：{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _fanOut = false;
        }

        return true;   // 自己那次仍走本体
    }
}

/// <summary>
/// 召唤物（本体叫 pet，奥斯提是最典型的一只）的血量同步 + 给别的 mod 留的扩展点。
/// </summary>
/// <remarks>
/// <para>
/// 本体召唤物是挂在玩家身上的 <see cref="Creature" />（<c>Creature.PetOwner</c> / <c>Player.Osty</c>），
/// 靠"替你去死"这类能力<b>替主人接伤害</b>（<c>DieForYouPower.ModifyUnblockedDamageTarget</c>：
/// 打向主人的<b>未格挡</b>伤害被改成打向召唤物）。共享身体下两个人各有一只同名召唤物，
/// 而镜像原来只推"两个人自己的身体"，于是两边召唤物的血量各走各的（一边死了另一边还活着）。
/// </para>
/// <para>
/// 这里把"召唤物 → 队友身上的同一只"的配对补进身体镜像：<c>BodyStatMirrorPatch</c> 一旦遇到 pet，
/// 就把 当前生命 / 生命上限 推过去（本体的生死就是 <c>IsAlive =&gt; CurrentHp &gt; 0</c>，所以推到 0 即同步死亡）。
/// 伤害 / 回血 / 加生命上限全都走 <c>set_CurrentHp</c> / <c>set_MaxHp</c>，正好是已有挂点，不用另加补丁。
/// 不推格挡：pet 上显示的格挡是"主人的格挡"，本体自己会画。
/// </para>
/// <para>
/// <b>【扩展点】其他 mod 角色的召唤物</b>：默认规则按"Monster 模型 ID + 同种序号"配对，
/// 所以只要用的是本体这套 pet（<c>PlayerCmd.AddPet&lt;T&gt;</c>），<b>不注册也能同步</b>。特殊情形二选一：
/// </para>
/// <list type="bullet">
/// <item><description><c>SummonMirror.RegisterKeyRule("MyMod", c =&gt; c.Monster is MySummon ? "MySummon" : null, c =&gt; c.PetOwner ?? MyOwnerOf(c))</c>
/// —— 给出"怎么认出是同一只"的键，以及"它属于谁"（键相同就在两个人之间配对）。</description></item>
/// <item><description><c>SummonMirror.RegisterPairing("MyMod", c =&gt; 队友身上对应的那只)</c> —— 完全自己决定配对，优先级最高。</description></item>
/// </list>
/// <para>请在 mod 初始化（注册内容）时注册这些规则，不要在战斗中途注册。</para>
/// <para>
/// <b>还没做的部分</b>：召唤是逐玩家的（一局里两个人各有一只 Osty）。如果只有一个人召出来了，
/// 我们不会替另一边凭空造一只（那要重跑本体的召唤 + 钩子 + 界面流程），只在日志里留一条
/// <c>summon.missing</c>。要不要做"召唤事件镜像"，等 log 说话。
/// </para>
/// </remarks>
public static class SummonMirror
{
    private sealed record KeyRule(string Name, Func<Creature, string?> KeyOf, Func<Creature, Player?>? OwnerOf);

    private sealed record PairingRule(string Name, Func<Creature, Creature?> PairOf);

    /// <summary>自定义键规则（后注册的优先）。</summary>
    private static readonly List<KeyRule> KeyRules = [];

    /// <summary>自定义配对规则（优先级最高）。</summary>
    private static readonly List<PairingRule> Pairings = [];

    /// <summary>
    /// 【扩展点】注册"怎么认出同一只召唤物"：<paramref name="keyOf" /> 返回的键两端算出来必须一致，
    /// 键相同的两只召唤物在两名成员之间配对；返回 null 表示这条规则不管这只。
    /// </summary>
    /// <param name="name">规则名（出错时进日志）。</param>
    /// <param name="keyOf">配对键。</param>
    /// <param name="ownerOf">这只召唤物属于谁（不给就取 <c>Creature.PetOwner</c>；非 pet 的召唤物必须给）。</param>
    public static void RegisterKeyRule(string name, Func<Creature, string?> keyOf, Func<Creature, Player?>? ownerOf = null)
    {
        KeyRules.Add(new KeyRule(name, keyOf, ownerOf));
    }

    /// <summary>【扩展点】完全自己决定配对：返回队友身上对应的那只（返回 null 表示这条规则不管）。</summary>
    public static void RegisterPairing(string name, Func<Creature, Creature?> pairOf)
    {
        Pairings.Add(new PairingRule(name, pairOf));
    }

    /// <summary>队友身上对应的那只召唤物；不是该同步的召唤物 / 对面没有 → null。</summary>
    internal static Creature? PartnerOf(Creature creature)
    {
        try
        {
            if (!TogetherPair.IsActive)
            {
                return null;
            }

            foreach (var pairing in Pairings)
            {
                var partner = Invoke(pairing.Name, () => pairing.PairOf(creature));
                if (partner is not null && !ReferenceEquals(partner, creature))
                {
                    return partner;
                }
            }

            if (!TryClaim(creature, out var key, out var owner) || owner is null)
            {
                return null;
            }

            foreach (var partnerPlayer in TogetherPair.OthersOf(owner))
            {
                if (partnerPlayer.PlayerCombatState?.Pets is not { } pets)
                {
                    continue;
                }

                foreach (var candidate in pets)
                {
                    if (TryClaim(candidate, out var candidateKey, out _) && candidateKey == key)
                    {
                        return candidate;
                    }
                }
            }

            CappedLog.Info(
                "summon.missing",
                $"队友身上没有同一只召唤物（{key}，{creature.LogName}）→ 本次只同步了一边");
            return null;
        }
        catch (Exception ex)
        {
            // 镜像挂在血量 setter 上，绝不能往外抛。
            CappedLog.Info("summon.missing", $"召唤物配对失败（忽略）：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>这只 creature 算不算要同步的召唤物；是的话给出配对键与它的主人。</summary>
    private static bool TryClaim(Creature creature, out string key, out Player? owner)
    {
        key = string.Empty;
        owner = null;

        for (var i = KeyRules.Count - 1; i >= 0; i--)
        {
            var rule = KeyRules[i];
            var claimed = Invoke(rule.Name, () => rule.KeyOf(creature));
            if (string.IsNullOrEmpty(claimed))
            {
                continue;
            }

            key = $"{rule.Name}:{claimed}";
            owner = Invoke(rule.Name, () => rule.OwnerOf?.Invoke(creature)) ?? creature.PetOwner;
            return true;
        }

        // 默认规则：本体的 pet 按"Monster 模型 ID + 同种序号"配对（同种可以有好几只）。
        if (!creature.IsPet || creature.Monster is not { } monster)
        {
            return false;
        }

        key = $"pet:{monster.Id.Entry}#{SameKindIndexOf(creature, monster)}";
        owner = creature.PetOwner;
        return true;
    }

    /// <summary>这只召唤物是主人身上第几只同种召唤物（两端算出来一致）。</summary>
    private static int SameKindIndexOf(Creature pet, MonsterModel monster)
    {
        if (pet.PetOwner?.PlayerCombatState?.Pets is not { } pets)
        {
            return 0;
        }

        var index = 0;
        foreach (var other in pets)
        {
            if (ReferenceEquals(other, pet))
            {
                break;
            }

            if (other.Monster?.Id == monster.Id)
            {
                index++;
            }
        }

        return index;
    }

    /// <summary>外部传进来的规则可能抛异常，先把它们挡在镜像之外。</summary>
    private static T? Invoke<T>(string name, Func<T?> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            CappedLog.Info("summon.missing", $"召唤物配对规则「{name}」抛异常（忽略）：{ex.GetType().Name}: {ex.Message}");
            return default;
        }
    }
}

/// <summary>
/// 召唤物（pet）共享：两名成员<b>共用同一只</b>召唤物（奥斯提等）。
/// </summary>
/// <remarks>
/// <para>
/// 本体召唤物是逐玩家的（<c>PlayerCombatState.Pets</c> / <c>Player.Osty</c> / <c>Creature.PetOwner</c>），
/// 而"召唤"是<b>累加</b>语义：已经活着就 <c>GainMaxHp</c>，没有才新建。共享身体下两人各一只的话，
/// 两个亡灵契约师的初始遗物（<c>BoundPhylactery</c>：开局各召唤 1）会各建一只 1 血奥斯提，
/// 而不是"同一只 2 血"。
/// </para>
/// <para>
/// 做法和共享牌堆/球位同源：<b>把回声那份宠物列表字段直接换成锚点那一份</b>（<c>_pets</c>）。
/// 换完之后 <c>Player.Osty</c> / <c>IsOstyAlive</c> 对两人是同一只 →
/// 第二个人再召唤就是 <c>GainMaxHp</c> → 召唤值自然累加（1 + 1 = 2）✓；
/// 复活、死亡、界面节点也都只有这一只，两边看到的是同一个生物。
/// </para>
/// <para>
/// <b>唯一要补的一条</b>：本体 <c>OstyCmd.Summon</c> 里那句"找回自己那只"是
/// <c>Allies.FirstOrDefault(c =&gt; c.Monster is Osty &amp;&amp; c.PetOwner == summoner)</c> —— 共享之后
/// <c>PetOwner</c> 只可能是当初召唤它的那个人，所以另一个人遇到"奥斯提已经死了要复活"时会走进
/// "新建一只"的分支（共享列表里出现两只）。<c>Creature.PetOwner</c> 的 setter 又是"有主就抛异常"，
/// 不能临时改主人。所以这里对这条<b>只接管复活分支</b>（逻辑照抄本体，含各个 Hook），
/// 活着/全新召唤两条照旧交给本体。
/// </para>
/// </remarks>
internal static class PetSharing
{
    /// <summary>这次开局要不要管宠物共享。</summary>
    public static bool Applies(Player? player)
    {
        return TogetherPair.IsActive && TogetherPair.IsMember(player);
    }

    /// <summary>
    /// 共享的那只奥斯提已经死了、但本体的"按 PetOwner 找回"认不出这位召唤者时的复活分支。
    /// </summary>
    /// <remarks>照抄本体 <c>OstyCmd.Summon</c> 的复活分支（含 Hook），只是把宠物固定成共享的那一只。</remarks>
    internal static async Task<SummonResult> ReviveShared(
        PlayerChoiceContext choiceContext,
        Player summoner,
        Creature pet,
        decimal amount,
        AbstractModel? source)
    {
        var combatState = summoner.Creature.CombatState;
        amount = Hook.ModifySummonAmount(combatState, summoner, amount, source);
        if (amount == 0m)
        {
            return new SummonResult(pet, 0m);
        }

        if (CombatManager.Instance.IsInProgress)
        {
            SfxCmd.Play("event:/sfx/characters/necrobinder/necrobinder_summon");
        }

        // pet 已经在共享列表里 → AddPetInternal 是空操作（也不会去改 PetOwner，避开"已有主人"异常）。
        summoner.PlayerCombatState.AddPetInternal(pet);
        // 不重置血上限：有卡牌（如"献祭/保护者"）就是读奥斯提的血上限，复活时按"累加"处理。
        await CreatureCmd.GainMaxHp(pet, amount);
        await CreatureCmd.Heal(pet, amount, true);
        await Hook.AfterOstyRevived(combatState, pet);
        CombatManager.Instance.History.Summoned(combatState, (int)amount, summoner);
        await Hook.AfterSummon(combatState, choiceContext, summoner, amount);

        return new SummonResult(pet, amount);
    }
}

/// <summary>
/// 「替你去死」（奥斯提）在共享身体下的组内等价。
/// </summary>
/// <remarks>
/// 本体判定是 target != Owner.PetOwner?.Creature —— 只替"召出它的那个人"挡未格挡的强化攻击。
/// 共享身体里两名成员各有一个 creature（敌人一般打锚点那个），于是亡灵在回声位时奥斯提完全不响应
/// （"p2 亡灵召唤对 p1 不响应、p1 亡灵对 p2 有用"就是这个不对称）。这里把"组内任一成员的身体"
/// 都当作召唤物的主人，让本体原本的判定通过；其余条件（IsPoweredAttack、召唤物没死、
/// 目标本来就是它主人时）一律交回本体，保持原版行为。
/// </remarks>
[HarmonyPatch(typeof(DieForYouPower), nameof(DieForYouPower.ModifyUnblockedDamageTarget))]
internal static class DieForYouSharedBodyPatch
{
    [HarmonyPrefix]
    private static bool Prefix(DieForYouPower __instance, Creature target, ValueProp props, ref Creature __result)
    {
        if (!TogetherPair.IsActive || !props.IsPoweredAttack() || __instance.Owner.IsDead)
        {
            return true;
        }

        if (__instance.Owner.PetOwner is not { } summoner || !TogetherPair.IsMember(summoner))
        {
            return true;
        }

        // 目标本来就是"召出它的那个人的身体" → 交回本体，行为完全一致。
        if (target.Player is { } targetOwner && ReferenceEquals(targetOwner, summoner))
        {
            return true;
        }

        // 目标是同一具共享身体的另一位成员 → 等价处理：这次伤害改道到这只召唤物。
        if (target.Player is { } other && TogetherPair.IsMember(other))
        {
            __result = __instance.Owner;
            return false;
        }

        return true;
    }
}

/// <summary>
/// 奥斯提复活：不重置血上限（本体复活分支会 <c>SetMaxHp</c> 覆盖成新的召唤值，有卡牌要读这个值）。
/// </summary>
/// <remarks>
/// 只接管"自己那只已经死了"的复活分支，改成 <c>GainMaxHp</c> 累加 + 治疗；
/// 活着（本体自己会 <c>GainMaxHp</c>）和"还没有这只"（本体自己新建）两条都原样交给本体。
/// </remarks>
[HarmonyPatch(typeof(OstyCmd), nameof(OstyCmd.Summon))]
internal static class OstySharedSummonPatch
{
    [HarmonyPrefix]
    private static bool Prefix(
        PlayerChoiceContext choiceContext,
        Player summoner,
        decimal amount,
        AbstractModel? source,
        ref Task<SummonResult> __result)
    {
        // 【已停用】项目决定：奥斯提的生死 / 血量上限完全按本体来 —— 死亡后再召唤时上限定会重置
        // （本体复活分支里的 SetMaxHp），不再由我们改成累加。这里只留一个空壳，随时可删。
        _ = choiceContext;
        _ = summoner;
        _ = amount;
        _ = source;
        _ = __result;
        return true;
    }
}
