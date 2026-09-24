using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Orbs;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

using Together.Core.Combat;
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
}

/// <summary>能力的镜像策略（对外版，见 <see cref="TogetherApi.RegisterPowerMirrorOverride" />）。</summary>
public enum PowerMirrorPolicy
{
    /// <summary>组内每人一份（默认）：等价原版多人局里"每个人各买了一份能力"。</summary>
    Mirror,

    /// <summary>只留被施加的那一份，不复制给其他成员。</summary>
    SingleInstance,
}
