using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Orbs;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

using Together.Core.Combat;
using Together.Core.Settings;
using Together.Core.Utils;

namespace Together.Core.Api;

/// <summary>
/// <b>对外接口</b>：别的 mod 想跟本 mod 协作（或想问"我这张卡 / 这份能力在共享局里该怎么表现"）时，只依赖这个类。
/// </summary>
/// <remarks>
/// <para>
/// 本 mod 分四层：<b>底座</b>（谁是共生体 / 总闸门）→ <b>共享域</b>（身体 / 牌堆 / 能力 / 球位 / 召唤物 / 金币）
/// → <b>规则收口</b>（归属、事件份数、顺序）→ <b>兼容层</b>；这四层全是 <c>internal</c>。
/// 依赖本 mod 请<b>只用</b>这个类（它是唯一承诺不破坏的面），不要直接碰内部类型。
/// </para>
/// <para>
/// 所有查询在"本局不是共享局"时都返回安全默认值（<c>false</c> / <c>null</c> / 空集合），不会抛。
/// <b>注册类接口请在 mod 初始化（注册内容）时调用一次</b>，不要放在战斗中途。
/// </para>
/// </remarks>
public static class TogetherApi
{
    /// <summary>本 mod 的 ModId（= 清单 id = DLL 名 = PCK 名）。</summary>
    public static string ModId => Const.ModId;

    /// <summary>本 mod 版本号。</summary>
    public static string Version => Const.Version;

    // ======================================================================
    // 对局事实：谁是共生体
    // ======================================================================

    /// <summary>本局真的在"共用身体"（联机 + 已配对）。<b>全 mod 的总闸门</b>，也是外部判断"要不要为我让路"的开关。</summary>
    public static bool IsActive => TogetherPair.IsActive;

    /// <summary>这位玩家是不是共生体成员（含锚点）。</summary>
    public static bool IsMember(Player? player)
    {
        return TogetherPair.IsMember(player);
    }

    /// <summary>锚点（权威实例持有者：主卡组与四口战斗牌堆都挂在它身上）。</summary>
    public static Player? Anchor => TogetherPair.Anchor;

    /// <summary>组内全部成员（锚点在前）。</summary>
    public static IReadOnlyList<Player> Members => [.. TogetherPair.Members()];

    /// <summary>组内除这位以外的其他成员（镜像就是往这些人身上推）。</summary>
    public static IReadOnlyList<Player> OthersOf(Player? player) => [.. TogetherPair.OthersOf(player)];

    /// <summary>这位的"队友"：两人局就是另一个人；多人局返回第一个其他成员。</summary>
    public static Player? Counterpart(Player? player)
    {
        return TogetherPair.OthersOf(player).FirstOrDefault();
    }

    // ======================================================================
    // 共享域查询
    // ======================================================================

    /// <summary>共享身体里"另一位成员的身体"（不共享局 / 不是成员时返回 <c>null</c>）。</summary>
    public static Creature? OtherBody(Creature? creature)
    {
        return creature?.OthersOrEmpty().FirstOrDefault();
    }

    /// <summary>队友身上"同一只召唤物"（配对不上返回 <c>null</c>）；特殊召唤物用 <see cref="RegisterPetKeyRule" /> 扩展。</summary>
    public static Creature? PetCounterpart(Creature? pet)
    {
        return pet is null ? null : SummonMirror.PartnerOf(pet);
    }

    /// <summary>这口球位队列是不是共生体共享队列。</summary>
    public static bool IsSharedOrbQueue(OrbQueue? queue)
    {
        return OrbSlotSharing.IsShared(queue);
    }

    /// <summary>共享球位队列的主人（也就是"该由谁跑球位回合钩子"的那个人）。</summary>
    public static Player? SharedOrbQueueOwner(OrbQueue? queue)
    {
        return OrbSlotSharing.OwnerOf(queue);
    }

    // ======================================================================
    // 能力镜像
    // ======================================================================

    /// <summary>这份能力是不是本 mod 造出来的"镜像副本"（副本自己不负责联删 / 层数同步）。</summary>
    public static bool IsMirroredPower(PowerModel power)
    {
        return PowerMirror.IsMirrorCopy(power);
    }

