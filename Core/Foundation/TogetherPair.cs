using System.Text.Json;

using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib.Networking.Sidecar;
using Together.Core.Alignment;
using Together.Core.Api;
using Together.Core.Common;
using Together.Core.Diagnostics;
using Together.Core.Settings;
using Together.Core.Shared.Body;
using Together.Core.Shared.Deck;
using Together.Core.Shared.Gold;
using Together.Core.Shared.Orb;
using Together.Core.Shared.Pet;
using Together.Core.Shared.Power;
using Together.Core.Ui;

namespace Together.Core.Foundation;
/// <summary>
/// 共生体的「成员注册表」：谁是锚点、谁是回声。
/// </summary>
/// <remarks>
/// <b>锚点（<see cref="Anchor" />）</b>是权威实例持有者——主卡组与四个战斗牌堆都挂在它身上；
/// <b>回声（<see cref="Echoes" />）</b>是组里其他人，访问入口被重定向到锚点，但手牌与能量保持独立。
/// 人数 = 选人界面按了「加入合作模式」的人数（≥2 即成组，没有名额限制，最多 <see cref="MaxMembers" /> 人）。
/// <b>不用 <c>LocalContext</c> 之类的本机视角来决定锚点</b>：两端必须算出同一个答案，否则第一次抽牌就分叉；
/// <c>RunState.Players</c> 的顺序来自大厅，两端天然一致，也会写进存档。
/// </remarks>
internal static class TogetherPair
{
    /// <summary>成组的最少人数（只有一个人按了按钮 = 不成组，按原版打）。</summary>
    public const int MinMembers = 2;

    /// <summary>成组的最多人数（设置里可填的上限）。</summary>
    public const int MaxMembers = 4;

    private static IRunState? _armedRunState;
    private static Player? _anchor;
    private static readonly List<Player> _echoes = [];
    private static PlayerCombatState? _anchorCombatState;

    /// <summary>本局是否已被外部 mod"解绑"（解绑后读档 / 重连都不再自动重新配对，新开一局复位）。</summary>
    private static bool _duelUnbound;

    /// <summary>
    /// 激活配对之前，每个回声自己的主卡组实例。
    /// </summary>
    /// <remarks>
    /// 解绑时要把回声的 <c>Deck</c> backing field 换回它自己那一份，否则回声会继续读到锚点的共享卡组、
    /// 分牌等于白做。必须在"把回声的 Deck 换成锚点那份"<b>之前</b>抓。
    /// </remarks>
    private static readonly Dictionary<Player, CardPile> OriginalDecks = [];

    /// <summary><c>Player.Piles</c> 是首次访问即固化的缓存数组，需要在激活时清一次。</summary>
    private static readonly AccessTools.FieldRef<Player, CardPile[]?> RunPileCache =
        AccessTools.FieldRefAccess<Player, CardPile[]?>("_runPiles");

    /// <summary><c>Player.Deck</c> 是 get-only 自动属性，直接换它的 backing field。</summary>
    /// <remarks>
    /// 必须换字段、不能只改 getter：界面（顶部卡组按钮、卡组界面）初始化时会抓一次
    /// <c>PileType.Deck.GetPile(player)</c> 把 <c>CardPile</c> 引用存进自己的字段、之后只看那个引用；
    /// 只重定向 getter 的话这份"旧引用"永远是回声自己的卡组（实测"p2 只显示 9 张初始卡"）。
    /// </remarks>
    private static readonly AccessTools.FieldRef<Player, CardPile> DeckField =
        AccessTools.FieldRefAccess<Player, CardPile>("<Deck>k__BackingField");

