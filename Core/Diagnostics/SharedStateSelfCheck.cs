using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using Together.Core.Foundation;
using Together.Core.Shared.Deck;


namespace Together.Core.Diagnostics;
/// <summary>自检 / 对账日志：把状态按校验和的 <c>id</c> 打出来 —— 两端同一个 id 就是同一步骤。</summary>
/// <remarks>
/// <para>
/// <b>为什么用校验和的 id 当键</b>：本体的 <c>ChecksumTracker.GenerateChecksum</c> 严格按调用顺序递增分配
/// <c>id</c>，而"校验和必须每端调用同样次数"本身就是本体的硬要求 —— 所以这个 id 天然就是"第几步"，
/// 两端同一个 id 指的是同一件事。而 context 文案并不唯一（带房间 id、同名回合一局会出现很多次），光凭文案对不上。
/// </para>
/// <para>
/// <b>开关</b>：环境变量 <c>TOGETHER_SELFCHECK</c> 优先（<c>1</c> 开 / <c>0</c> 关）；<b>没设时共享局默认开</b> ——
/// 自动对账要求两端输出同一个日志集合，一端开一端关必然对不上。刷屏由"只在联机 + 共享激活时出声"控制。
/// 本机双人（Local Multi-Control 的回环主机）没有真正的对端、校验和不会报分叉，这时这份日志就是唯一的分叉探测器。
/// </para>
/// </remarks>
internal static class SelfCheck
{
    /// <summary>最近一次校验和的 id（0 = 还没打过 / 校验和未启用）。</summary>
    private static uint _checkpoint;

    /// <summary>最近一次校验和的 id，供其它诊断日志挂上"第几步"。</summary>
    public static uint CurrentCheckpoint => _checkpoint;

    /// <summary>是否输出。环境变量优先；未设置时 = 本局是不是共享局。</summary>
    public static bool Enabled
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("TOGETHER_SELFCHECK");
            if (string.Equals(env, "1", StringComparison.Ordinal))
            {
                return true;
            }

            if (string.Equals(env, "0", StringComparison.Ordinal))
            {
                return false;
            }

            return TogetherPair.IsActive;
        }
    }

    /// <summary>诊断日志的统一出口：只在自检开启时输出。</summary>
    /// <remarks>排查期加的那些 <c>[together][diag]</c> 日志都走这里；输出时自动带上当前的 <c>chk=</c>。</remarks>
    public static void Write(string message)
    {
        if (!Enabled)
        {
            return;
        }

        Log.Info(Tag(message));
    }

    /// <summary>给一行诊断带上"当前是第几步"（还没有 chk 时原样返回）。</summary>
    public static string Tag(string message)
    {
        return _checkpoint == 0 ? message : $"chk={_checkpoint} {message}";
    }

    /// <summary>校验和生成点的收口：记下 id，并输出两端可对账的状态行。</summary>
    internal static void Report(uint id, string context)
    {
        if (id == 0)
        {
            return;   // 校验和没启用（单人局 / 非联机）
        }

        _checkpoint = id;

        if (!Enabled || TogetherPair.Anchor is not { } anchor)
        {
            return;
        }

        // 归属漂移监视：只在"分布真的变了"时打一行（含上一跳 → 这一跳）。两端同一 chk 的分布
        // 应当逐字相同 —— 不同就是 owner 又被人整体改写了，这一行是下一次排查的第一现场。
        OwnerDriftWatch.OnCheckpoint(id, context, anchor);

        Log.Info(Line(id, context, "anchor", anchor));

        foreach (var echo in TogetherPair.Echoes)
        {
            Log.Info(Line(id, context, "echo", echo));
        }
    }

    /// <summary>一行对账：<c>[sync] chk=&lt;id&gt; ctx=&lt;context&gt; tag=together.state …</c>。</summary>
    private static string Line(uint id, string context, string who, Player player)
    {
        return $"[sync] chk={id} ctx={context} tag=together.state who={who} netId={player.NetId} {Describe(player)}";
    }

    private static string Describe(Player player)
    {
        var creature = player.Creature;
        var combat = player.PlayerCombatState;

        var powers = creature is null
            ? "-"
            : string.Join(",", creature.Powers.Select(p => $"{p.Id.Entry}={p.Amount}"));

        var piles = combat is null
            ? "-"
            : $"hand={combat.Hand.Cards.Count}"
              + $" draw={combat.DrawPile.Cards.Count}"
              + $" discard={combat.DiscardPile.Cards.Count}"
              + $" exhaust={combat.ExhaustPile.Cards.Count}"
              + $" play={combat.PlayPile.Cards.Count}"
              + $" energy={combat.Energy}";

        var orbs = combat?.OrbQueue is { } queue ? queue.Orbs.Count.ToString() : "-";

        // 主卡组（Deck）不在战斗堆里，本体 checksum 只报"某字段不同"看不出是哪张牌 →
        // 这里带上"张数 + 顺序指纹"：变牌 / 草蜢偷牌 / 事件改卡组这类问题，两端一比对就知道卡组是否真的不同。
        var deck = player.Deck;
        var deckText = deck is null
            ? "-"
            : $"{deck.Cards.Count}/{DeterministicCardOrder.Fingerprint(deck.Cards)}";

        // 卡组 Owner 分布（netId:张数）。牌 ID 一样但 owner 不同时，deck 指纹看不出来 ——
        // 而"按 card.Owner 判定的遗物钩子"（五轮书…）正是吃这个差异，所以这里单独打出来。
        var owners = deck is null
            ? "-"
            : string.Join(",", deck.Cards
                .GroupBy(card => card.Owner?.NetId ?? 0UL)
                .OrderBy(group => group.Key)
                .Select(group => $"{group.Key}:{group.Count()}"));

        return $"hp={creature?.CurrentHp}/{creature?.MaxHp} block={creature?.Block}"
               + $" powers=[{powers}] {piles} deck={deckText} owners=[{owners}] gold={player.Gold} orbs={orbs}";
    }
}