    /// <summary>覆写某个能力的镜像策略（纠错出口）。默认所有能力都镜像。</summary>
    /// <param name="powerType">必须派生自 <c>PowerModel</c>。</param>
    /// <param name="policy">
    /// <see cref="PowerMirrorPolicy.Mirror" /> = 组内每人一份（默认）；
    /// <see cref="PowerMirrorPolicy.SingleInstance" /> = 只留被施加的那一份（"我自己处理镜像"或"这份能力语义上不能复制"）。
    /// </param>
    /// <remarks>请在 mod 初始化时调用一次；类型会在"这份能力被施加"的那一刻被查询。</remarks>
    public static void RegisterPowerMirrorOverride(Type powerType, PowerMirrorPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(powerType);
        ArgumentNullException.ThrowIfNull(policy);

        if (!typeof(PowerModel).IsAssignableFrom(powerType))
        {
            throw new ArgumentException($"类型 {powerType.FullName} 不是 PowerModel 派生类", nameof(powerType));
        }

        PowerMirror.RegisterOverride(
            powerType,
            policy == PowerMirrorPolicy.Mirror
                ? PowerMirror.PowerMirrorPolicy.Mirror
                : PowerMirror.PowerMirrorPolicy.SingleInstance);
    }

    // ======================================================================
    // 召唤物配对扩展点（转发 SummonMirror；默认按"Monster ID + 同种序号"配对，不注册也能用）
    // ======================================================================

    /// <summary>注册"怎么认出同一只召唤物"：<paramref name="keyOf" /> 的键两端算出来必须一致。</summary>
    /// <param name="name">规则名（出错时进日志）。</param>
    /// <param name="keyOf">配对键；返回 <c>null</c> 表示这条规则不管这只。</param>
    /// <param name="ownerOf">它属于谁（不给就取 <c>Creature.PetOwner</c>；非 pet 的召唤物必须给）。</param>
    public static void RegisterPetKeyRule(
        string name,
        Func<Creature, string?> keyOf,
        Func<Creature, Player?>? ownerOf = null)
    {
        SummonMirror.RegisterKeyRule(name, keyOf, ownerOf);
    }

    /// <summary>完全自己决定召唤物配对：返回队友身上对应的那只（返回 <c>null</c> 表示这条规则不管）。</summary>
    public static void RegisterPetPairing(string name, Func<Creature, Creature?> pairOf)
    {
        SummonMirror.RegisterPairing(name, pairOf);
    }

    // ======================================================================
    // 诊断
    // ======================================================================

    /// <summary>一串牌"按当前顺序"的指纹（形如 <c>3F2A…/31</c>）：两份 log 一比就知道顺序漂没漂。</summary>
    /// <remarks>
    /// 本 mod <b>不重排任何牌堆</b>（弃牌堆 / 主卡组 / 抽牌堆都保持你给它的顺序），所以指纹是只读的调试手段，
    /// 也可以被别的 mod 拿去做自己的自检。
    /// </remarks>
    public static string PileFingerprint(IEnumerable<CardModel>? cards)
    {
        return DeterministicCardOrder.Fingerprint(cards);
    }

    /// <summary>
    /// 打一个"对账点"：生成一次校验和（两端同一步骤必然拿到同一个 <c>chk</c> 号）、
    /// 输出一行 <c>[sync] chk=… ctx=… tag=together.state …</c>，并返回这个号。
    /// </summary>
    /// <param name="tag">你自己的短标签（会进 context，形如 <c>together:&lt;tag&gt;</c>）。</param>
    /// <param name="data">可选补充信息（同 tag 下区分不同场景）。</param>
    /// <returns>这次的 <c>chk</c> 号；<b>0 = 校验和未启用</b>（单人局 / 非联机），调用方应当忽略。</returns>
    /// <remarks>
    /// <para>
    /// 用法：你的 mod 想把自己的状态跟对端（或跟 together）对齐时，先调它拿到号，再把自己的状态打进同一批日志
    /// （自己带 <c>chk=&lt;号&gt;</c> 即可），这样两份 log 里同一个号下的行就能直接对照。
    /// </para>
    /// <para>
    /// <b>两端必须都调用、且调用次数完全一致</b>：校验和的 <c>id</c> 严格按调用顺序递增，
    /// 多调（或少调）一次会让<b>之后所有号全部错位</b>，本体自己的校验和比对会报假分歧。
    /// 所以不要放在"只有一边会走"的分支里。
    /// </para>
    /// </remarks>
    public static uint Checkpoint(string tag, string data = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);

        if (RunManager.Instance?.ChecksumTracker is not { } tracker)
        {
            return 0;
        }

