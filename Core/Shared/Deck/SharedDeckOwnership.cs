using HarmonyLib;

using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

using STS2RitsuLib;
using STS2RitsuLib.RunData;

using Together.Core.Diagnostics;
using Together.Core.Foundation;

namespace Together.Core.Shared.Deck;

/// <summary>「共享卡组的每一张牌归谁」的槽位表：按 Deck 顺序一一对应，随 run snapshot / 存档同步。</summary>
/// <remarks>
/// <para>
/// <b>为什么需要它</b>：<c>SerializableCard</c> <b>不带 owner 字段</b>，牌堆从存档/快照重建时 owner 是
/// <b>"重建者"</b>（<c>RunState.AddCard(card, owner)</c> 里的 <c>card.Owner = owner</c>）。共享卡组是
/// "同一份卡组挂在多个人身上"，于是同一批牌在两端会被重建成<b>不同的人</b>——实测整副卡组
/// 一端全归锚点、另一端全归回声（互为镜像，从本局第一个校验点就不同）。
/// 两端 owner 相反会连带出：<c>CardCmd.Transform</c> 的"替换卡必须同 owner"校验只有一端通过
/// （另一端抛异常 → 那张牌卡在 Play 区、后续效果整段不跑 → checksum 分歧）。
/// </para>
/// <para>
/// <b>做法</b>：把"卡组合法变化的那一刻"的 owner 序列记进 run 槽位（两端各写一份、内容由同一份
/// 卡组状态算出），重建/开战时再按槽位<b>还原</b>。槽位表不可用（老存档、卡组形状被改过）时退回
/// "全部钉锚点"——这是两端的同一套规则，结果确定，最坏也只是丢掉自然归属，不会停在镜像上。
/// </para>
/// <para>
/// <b>key 一旦发布就不能改</b>（改了老存档读不到这块数据）：<c>shared_deck_owners</c> 定死。
/// </para>
/// </remarks>
public sealed class SharedDeckOwnerSlotData
{
    /// <summary>判据版本；改了取法就 +1，旧数据整份作废（走兜底分支）。</summary>
    public int RulesVersion { get; set; }

    /// <summary>登记当时卡组顺序的指纹（<c>DeterministicCardOrder.Fingerprint</c>）。</summary>
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>与 Deck 顺序一一对应的 owner netId；<c>0</c> = 未登记（按锚点兜底）。</summary>
    public List<ulong> Owners { get; set; } = [];
}

/// <summary>槽位表的读写（RitsuLib 的 <c>RunSavedData</c> run 槽位）。</summary>
internal static class SharedDeckOwnerSlots
{
    /// <summary>判据版本。<b>改 <see cref="SharedDeckOwnerSlotData"/> 的取法时必须 +1</b>，好让旧数据整份作废。</summary>
    private const int RulesVersion = 1;

    /// <summary>槽位 key（发布后不可改）。</summary>
    private const string Key = "shared_deck_owners";

    private static RunSavedData<SharedDeckOwnerSlotData>? _slot;