    /// <summary>
    /// 本局是否真的在"共用身体"：配对已武装（锚点 + ≥1 回声）**且这是联机局**。
    /// </summary>
    /// <remarks>
    /// 全 mod 的总闸门：镜像（血量/格挡/上限/能力/金币）、球位、召唤物、归属归一、事件并发保护……
    /// 都只看这一个属性，所以判定写在这里、只写一次。
    /// <b>为什么必须带"联机"这一条</b>：配对按设计"非共生体局不清空"（避免读档/重连时误清），
    /// 于是单人局里可能还留着上一局联机的配对，`IsActive` 为真会让补丁去动单人局的数据 ——
    /// 最典型是"草蜢偷牌把上一局的玩家 creature 塞进 targets → 本体 NRE 崩溃"（实测 2026-09-23 11:28）。
    /// </remarks>
    public static bool IsActive
    {
        get
        {
            if (_anchor is null || _echoes.Count == 0)
            {
                return false;
            }

            return RunManager.Instance?.NetService is { } net && net.Type.IsMultiplayer();
        }
    }

    public static Player? Anchor => _anchor;

    /// <summary>组里除锚点以外的成员（1~3 个）。</summary>
    public static IReadOnlyList<Player> Echoes => _echoes;

    /// <summary>组内总人数（未激活时是 0）。</summary>
    public static int MemberCount => _anchor is null ? 0 : _echoes.Count + 1;

    /// <summary>锚点当前的 <see cref="PlayerCombatState" />；所有回声的四个牌堆都指向它。</summary>
    public static PlayerCombatState? AnchorCombatState => _anchorCombatState;