        var context = string.IsNullOrWhiteSpace(data) ? $"together:{tag}" : $"together:{tag} {data}";
        return tracker.GenerateChecksum(context, null).id;
    }

    // ======================================================================
    // 配对规则：把"谁该成组"的判定权交给别的 mod
    // ======================================================================

    private static readonly List<(string Name, Func<IRunState, IReadOnlyList<Player>?> Select)> PairRules = [];

    /// <summary>
    /// 注册"谁该成组"的判定规则。返回 <c>null</c>（或空列表）表示这条规则不管这一局，继续问下一条；
    /// 全部都没有结果时回落到本 mod 的现有行为（选人界面按下「共生体」按钮的名单）。
    /// </summary>
    /// <remarks>
    /// <para>请在 mod 初始化（注册内容）时调用一次。</para>
    /// <para>
    /// <b>规则必须满足</b>：① 只读，不改任何游戏状态；② 只依据 <see cref="IRunState.Players" /> 与
    /// <c>Player.Character</c> 这类"两端一致"的事实 —— 两边必须算出同一个名单、顺序也一致（按 Players 顺序），
    /// 否则锚点会分叉；③ 不要用 <c>LocalContext</c> 之类的"本机视角"。
    /// </para>
    /// <para>规则只在 <c>Arm()</c> 里被问（新开一局 / 读档 / 重连都会走），不在战斗中途问。</para>
    /// </remarks>
    public static void RegisterPairRule(string name, Func<IRunState, IReadOnlyList<Player>?> select)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(select);

        PairRules.Add((name, select));
    }

    /// <summary>按注册顺序问一遍规则；都没有结果返回 <c>null</c>。单条规则抛异常只废掉它自己。</summary>
    internal static IReadOnlyList<Player>? SelectMembersByRule(IRunState runState)
    {
        foreach (var (name, select) in PairRules)
        {
            try
            {
                if (select(runState) is { Count: > 0 } picked)
                {
                    return picked;
                }
            }
            catch (Exception ex)
            {
                // 外部 mod 一个 bug 不该让开局黑屏：单条规则出错只跳过本条。
                Main.Logger.Warn($"[together] 配对规则 {name} 抛异常，本条跳过：{ex.Message}");
            }
        }

        return null;
    }

    // ======================================================================
    // 绑定状态 / 解绑
    // ======================================================================

    /// <summary>
    /// 当前是否已配对（<c>Arm()</c> 过且没被 <see cref="Unbind" />）。
    /// </summary>
    /// <remarks>
    /// <b>它不看"是否联机"</b>：要判"战场行为现在有没有在共享"请用 <see cref="IsActive" />。
    /// 两个都对外，别用错。
    /// </remarks>
    public static bool IsBound => TogetherPair.MemberCount >= TogetherPair.MinMembers;

    /// <summary>
    /// 本局解除共生体绑定：立刻断开共享（镜像 / 共享牌堆 / 事件并发保护整体停掉），
    /// 把共享卡组按奇偶拆给两人，并把共生体开关置 false；<b>本局内不再自动重新配对</b>（新开一局复位）。
    /// </summary>
    /// <param name="reason">只进日志的触发来源。</param>
    /// <returns>本来就没绑定则返回 <c>false</c>。</returns>
    public static bool Unbind(string reason = "external")
    {
        return TogetherPair.Unbind(reason);
    }

    // ======================================================================
    // 设置（生效值：联机时客户端跟随主机，和设置页 / 同步用的是同一份）
    // ======================================================================

    /// <summary>共生体开关的生效值。</summary>
    public static bool SymbiosisEnabled => TogetherSettingsSync.EffectiveSymbiosisEnabled;

    /// <summary>
    /// 合作人数上限的生效值（2~4）。
    /// </summary>
    /// <remarks>
    /// <b>历史字段：已经不影响任何判定</b>（现在没有名额限制，谁都能加入，加入的人自成一组，
    /// 上限由 <c>TogetherPair.MaxMembers</c> 硬截断）。保留只为兼容已经读它的外部 mod 和联机快照字段数。
    /// </remarks>
    public static int GroupSize => TogetherSettingsSync.EffectiveGroupSize;

    /// <summary>开局是否合并双方初始卡组的生效值。</summary>
    public static bool MergeStarterDecks => TogetherSettingsSync.EffectiveMergeStarterDecks;

    /// <summary>血量上限提升百分比的生效值（0~100）。</summary>
    public static int HpBonusPercent => TogetherSettingsSync.EffectiveHpBonusPercent;

    /// <summary>是否共享金币的生效值。</summary>
    public static bool ShareGold => TogetherSettingsSync.EffectiveShareGold;
}

/// <summary>能力的镜像策略（对外版，见 <see cref="TogetherApi.RegisterPowerMirrorOverride" />）。</summary>
public enum PowerMirrorPolicy
{
    /// <summary>组内每人一份（默认）：等价原版多人局里"每个人各买了一份能力"。</summary>
    Mirror,

    /// <summary>只留被施加的那一份，不复制给其他成员。</summary>
    SingleInstance,
}