    /// <summary>在<b>已有的 mod 数据注册块里</b>登记槽位（调用点见 <c>CoopLobbyData.Register</c>）。</summary>
    public static void RegisterInto(RunSavedDataStore store)
    {
        try
        {
            _slot = store.Register(
                Key,
                () => new SharedDeckOwnerSlotData { RulesVersion = RulesVersion },
                new RunSavedDataOptions
                {
                    WritePolicy = RunSavedDataWritePolicy.WhenNonDefault,
                });

            Main.Logger.Info("[together] 共享卡组 owner 槽位已注册（shared_deck_owners）");
        }
        catch (Exception ex)
        {
            _slot = null;
            Main.Logger.Warn(
                $"[together] 共享卡组 owner 槽位注册失败（本次不保自然归属，只做锚点兜底）："
                + $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>卡组顺序指纹（和 <c>[sync]</c> 那行里的指纹同一套算法）。</summary>
    public static string FingerprintOf(CardPile deck)
    {
        return DeterministicCardOrder.Fingerprint(deck.Cards);
    }

    /// <summary>把<b>当前</b>卡组的 owner 序列记进槽位（内容没变就不写）。</summary>
    public static void Capture(RunState runState, CardPile deck, string reason)
    {
        if (_slot is null)
        {
            return;
        }

        try
        {
            // ★ 空卡组不记：卡组重建的中间态（逐玩家填充 / 牌堆被换掉的瞬间）会短暂为空，
            // 那一瞬间记下去等于"把真相抹掉"，下一次还原就会把整副牌钉到锚点。
            if (deck.Cards.Count == 0)
            {
                return;
            }

            var fingerprint = FingerprintOf(deck);
            // 读 owner 一律走字段读取：Owner getter 会 AssertMutable，遇到不可变模型会抛。
            var owners = deck.Cards.Select(card => SharedDeckOwnership.OwnerOf(card)?.NetId ?? 0UL).ToList();

            if (_slot.Get(runState) is { } current
                && current.RulesVersion == RulesVersion
                && current.Fingerprint == fingerprint
                && current.Owners.SequenceEqual(owners))
            {
                return;
            }

            _slot.Set(runState, new SharedDeckOwnerSlotData
            {
                RulesVersion = RulesVersion,
                Fingerprint = fingerprint,
                Owners = owners,
            });

            DriftLog.Info(
                "own.slots",
                $"共享卡组 owner 槽位表已记录（{reason}）：{Distribution(owners)} 指纹={fingerprint}");
        }
        catch (Exception ex)
        {
            CappedLog.Info("own.slots", $"槽位表记录失败（忽略）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>按槽位还原 owner；返回还原张数。表不存在 / 形状对不上时返回 <c>-1</c>（调用方走兜底）。</summary>
    public static int TryRestore(RunState runState, CardPile deck, string reason)
    {
        if (_slot is null)
        {
            return -1;
        }

        try
        {
            if (!_slot.TryGet(runState, out var data)
                || data is null
                || data.RulesVersion != RulesVersion
                || data.Owners.Count != deck.Cards.Count
                || data.Fingerprint != FingerprintOf(deck))
            {
                return -1;
            }

            var cards = deck.Cards.ToList();
            var restored = 0;

            for (var i = 0; i < cards.Count; i++)
            {
                var netId = data.Owners[i];
                var target = netId == 0UL ? null : PlayerOf(runState, netId);
                if (target is null)
                {
                    continue;   // 未登记 / 认不出这个 netId → 保持原样（兜底不在这里做）
                }

                if (SharedDeckOwnership.SetOwner(cards[i], target, $"restore:{reason}"))
                {
                    restored++;
                }
            }

            return restored;
        }
        catch (Exception ex)
        {
            CappedLog.Info("own.slots", $"槽位表还原失败（走兜底）：{ex.GetType().Name}: {ex.Message}");
            TogetherAlert.Notify("owner槽位表", $"按槽位还原共享卡组 owner 时抛异常：{ex.GetType().Name}: {ex.Message}");
            return -1;
        }
    }

    /// <summary>按 netId 在<b>这一份 run</b> 里找玩家（不用 <c>TogetherPair</c>：重建期它还指着旧实例）。</summary>
    private static Player? PlayerOf(RunState runState, ulong netId)
    {
        foreach (var player in runState.Players)
        {
            if (player.NetId == netId)
            {
                return player;
            }
        }

        return null;
    }

    /// <summary>owner 序列 → <c>netId:张数</c>（和 <c>[sync]</c> 行的写法一致，便于肉眼对拍）。</summary>
    public static string Distribution(IEnumerable<ulong> owners)
    {
        return string.Join(
            ",",
            owners.GroupBy(netId => netId)
                .OrderBy(group => group.Key)
                .Select(group => $"{group.Key}:{group.Count()}"));
    }
}

/// <summary>
/// 「给一张牌定 owner」的<b>唯一入口</b>，外加共享卡组的"还原 / 兜底 / 记录"三个动作。
/// </summary>
/// <remarks>
/// <para>
/// 归属写入全 mod 只允许走这里（内部就是本体的 <c>CardModel.GiveToAnotherPlayer</c>，它绕开
/// <c>Owner</c> setter 的"已有主就抛"守卫，是本体自己留的那条路）。集中一处的意义：
/// ① 每条改动都带原因进日志，出问题能一眼看出是谁改的；② 以后要额外维护什么（比如槽位表）
/// 不用再满项目找 <c>GiveToAnotherPlayer</c>。
/// </para>
/// <para>
/// <b>为什么默认"原地改"不动牌堆</b>：牌已经在正确的那口堆里时（进手牌对齐、按槽位还原卡组），
/// 只改 owner 就能让 <c>card.Pile</c> 按新 owner 找到同一口堆（组内成员的 Deck 本来就是同一份）；
/// 先摘牌反而会白白触发一轮堆事件。要<b>换堆</b>的路径（从共享堆回手、合并初始卡组）自己先
/// <c>RemoveFromCurrentPile</c>，再调这里。
/// </para>
/// </remarks>
internal static class SharedDeckOwnership
{
    /// <summary>CardModel 的 owner 字段（绕开 setter 的守卫；只读，用于日志/比较）。</summary>
    private static readonly AccessTools.FieldRef<CardModel, Player?> OwnerField =
        AccessTools.FieldRefAccess<CardModel, Player?>("_owner");

    /// <summary>读 owner 的字段值（<b>不</b>走 getter：getter 会 <c>AssertMutable</c>，不可变牌上会抛）。</summary>
    public static Player? OwnerOf(CardModel? card)
    {
        if (card is null)
        {
            return null;
        }

        try
        {
            return OwnerField(card);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>当前这一局的共享主卡组（锚点那一份）；非共享局返回 <c>null</c>。</summary>
    private static CardPile? SharedDeck()
    {
        return TogetherPair.IsActive ? TogetherPair.Anchor?.Deck : null;
    }

    /// <summary>这一局的 <c>RunState</c>（锚点上的）；拿不到返回 <c>null</c>。</summary>
    private static RunState? RunStateOrNull()
    {
        return TogetherPair.Anchor?.RunState as RunState;
    }

    /// <summary>把一张牌改成这位玩家所有（<b>不动牌堆</b>）。返回是否真的改了。</summary>
    public static bool SetOwner(CardModel? card, Player? player, string reason)
    {
        if (card is null || player is null)
        {
            return false;
        }

        var from = OwnerOf(card);
        if (ReferenceEquals(from, player))
        {
            return false;
        }

        card.GiveToAnotherPlayer(player);

        DriftLog.Info(
            "own.assign",
            $"归属改写（{reason}）：{card.Id.Entry} netId{from?.NetId} → netId{player.NetId}");
        return true;
    }

    /// <summary>换堆路径用的"先摘牌再改归属"（顺序照抄本体 <c>CardPileCmd.GiveToAnotherPlayer</c>）。</summary>
    public static bool MoveTo(CardModel? card, Player? player, string reason)
    {
        if (card is null || player is null)
        {
            return false;
        }

        var from = OwnerOf(card);
        if (ReferenceEquals(from, player))
        {
            return false;
        }

        card.RemoveFromCurrentPile(true);
        card.GiveToAnotherPlayer(player);

        DriftLog.Info(
            "own.assign",
            $"归属改写（换堆：{reason}）：{card.Id.Entry} netId{from?.NetId} → netId{player.NetId}");
        return true;
    }

    /// <summary>把当前共享卡组的 owner 序列记进槽位表（卡组合法变化之后调用）。</summary>
    public static void CaptureCurrent(string reason)
    {
        if (RunStateOrNull() is { } runState && SharedDeck() is { } deck)
        {
            SharedDeckOwnerSlots.Capture(runState, deck, reason);
        }
    }

    /// <summary>
    /// 修当前这一局的共享卡组归属：先按槽位还原，槽位不可用则全部钉锚点，最后重记一次槽位。
    /// </summary>
    /// <remarks>
    /// 调用点是"确定性的时机"——两端都会走到、而且顺序一致：
    /// 读档重建之后（<c>RunState.FromSerializable</c>）、配对激活之后（<c>Arm</c>）、
    /// 每场战斗开始填充牌堆之前（<c>PopulateCombatState</c> 的锚点那一次）。
    /// 这样"别处的整副重写"最早在下一次战斗开始时就被纠回来，而不会一路带到校验和里。
    /// </remarks>
    public static int RepairCurrent(string reason)
    {
        if (!TogetherPair.IsActive
            || TogetherPair.Anchor is not { } anchor
            || anchor.RunState is not RunState runState
            || anchor.Deck is not { } deck)
        {
            return 0;
        }

        return Repair(runState, deck, anchor, reason);
    }

    /// <summary>对指定卡组做"还原 / 兜底 + 记录"（重建期用：锚点由调用方按名单算，不读 <c>TogetherPair</c>）。</summary>
    public static int Repair(RunState runState, CardPile deck, Player anchor, string reason)
    {
        var restored = SharedDeckOwnerSlots.TryRestore(runState, deck, reason);

        if (restored >= 0)
        {
            if (restored > 0)
            {
                CappedLog.Info(
                    "own.repair",
                    $"共享卡组 owner 按槽位还原 {restored} 张（{reason}）→ {DistributionOf(deck)}");
            }

            return restored;
        }

        // 没有可用槽位表（老存档 / 卡组形状被别的路径改过）→ 全部钉锚点：
        // 这是两端算得出的同一套规则（锚点 = 名单 ∩ Players 顺序的第一个），结果确定。
        var pinned = 0;
        foreach (var card in deck.Cards.ToList())
        {
            if (SetOwner(card, anchor, $"pin:{reason}"))
            {
                pinned++;
            }
        }

        CappedLog.Info(
            "own.repair",
            pinned > 0
                ? $"共享卡组 owner 无可用槽位表 → 全部钉锚点 {pinned} 张（{reason}）→ {DistributionOf(deck)}"
                : $"共享卡组 owner 无需修复（{reason}，无可用槽位表但已全归锚点）");

        SharedDeckOwnerSlots.Capture(runState, deck, $"pin:{reason}");
        return pinned;
    }

    /// <summary>卡组当前 owner 分布（<c>netId:张数</c>）。</summary>
    public static string DistributionOf(CardPile? deck)
    {
        return deck is null
            ? "<null>"
            : SharedDeckOwnerSlots.Distribution(deck.Cards.Select(card => OwnerOf(card)?.NetId ?? 0UL));
    }
}

/// <summary>校验和生成之前，先把共享卡组的 owner 按槽位表修回来。</summary>
/// <remarks>
/// <para>
/// <b>为什么需要（2026-09-26 实测定位）</b>：本体的 <c>CombatStateSynchronizer</c> <b>每次房间切换</b>
/// 都会"清空卡组 + 逐张 <c>RunState.LoadCard(card, owner)</c> 重建"，而重建时 <c>owner</c> 是
/// <b>sync 来源的那名玩家</b>（硬的 <c>card.Owner = owner</c>，不走 <c>GiveToAnotherPlayer</c>）。
/// 于是共享卡组会被整副写成"某一个人的牌"——实测 <c>chk=54</c>（<c>ctx=Exiting event room …</c>）
/// 那一刻分布从 <c>1:31,回声:1</c> 变成 <c>回声:32</c>，调用栈是
/// <c>&lt;WaitForSync&gt;d__18.MoveNext ← CombatStateSynchronizer.CheckSyncCompleted ← OnSyncPlayerMessageReceived</c>。
/// </para>
/// <para>
/// 它发生在<b>事件房退出</b>这一段、不在"开战"那一刻，所以 <c>combat_start</c> 那次修复兜不住这个窗口；
/// 而每个房间切换的收尾一定会打一次校验和，所以挂在"校验和之前"能把这段窗口收干净 ——
/// <b>每个 sync 点之前 owner 都与槽位表一致 → 两端必然逐字相同</b>。
/// 只改卡组那口堆里各张牌的 owner，<b>不动任何牌堆的内容与顺序</b>。
/// </para>
/// </remarks>
[HarmonyPatch(
    typeof(ChecksumTracker),
    nameof(ChecksumTracker.GenerateChecksum),
    new[] { typeof(string), typeof(GameAction) })]
internal static class CheckpointOwnerRepairPatch
{
    [HarmonyPrefix]
    private static void Prefix()
    {
        try
        {
            SharedDeckOwnership.RepairCurrent("checkpoint");
        }
        catch (Exception)
        {
            // 修复失败最多是"这一跳不修"，绝不能让校验和本身出问题。
        }
    }
}