    /// <summary>在<b>跑局构造完成之后</b>激活共生体。调用点在 <c>RunStateReadyPatch</c>（新局 / 读档两个入口）。</summary>
    /// <remarks>
    /// <b>激活时机是这套设计里最容易踩的坑，必须保持"晚于 RunState 构造"。</b>
    /// <c>RunState.CreateShared</c> 的顺序是"先设 p1 的 RunState，再遍历牌组给每张卡设 owner"：
    /// <code>
    /// foreach (Player player in players) {
    ///     player.RunState = runState;                    // p1 先拿到 RunState
    ///     foreach (CardModel card in player.Deck.Cards)  // ← 若此时已激活，p2.Deck 就是 p1 的卡组
    ///         runState.AddCard(card, player);            // → 同一张牌被设两次 owner → 抛异常
    /// }
    /// </code>
    /// 早期版本在 <c>Refresh(player.RunState)</c> 里懒激活，正好命中这一点，结果
    /// <c>InvalidOperationException: Card ... already has an owner</c> → 开局中断、黑屏。
    /// </remarks>
    public static void Arm(IRunState? runState, bool isNewRun = false)
    {
        if (runState is null || runState is NullRunState)
        {
            SelfCheck.Write("[together][diag] Arm：还没有跑局，保持现有配对");
            return;
        }

        if (_duelUnbound)
        {
            if (isNewRun)
            {
                // 新一局开始：上一局的解绑标记只负责本局，不能跨局拦人。
                _duelUnbound = false;
            }
            else
            {
                SelfCheck.Write("[together][diag] Arm：本局已被外部 mod 解绑，跳过读档/重连重新配对");
                return;
            }
        }

        if (ReferenceEquals(runState, _armedRunState))
        {
            return;
        }

        var players = runState.Players;
        var seed = SeedOf(runState);

        // ① 外部规则优先（别的 mod 可以要求"正好 2 人 + 指定角色"就成组，见 TogetherApi.RegisterPairRule）；
        // ② 没有规则命中 → 读「本局名单」（RitsuLib 的 RunSavedData 槽位，随 run snapshot 下发）。
        // 这份名单是**开局快照里的同一份数据**，所以两端读到的必然一致（以前那份 sidecar 广播才可能一端有一端没有）。
        var picked = TogetherApi.SelectMembersByRule(runState) is { } ruled
            ? [.. ruled]
            : PickedFromRoster(players, runState);

        // 名单自检：这一行两端应该完全一样；不一样就说明**存档/快照数据本身**被谁改了。
        var listed = CoopLobbyData.RosterOf(runState);
        var missing = players.Count - listed.Length;
        CappedLog.Info(
            "arm.pick",
            $"Arm：本局名单 {listed.Length} 人[{string.Join(",", listed)}]"
            + $" runState=[{string.Join(",", players.Select(p => p.NetId))}]"
            + $" 选定 {picked.Count} 人[{string.Join(",", picked.Select(p => p.NetId))}]"
            + (listed.Length > 0 && missing > 0
                ? $"    ⚠ 有 {missing} 人没登记：可能是没按「加入合作模式」，也可能是两端 together 版本不一致"
                : ""));

        // 读档 / 重连时选人界面根本没出现过，成员名单是空的 —— 按存档种子找回。
        // 否则按共生体写出来的存档会被当成普通联机局加载（血量/卡组不再共享）。
        if (picked.Count < MinMembers && !isNewRun
            && TogetherSettingsStore.FindSymbioticRun(SeedOf(runState)) is { Length: >= MinMembers } remembered)
        {
            picked.Clear();
            foreach (var player in players)
            {
                if (remembered.Contains(player.NetId))
                {
                    picked.Add(player);
                }
            }

            Log.Info(
                $"[together] 读档：按种子找回共生体成员 [{string.Join(",", remembered)}]"
                + $" → 本局命中 {picked.Count} 人");
        }

        if (picked.Count < MinMembers)
        {
            // 关键：**不要在这里清空已激活的配对**。
            // 除了"新开一局"，读档检查、多角色会话重建等路径也会构造 RunState，
            // 早期版本会在这里把配对清掉且不重新激活 —— 表现就是"战斗中途回声突然不共享了"
            // （回声从自己那份空牌堆抽牌 → 一张都抽不到）。
            // 旧配对的 Player 对象已经作废，留着不会误伤新局（IsMember 比的是对象引用）。
            SelfCheck.Write(
                $"[together][diag] Arm：本局没有共生体（players={players.Count}"
                + $" 已确定={picked.Count}），保持现有配对不变");
            return;
        }

        _anchor = picked[0];
        _echoes.Clear();
        _echoes.AddRange(picked.Skip(1));
        _armedRunState = runState;
        _anchorCombatState = null;

        // 解绑时要靠这份记录把回声的卡组换回去；必须在"字段替换"之前抓。
        OriginalDecks.Clear();
        foreach (var echo in _echoes)
        {
            OriginalDecks[echo] = DeckField(echo);
        }

        // 「开局一次性操作」= 合并初始卡组 + 血量上限提升（+ 金币求和）。它们**必须只做一次**：
        //  - 合并：存档按 player.Deck 序列化，而回声的 Deck getter 早已重定向到共享卡组，
        //    所以存档里每人卡组都是同一份；再合并一次 = **翻倍**（实测重连一次 51 → 102）。
        //  - 血量上限：已写进存档，再抬一次会越滚越大。
        //
        // 判据必须同时满足两点，缺一不可：
        //  ① **不能只看 isNewRun**：新局第一次 Arm 常常因为"投票名单还没同步到"而不成组就 return 了，
        //     之后同局内每次 RunState 重建都走 FromSerializable（isNewRun=false）→ 这三项**永远补不上**
        //     （实测：新局共享卡组只有锚点那 10~11 张，回声的牌从没并进来）。
        //  ② **不能靠对象引用判断"并过没"**：读档 / 重连时 RunState 从存档反序列化，
        //     回声的 Deck 会变成新的 CardPile 对象（内容相同、引用不同）→ 引用比会误判成"没并过"
        //     → 重复合并（实测 22 → 54 张）、重复求和（198 → 490）、重复加血。
        // 所以改成"做完就打标记、只看标记"：做完一次立刻落标记 → 重建 / 重连 / 读档都不会重复。
        // ★ 标记放在 **run 自己的槽位** 里（随 snapshot 两端一致、随存档恢复）：
        // 本机内存 / 本机设置文件都算"本机状态"，两端可能给出不同答案 ——
        // 实测那样会"一端合并了、一端没合并"（chk=1 就分歧：14 张/106 血 vs 4 张/66 血）。
        var needsOpeningSetup = !CoopLobbyData.WasOpeningSetupDone(runState);

        if (needsOpeningSetup)
        {
            if (TogetherSettingsSync.EffectiveMergeStarterDecks)
            {
                foreach (var echo in _echoes)
                {
                    MergeStarterDeckInto(echo, picked[0]);
                }
            }

            ApplyHpBonus(picked[0], _echoes);

            // 立刻把标记写进 run 槽位：之后任何一次 RunState 重建 / 重连 / 读档都不会再重复这三项。
            CoopLobbyData.MarkOpeningSetupDone(runState);
        }

        // 把每个回声的主卡组字段直接换成锚点那一份（理由见 DeckField 的注释）。
        foreach (var echo in _echoes)
        {
            DeckField(echo) = picked[0].Deck;
            RunPileCache(echo) = null;
        }

        // 共享卡组是同一份，而进阶的"进阶之灾"是**逐玩家**往卡组里塞的 → 会塞成好几张。
        // 新开局的路径由 AscensionBaneDedupePatch 直接管；这里再对齐一次是为了读档/重连
        // （FromSerializable 那条路不会调 ApplyEffectsTo，旧存档里的重复会被原样带回来）。
        AscensionBaneDedupePatch.DedupeSharedDeck(picked[0]);

        // ★ 共享卡组 owner 收口（必须在"回声的 Deck 字段已换成锚点那份"之后）：
        // ① 先按槽位表还原每张牌原本的主人（没有表就退回全钉锚点，两端同一规则）；
        // ② 再把当前状态记回槽位表 —— 开局合并/进阶之灾去重都改过卡组，此刻是新的基线。
        // 这一步是"整副卡组被别处重写成同一个人的牌"的兜底：最迟在配对激活时纠回来。
        if (picked[0].RunState is RunState armedRunState && picked[0].Deck is { } sharedDeck)
        {
            SharedDeckOwnership.Repair(armedRunState, sharedDeck, picked[0], "arm");
            SharedDeckOwnerSlots.Capture(armedRunState, sharedDeck, "arm");
        }

        // 金币共享（可选）：新局把所有人的起始金币加起来（99 × n），读档/重连只做对齐。
        GoldMirror.OnArm(needsOpeningSetup);

        // 新开一局：把成员按种子记下来，供以后读档/重连找回。
        if (needsOpeningSetup)
        {
            TogetherSettingsStore.RememberSymbioticRun(SeedOf(runState), picked.Select(p => p.NetId));
        }

        Log.Info(
            $"[together] 共生体已激活：anchor={Describe(picked[0])} "
            + $"echoes=[{string.Join(",", _echoes.Select(Describe))}]"
            + $"（大厅共 {players.Count} 人，共享卡组 {picked[0].Deck.Cards.Count} 张，"
            + $"开局一次性操作={(needsOpeningSetup ? "本次执行（合并卡组/加血量上限/金币求和）" : "跳过（读档或已做过）")}）");

        // 兼容层补扫：有些 mod 的程序集可能晚于 mod 初始化才被加载，
        // 进局时再扫一遍（已扫过的程序集直接跳过，成本只有几十个字符串比较）。
        SameOwnerCheckCompat.Apply("run_armed");
    }

