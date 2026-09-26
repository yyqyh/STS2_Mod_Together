using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;
using Together.Core.Diagnostics;
using Together.Core.Foundation;

namespace Together.Core.Shared.Deck;

/// <summary>
/// 共享卡组的 owner 修复：<b>每次 RunState 从存档重建之后</b>，按槽位表还原每张牌的 owner；表不可用才全钉锚点。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要</b>（本体事实）：<c>SerializableCard</c> <b>不带 owner 字段</b>，重建时走
/// <c>RunState.LoadCard(card, player)</c> → <c>AddCard(card, owner)</c> 把 Owner <b>硬写成"重建者"</b>；
/// 而共享卡组是"逐人写、逐人重建"的 → 同一批牌的 owner 两端必然漂移
/// （实测 <c>owners=[1:5, 131…:86]</c> → 进战斗重建后 <c>[131…:91]</c> → checksum #1 分歧）。
/// </para>
/// <para>
/// <b>为什么挂在 <c>FromSerializable</c> 的 Postfix 而不是 <c>Arm</c> 里</b>：
/// 那一刻 run saved data 刚导入、玩家牌堆已建好；而 <c>Arm</c> 只在"成组判定通过"时才走到那段，
/// 会被提前 return 拦掉（实测插在 <c>Arm</c> 里一行日志都没打出来）。
/// </para>
/// <para>
/// <b>锚点从哪来</b>：不能读 <c>TogetherPair.Anchor</c>（重建时它还指着旧局的 Player 实例）——
/// 必须现从 run 数据里的名单（<see cref="CoopLobbyData.RosterOf" />，随 run snapshot 两端一致）里，
/// 按 <c>RunState.Players</c> 顺序取第一个成员当锚点，这样两端算出来是同一个人。
/// </para>
/// <para>
/// <b>取舍（2026-09-26 起）</b>：现在优先用 <see cref="SharedDeckOwnerSlots" /> 那份"按 Deck 顺序的
/// owner 槽位数组 + 指纹"（随 run snapshot / 存档同步，两端各写一份、内容由同一份卡组状态算出）
/// 把每张牌还原成它<b>原本</b>的主人 —— 自然归属与两端一致性同时保住。
/// 只有槽位表不可用（老存档、卡组形状被别的路径改过）时才退回"全部钉锚点"：
/// 那时牌会失去自然归属（按 <c>card.Owner</c> 判定的遗物会按锚点统一计数），但两端仍一致。
/// </para>
/// </remarks>
[HarmonyPatch(typeof(RunState), nameof(RunState.FromSerializable))]
internal static class SharedDeckOwnerNormalizePatch
{
    [HarmonyPostfix]
    private static void Postfix(RunState __result)
    {
        try
        {
            if (__result is null)
            {
                return;
            }

            var roster = CoopLobbyData.RosterOf(__result);
            if (roster.Length < TogetherPair.MinMembers)
            {
                CappedLog.Info("own.deck", $"重建后归一：跳过（名单 {roster.Length} 人，还没成组或 run 数据还没导入）");
                return;   // 这局没有成组：不插手
            }

            Player? anchor = null;
            foreach (var player in __result.Players)
            {
                if (roster.Contains(player.NetId))
                {
                    anchor = player;   // Players 顺序两端一致 → 锚点两端一致
                    break;
                }
            }

            if (anchor?.Deck is not { } deck)
            {
                CappedLog.Info("own.deck", "重建后归一：跳过（名单里有成员，但在 Players 里找不到锚点）");
                return;
            }

            // 顺手登记"这张牌属于共享卡组"（事件并发守卫要靠它区分"我们的牌"和"别的 mod 的牌"）。
            foreach (var card in deck.Cards)
            {
                SharedDeckRegistry.Register(card);
            }

            // ★ 优先按"槽位表"还原（保住自然归属、两端同一份数据）；表不可用才退回"全部钉锚点"。
            // 详见 SharedDeckOwnership.Repair 的三分支说明。
            var restored = SharedDeckOwnership.Repair(__result, deck, anchor, "from_serializable");

            CappedLog.Info(
                "own.deck",
                restored > 0
                    ? $"重建后共享卡组 owner 还原：{restored}/{deck.Cards.Count} 张（锚点 netId={anchor.NetId}，"
                      + $"当前分布 {SharedDeckOwnership.DistributionOf(deck)}）"
                    : $"重建后共享卡组 owner 无需改动（{deck.Cards.Count} 张，分布 {SharedDeckOwnership.DistributionOf(deck)}）");
        }
        catch (Exception ex)
        {
            CappedLog.Info("own.deck", $"重建后 owner 归一失败（忽略）：{ex.GetType().Name}: {ex.Message}");
        }
    }
}
