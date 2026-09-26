using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using Together.Core.Alignment;
using Together.Core.Diagnostics;
using Together.Core.Foundation;
using Together.Core.Settings;

namespace Together.Core.Shared.Deck;

/// <summary>
/// 组内放宽（"牌进主卡组"这一条）：共享局里把"按 card.Owner 认领牌"的判据改成"组内任何成员的牌都算我的"。
/// </summary>
/// <remarks>
/// <para>
/// 挂点：本体所有"牌换了牌堆"的通知都汇聚到静态单点 Hook.AfterCardChangedPiles，
/// 里面只是 foreach(listener) await listener.AfterCardChangedPiles(card, oldPile, clonedBy)。
/// 挂这一处就能覆盖全部遗物/能力里那句 if (card.Owner != base.Owner) return，
/// 不用逐个遗物特例（五轮书 / 调节音叉 / 双截棍 / 卡戎之灰 都是这一句）。
/// </para>
/// <para>
/// 做法：只在"牌进主卡组"这一类上（五轮书那种判据）动手，具体放宽逻辑交给
/// <see cref="OwnerClaim" /> —— <b>逐监听者</b>判断它读不读 <c>card.Owner</c>，只给读的那些
/// 临时换成"自己的牌"的视角，不读的原样跑一次。
/// </para>
/// <para>
/// 注意：CardModel.Pile 由 _owner.Piles 推出，临时换 owner 期间 card.Pile 也按那个成员找堆 ——
/// 共享卡组下各成员的 Deck 本来就是同一份，能找到，判据才成立。
/// </para>
/// </remarks>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardChangedPiles))]
internal static class SharedHookOwnerWidenPatch
{
    /// <summary>CardModel 的 owner 字段（绕开 setter 的"已有主"守卫）。</summary>
    private static readonly AccessTools.FieldRef<CardModel, Player?> OwnerField =
        AccessTools.FieldRefAccess<CardModel, Player?>("_owner");

    /// <summary>我们自己在派发（避免再进 Prefix）。</summary>
    private static bool _reentrant;

    [HarmonyPrefix]
    private static bool Prefix(
        IRunState runState,
        ICombatState? combatState,
        CardModel card,
        PileType oldPile,
        AbstractModel? clonedBy,
        ref Task __result)
    {
        if (_reentrant || !TogetherPair.IsActive || card is null)
        {
            return true;
        }

        // 卡组"内容变了"的两种形态都会经过这一处：进卡组（牌现在在 Deck）和离开卡组（oldPile 是 Deck）。
        // 只有这两种情况才重记槽位表 —— 其余牌堆搬运（抽牌 / 弃牌 / 消耗）不该每次都算一遍指纹。
        // 记账点选在这里的理由：开局填充 / 战斗奖励 / 事件加牌 / 商店买牌 / 移除卡牌全都汇聚到这个钩子。
        if (oldPile == PileType.Deck || card.Pile?.Type == PileType.Deck)
        {
            SharedDeckOwnership.CaptureCurrent(oldPile == PileType.Deck ? "deck_leave" : "deck_entry");
        }

        if (card.Pile?.Type != PileType.Deck)
        {
            return true;
        }

        // 进主卡组 = 共享卡组的一员（开局之后抓牌 / 买牌也走这里）。登记与兼容开关无关：
        // 事件并发守卫要能认出"这张牌是我们的"，否则 mod 事件里的失效选择会漏兜、直接卡住。
        SharedDeckRegistry.Register(card);

        if (!TogetherSettingsSync.EffectiveCompatHookWiden)   // ★ 兼容模式：关掉 = 回到原版派发（仍两端一致）
        {
            return true;
        }

        var members = TogetherPair.Members().ToList();
        if (members.Count < 2)
        {
            return true;
        }

        // 混合局里普通玩家自己的牌不碰：只有"属于组内成员"的牌才做放宽。
        if (!OwnerClaim.IsSharedCard(card))
        {
            return true;
        }

        _reentrant = true;
        __result = WidenAsync(runState, combatState, card, oldPile, clonedBy);
        return false;
    }

    /// <summary>
    /// 照本体两轮派发（<c>AfterCardChangedPiles</c> → <c>AfterCardChangedPilesLate</c>），
    /// 但改成 <see cref="OwnerClaim" /> 的<b>逐监听者</b>视角切换：只有"方法体里读了 <c>card.Owner</c>"的监听者
    /// 才会临时看到"这是我的牌"，不读 owner 的监听者原样跑一次、不会被重复触发。
    /// </summary>
    /// <remarks>
    /// 早先那版是"整段派发按成员各跑 N 遍"（每个成员一遍、把卡的 owner 换成他）——
    /// 在那个钩子上大多数监听者确实都按 owner 认领，所以没出事；但只要有一个不判 owner 的监听者，
    /// 它就会被多触发 N-1 次。现在统一走结构判据，和"消耗 / 弃牌 / 打出 / 抽牌 / 伤害"那七个钩子同一套机制。
    /// </remarks>
    private static async Task WidenAsync(
        IRunState runState,
        ICombatState? combatState,
        CardModel card,
        PileType oldPile,
        AbstractModel? clonedBy)
    {
        try
        {
            var signature = new[] { typeof(CardModel), typeof(PileType), typeof(AbstractModel) };

            await OwnerClaim.Dispatch(
                OwnerClaim.Listeners(runState, combatState).ToList(),
                null,
                card,
                nameof(AbstractModel.AfterCardChangedPiles),
                signature,
                model => model.AfterCardChangedPiles(card, oldPile, clonedBy),
                pushModel: false);

            await OwnerClaim.Dispatch(
                OwnerClaim.Listeners(runState, combatState).ToList(),
                null,
                card,
                nameof(AbstractModel.AfterCardChangedPilesLate),
                signature,
                model => model.AfterCardChangedPilesLate(card, oldPile, clonedBy),
                pushModel: false);
        }
        finally
        {
            _reentrant = false;
        }
    }
}