    /// <summary>
    /// 成员 = 本局名单（RitsuLib 的 RunSavedData 槽位）∩ <c>RunState.Players</c>，按 <c>Players</c> 顺序排。
    /// </summary>
    /// <remarks>
    /// 名单是**开局快照里的数据**（大厅暂存 → run snapshot），两端读到的天然是同一份，
    /// 所以这里不再需要任何"等广播 / 按种子找回 / 落后端补跑"的兜底 —— 那些都是 sidecar 时代的东西，已经删掉。
    /// </remarks>
    private static List<Player> PickedFromRoster(IReadOnlyList<Player> players, IRunState runState)
    {
        var roster = CoopLobbyData.RosterOf(runState);
        var picked = new List<Player>(MaxMembers);

        foreach (var player in players)
        {
            if (roster.Contains(player.NetId))
            {
                picked.Add(player);
            }
        }

        CappedLog.Info(
            "arm.pick.roster",
            $"按本局名单选定成员：[{string.Join(",", roster)}] → 本局命中 {picked.Count} 人");
        return picked;
    }

    // ======================================================================
    // 解绑（外部 mod 入口：进 PVP 决斗等）
    // ======================================================================

    /// <summary>对外入口：本局解除共生体绑定（见 <see cref="UnbindForDuel" />）。</summary>
    internal static bool Unbind(string reason)
    {
        return UnbindForDuel(reason);
    }