/// <summary>共享卡组 owner 分布的"变化监视"：只在分布真的变了的时候打一行。</summary>
/// <remarks>
/// 用途只有一个：<b>把"owner 整体被人改写"这件事变成时间线上的一行</b>。
/// 2026-09-25 的分歧 <c>#110</c> 里，两端的分布从本局第一个校验点就已经互为镜像，
/// 却没有对应的"归属改写"日志（那次改写走的是牌堆重建，见 <see cref="OwnerWriteProbe" />），
/// 于是只能靠翻整份 log 才知道它没变过。有了这一行，下一份 log 直接看"哪一跳变了、变化前长什么样"。
/// 它<b>只打印、不改状态</b>；真正的修复是 <c>SharedDeckOwnership.RepairCurrent</c> 在
/// "读档 / 配对激活 / 开战"三个确定性时机上做的。
/// </remarks>
internal static class OwnerDriftWatch
{
    private static string? _last;

    /// <summary>每个校验点比一次（含指纹，顺序变了也算变）。</summary>
    public static void OnCheckpoint(uint id, string context, Player anchor)
    {
        try
        {
            if (anchor.Deck is not { } deck)
            {
                return;
            }

            var signature = SharedDeckOwnership.DistributionOf(deck)
                            + "|" + DeterministicCardOrder.Fingerprint(deck.Cards);

            if (signature == _last)
            {
                return;
            }

            var previous = _last;
            _last = signature;

            if (previous is null)
            {
                // 基线一行：只在开了逐条取证时要（它本身不是信号，只是"监视器活着"的证明）。
                DriftLog.Info("drift.watch", $"共享卡组分布基线（chk={id}）：{signature}");
                return;
            }

            // ★ 这一行默认可见：它是"整副卡组被人改写"的**唯一默认信号**，不该被静默掉。
            CappedLog.Info(
                "drift.watch",
                $"共享卡组分布变化（chk={id} ctx={context}）：{previous} → {signature}");
        }
        catch (Exception)
        {
            // 监视失败绝不能影响校验和流程。
        }
    }
}

/// <summary>校验和生成点的自检钩子。</summary>
/// <remarks>
/// 必须显式给参数类型：<c>ChecksumTracker.GenerateChecksum</c> 有两个重载
/// （<c>(string, GameAction?)</c> 与 <c>(NetFullCombatState)</c>），
/// 只写 <c>nameof</c> 会让 Harmony 报 AmbiguousMatch，整个 PatchAll 直接失败。
/// </remarks>
[HarmonyPatch(
    typeof(ChecksumTracker),
    nameof(ChecksumTracker.GenerateChecksum),
    new[] { typeof(string), typeof(GameAction) })]
internal static class ChecksumSelfCheckPatch
{
    [HarmonyPostfix]
    private static void Postfix(string __0, ref NetChecksumData __result)
    {
        SelfCheck.Report(__result.id, __0);
    }
}