    /// <summary>
    /// 本局解除共享：关闭共生体开关、清空成员名单并广播，把当前共享卡组按奇偶分成两副，
    /// 并且<b>本局内不再自动重新配对</b>（读档 / 重连都不会复活；新开一局复位）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 调用后 <see cref="IsActive" /> / <see cref="IsMember" /> 立刻回到普通联机局语义，
    /// 镜像、共享牌堆、事件并发保护整体停掉，但不影响已经打出去的牌。
    /// </para>
    /// <para>
    /// 分牌口径：把解绑瞬间的共享主卡组按当前顺序列出来（锚点卡组在前、回声卡组在后），
    /// <b>1-based 序号奇数给 P1（锚点）、偶数给 P2（回声）</b>。决斗事件在事件房里触发、不在战斗中，
    /// 所以这里只切主卡组；下一场战斗会按各自的新卡组重建抽 / 弃 / 消耗堆。
    /// </para>
    /// <para>
    /// 超过 2 人时 PVP 没有意义：只把回声的卡组字段还原成各自的，不做奇偶分牌，并打一条警告。
    /// </para>
    /// </remarks>
    public static bool UnbindForDuel(string reason)
    {
        if (MemberCount < MinMembers)
        {
            Log.Info($"[together] 解绑：当前没有激活的共生体配对（{reason}）");
            return false;
        }

        var netService = RunManager.Instance?.NetService;

        TogetherSettingsStore.SetSymbiosisEnabled(false);
        CoopLobbyData.ClearRoster(_armedRunState);
        TogetherSettingsSync.PublishHostSettings(netService, reason);
        _duelUnbound = true;

        var memberCount = MemberCount;
        DeactivateAndSplitForDuel();

        Log.Info($"[together] 解绑完成（{reason}）：原有 {memberCount} 人；共生体开关已关闭，本局名单已清空");
        return true;
    }

    private static void DeactivateAndSplitForDuel()
    {
        var anchor = _anchor;
        var echoes = _echoes.ToList();

        if (anchor is not null && echoes.Count == 1)
        {
            SplitSharedDeckByParity(anchor, echoes[0]);
        }
        else
        {
            RestoreEchoDecks(echoes);

            if (anchor is not null && echoes.Count > 1)
            {
                Log.Warn(
                    $"[together] 解绑：共生体有 {echoes.Count + 1} 人，PVP 只支持两人；"
                    + "已恢复回声卡组但未做奇偶分牌，请带 log 反馈");
            }
        }

        ClearPairingState();
    }

    /// <summary>把当前配对引用全部清掉（不处理卡组拆分；拆分只走解绑那条路）。</summary>
    private static void ClearPairingState()
    {
        _anchor = null;
        _echoes.Clear();
        _anchorCombatState = null;
        _armedRunState = null;
        OriginalDecks.Clear();
    }

    /// <summary>
    /// 把两个人的共享主卡组按 1-based 序号奇偶拆开：奇数张 → 锚点（P1），偶数张 → 回声（P2）。
    /// </summary>
    private static void SplitSharedDeckByParity(Player p1, Player p2)
    {
        var p1Deck = p1.Deck;
        var p2Deck = OriginalDecks.TryGetValue(p2, out var originalDeck) ? originalDeck : DeckField(p2);

        // 先换回回声自己的卡组字段：此后 p2.Deck / 卡组界面读到的才是分给他的那一副。
        DeckField(p2) = p2Deck;
        RunPileCache(p2) = null;

        if (ReferenceEquals(p1Deck, p2Deck))
        {
            Log.Warn("[together] 解绑：P1/P2 卡组是同一实例，无法按奇偶拆分（请带 log 反馈）");
            return;
        }

        var ordered = new List<(CardModel Card, CardPile Source)>();
        var seen = new HashSet<CardModel>(ReferenceEqualityComparer.Instance);

        foreach (var card in p1Deck.Cards.ToList())
        {
            if (seen.Add(card))
            {
                ordered.Add((card, p1Deck));
            }
        }

        foreach (var card in p2Deck.Cards.ToList())
        {
            if (seen.Add(card))
            {
                ordered.Add((card, p2Deck));
            }
        }

        p1Deck.Clear(silent: true);
        p2Deck.Clear(silent: true);

        for (var i = 0; i < ordered.Count; i++)
        {
            var card = ordered[i].Card;
            var toP1 = (i + 1) % 2 == 1;
            var targetPile = toP1 ? p1Deck : p2Deck;

            targetPile.AddInternal(card, -1, silent: true);
            SharedDeckOwnership.SetOwner(card, toP1 ? p1 : p2, "unbind_split");
        }

        Log.Info(
            $"[together] 解绑分牌：共享卡组 {ordered.Count} 张 → "
            + $"P1({Describe(p1)})={p1Deck.Cards.Count} 张（奇数位），"
            + $"P2({Describe(p2)})={p2Deck.Cards.Count} 张（偶数位）");
    }

    private static void RestoreEchoDecks(IEnumerable<Player> echoes)
    {
        foreach (var echo in echoes)
        {
            if (!OriginalDecks.TryGetValue(echo, out var deck))
            {
                continue;
            }

            DeckField(echo) = deck;
            RunPileCache(echo) = null;
        }
    }

    /// <summary>把回声的初始卡组并进锚点的卡组：共享卡组 = p1 + p2 + …。</summary>
    /// <remarks>
    /// 逐张走"从原卡组摘掉 → 放进目标卡组 → 归属改成锚点"：牌还是那些牌、只是换了一副卡组，不留无主牌。
    /// 必须发生在"回声的 Deck 字段被换成锚点那份"<b>之前</b>，且必须直接读 <c>Deck</c> 的<b>字段</b>：
    /// 此时 <c>_anchor/_echoes</c> 已赋值，回声的 getter 会被重定向到锚点，用 getter 读会拿到目标那副（合并空转）。
    /// </remarks>
    private static void MergeStarterDeckInto(Player from, Player to)
    {
        var fromDeck = DeckField(from);
        var toDeck = to.Deck;
        var cards = fromDeck.Cards.ToList();

        if (ReferenceEquals(fromDeck, toDeck))
        {
            Log.Warn("[together] 共生体合并初始卡组：源与目标是同一副卡组，已跳过（不应该发生，请带 log 反馈）");
            return;
        }

        foreach (var card in cards)
        {
            fromDeck.RemoveInternal(card, silent: true);
            toDeck.AddInternal(card, -1, silent: true);
            SharedDeckOwnership.SetOwner(card, to, "merge_starter_deck");
        }

        Log.Info(
            $"[together] 共生体合并初始卡组：把 {from.Character.GetType().Name} 的 {cards.Count} 张"
            + $"并入共享卡组（合计 {toDeck.Cards.Count} 张）");
    }

    /// <summary>共生体血量上限提升：把每个回声最大生命的 <c>HpBonusPercent</c>% 加进共享血池。</summary>
    /// <remarks>上限和当前血一起抬（否则开局不是满血）；回声那边由 <see cref="BodyMirror" /> 对齐。</remarks>
    private static void ApplyHpBonus(Player anchor, IReadOnlyList<Player> echoes)
    {
        var percent = TogetherSettingsSync.EffectiveHpBonusPercent;
        if (percent <= 0 || echoes.Count == 0)
        {
            return;
        }

        var to = anchor.Creature;
        if (to is null)
        {
            return;
        }

        // 必须在抬锚点之前读：一旦抬上去，镜像会立刻把其他人那份也改成新值。
        var sourceMaxHp = 0;
        foreach (var echo in echoes)
        {
            if (echo.Creature is { } creature)
            {
                sourceMaxHp += creature.MaxHp;
            }
        }

        var bonus = (int)Math.Round(sourceMaxHp * (percent / 100.0), MidpointRounding.AwayFromZero);
        if (bonus <= 0)
        {
            return;
        }

        to.SetMaxHpInternal(to.MaxHp + bonus);
        to.SetCurrentHpInternal(to.CurrentHp + bonus);
        BodyMirror.SyncAll();

        Log.Info(
            $"[together] 共生体血量上限提升：{echoes.Count} 位回声最大生命合计 {sourceMaxHp} 的 {percent}%"
            + $" = +{bonus} → 共享血池 {to.CurrentHp}/{to.MaxHp}");
    }

    public static bool IsAnchor(Player? player)
    {
        return player is not null && ReferenceEquals(player, _anchor);
    }

    /// <summary>组里的"非锚点成员"。牌堆/卡组重定向、共享堆归属等判断都用它。</summary>
    public static bool IsEcho(Player? player)
    {
        if (player is null || _anchor is null)
        {
            return false;
        }

        foreach (var echo in _echoes)
        {
            if (ReferenceEquals(echo, player))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>这位玩家是不是组内成员（含锚点）。</summary>
    public static bool IsMember(Player? player)
    {
        return IsAnchor(player) || IsEcho(player);
    }

    /// <summary>
    /// 按 netId 找当前配对里的成员实例（找不到返回 <c>null</c>）。
    /// </summary>
    /// <remarks>
    /// 给"旧 Player 实例"认领用：保存 / 读档 / 重连会重建整个 <c>RunState</c>，
    /// 重建窗口里新旧实例会并存一小会儿，而成员判定是<b>引用相等</b>（见 <see cref="IsAnchor" />）。
    /// 拿着旧实例来的逻辑（实测是金币 setter）可以先在这里认回当前实例，再照常处理。
    /// </remarks>
    public static Player? MemberByNetId(ulong netId)
    {
        if (_anchor is not null && _anchor.NetId == netId)
        {
            return _anchor;
        }

        foreach (var echo in _echoes)
        {
            if (echo.NetId == netId)
            {
                return echo;
            }
        }

        return null;
    }

    /// <summary>组内所有成员（锚点在前）。</summary>
    public static IEnumerable<Player> Members()
    {
        if (_anchor is null)
        {
            return [];
        }

        return new[] { _anchor }.Concat(_echoes);
    }

    /// <summary>组内<b>除这位以外</b>的其他成员（镜像就是往这些人身上推）。</summary>
    public static IEnumerable<Player> OthersOf(Player? player)
    {
        if (!IsMember(player))
        {
            return [];
        }

        return Members().Where(member => !ReferenceEquals(member, player));
    }

    /// <summary>组内除这位以外的其他 creature。</summary>
    public static IEnumerable<Creature> OtherCreaturesOf(Creature? creature)
    {
        return creature is null ? [] : OthersOf(creature.Player).Select(p => p.Creature);
    }

    /// <summary>记录锚点当前的战斗状态；锚点重建 <see cref="PlayerCombatState" /> 时必须调用，
    /// 否则回声会一直指向上一场战斗的牌堆。</summary>
    public static void SetAnchorCombatState(PlayerCombatState? state)
    {
        _anchorCombatState = state;
    }

    private static string Describe(Player player)
    {
        return $"{player.NetId}({player.Character.GetType().Name})";
    }

    /// <summary>本局的种子（读档后也是同一个，用来把"共生体成员"跟存档对上）。</summary>
    private static string? SeedOf(IRunState runState)
    {
        try
        {
            return runState.Rng.StringSeed;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
