using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using Together.Core.Content;
using Together.Core.Patches.Deck;
using Together.Core.Settings;
using Together.Core.Utils;
using System.Text.Json;
using STS2RitsuLib.Networking.Sidecar;

namespace Together.Core.Combat;

/// <summary>
/// 共生体的「成员注册表」：谁是锚点、谁是回声。
/// </summary>
/// <remarks>
/// <para>
/// <b>锚点（<see cref="Anchor" />）</b>是权威实例持有者——主卡组与四个战斗牌堆都挂在它身上；
/// <b>回声（<see cref="Echoes" />）</b>是组里其他人，访问入口被重定向到锚点，但手牌与能量保持独立。
/// 人数由设置里的"共生体人数上限"决定（2~4），实际人数 = 选人界面按了「共生体」的人数（≥2 即成组）。
/// </para>
/// <para>
/// <b>为什么不用 <c>LocalContext</c> 之类的本机视角来决定锚点</b>：两端必须算出同一个答案，
/// 否则第一次抽牌就会分叉。<c>RunState.Players</c> 的顺序来自大厅，两端天然一致，也会写进存档。
/// </para>
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

    /// <summary><c>Player.Piles</c> 是首次访问即固化的缓存数组，需要在激活时清一次。</summary>
    private static readonly AccessTools.FieldRef<Player, CardPile[]?> RunPileCache =
        AccessTools.FieldRefAccess<Player, CardPile[]?>("_runPiles");

    /// <summary>
    /// <c>Player.Deck</c> 是 get-only 自动属性，这里直接拿到它的 backing field。
    /// </summary>
    /// <remarks>
    /// 为什么必须换字段、而不能只改 getter：界面（顶部卡组按钮、卡组界面）会在初始化时
    /// 抓一次 <c>PileType.Deck.GetPile(player)</c> 把 <c>CardPile</c> 引用存进自己的字段里，
    /// 之后只看那个引用。只重定向 getter 的话，这份"旧引用"永远是回声自己的卡组
    /// （实测就是"p2 只显示 9 张初始卡"）。把字段换掉之后，此后任何一次读取——
    /// 无论走不走 getter——拿到的都是锚点那份卡组。
    /// </remarks>
    private static readonly AccessTools.FieldRef<Player, CardPile> DeckField =
        AccessTools.FieldRefAccess<Player, CardPile>("<Deck>k__BackingField");

    /// <summary>本局是否已经成组（锚点 + 至少一个回声）。</summary>
    /// <summary>
    /// 本局是否真的在"共用身体"：配对已武装（锚点 + ≥1 回声）**且这是联机局**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这里就是全 mod 的总闸门：镜像（血量/格挡/上限/能力/金币）、球位、召唤物、归属归一、
    /// 事件并发保护、牌堆顺序归一……都只看这一个属性，所以判定写在这里、只写一次。
    /// </para>
    /// <para>
    /// <b>为什么必须带"联机"这一条</b>：配对是按设计"非共生体局不清空"的（避免读档/重连时误清），
    /// 于是<b>单人局里它可能还留着上一局联机的配对</b>——那时 `IsActive` 为真会让各种补丁去动单人局的数据，
    /// 最典型就是"草蜢偷牌把上一局的玩家 creature 塞进 targets → 本体 NRE 崩溃"（实测 2026-09-23 11:28）。
    /// 带上联机判定后，单人局一律不介入。
    /// </para>
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

    /// <summary>
    /// 更严的"本局有效"判定：在 <see cref="IsActive" /> 之上再要求成员都还在**本局**的 Players 里
    /// （排除"上一局的 Player 对象"残留）。需要"塞进本局数据"的地方（如草蜢扩 targets）用它。
    /// </summary>
    public static bool IsLive
    {
        get
        {
            if (!IsActive)
            {
                return false;
            }

            if (Members().FirstOrDefault()?.RunState is not { } state)
            {
                return false;
            }

            return Members().All(member => state.Players.Contains(member));
        }
    }

    public static Player? Anchor => _anchor;

    /// <summary>组里除锚点以外的成员（1~3 个）。</summary>
    public static IReadOnlyList<Player> Echoes => _echoes;

    /// <summary>组内总人数（未激活时是 0）。</summary>
    public static int MemberCount => _anchor is null ? 0 : _echoes.Count + 1;

    /// <summary>锚点当前的 <see cref="PlayerCombatState" />；所有回声的四个牌堆都指向它。</summary>
    public static PlayerCombatState? AnchorCombatState => _anchorCombatState;

    /// <summary>
    /// 在<b>跑局构造完成之后</b>激活共生体。调用点在 <c>RunStartPatches</c> 的两个 Postfix 里。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>激活时机是这套设计里最容易踩的坑，必须保持"晚于 RunState 构造"。</b>
    /// <c>RunState.CreateShared</c> 的顺序是"先设 p1 的 RunState，再遍历牌组给每张卡设 owner"：
    /// </para>
    /// <code>
    /// foreach (Player player in players) {
    ///     player.RunState = runState;                    // p1 先拿到 RunState
    ///     foreach (CardModel card in player.Deck.Cards)  // ← 若此时已激活，p2.Deck 就是 p1 的卡组
    ///         runState.AddCard(card, player);            // → 同一张牌被设两次 owner → 抛异常
    /// }
    /// </code>
    /// <para>
    /// 早期版本在 <c>Refresh(player.RunState)</c> 里懒激活，正好命中这一点，
    /// 结果是 <c>InvalidOperationException: Card ... already has an owner</c> → 开局中断 → 黑屏。
    /// 所以现在只在跑局工厂返回之后激活：那一刻所有人的 RunState 与卡组 owner 都已经落定。
    /// </para>
    /// </remarks>
    public static void Arm(IRunState? runState, bool isNewRun = false)
    {
        if (runState is null || runState is NullRunState)
        {
            SelfCheck.Write("[together][diag] Arm：还没有跑局，保持现有配对");
            return;
        }

        if (ReferenceEquals(runState, _armedRunState))
        {
            return;
        }

        var players = runState.Players;

        // 成员 = 选人界面里按了「确定为共生体」的人（见 SymbiosisMembers）。
        // 取"在大厅里确定过、且这一局真的在 Players 里"的人，按 RunState.Players 的顺序排：
        // 两端必须算出同一个答案，而 Players 顺序来自大厅、两端天然一致，也会写进存档。
        var confirmed = SymbiosisMembers.Snapshot();
        var picked = new List<Player>(MaxMembers);
        foreach (var player in players)
        {
            if (confirmed.Contains(player.NetId))
            {
                picked.Add(player);
            }
        }

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

        // 下面这两件事都是"开局一次性"的，**只能在新开一局时做**：
        //
        //  - 合并初始卡组：存档是按 player.Deck 序列化的，而回声的 Deck getter 早就重定向到共享卡组了，
        //    所以存档里每个人的卡组都是同一份；读档后回声手里就有了一副"共享卡组的副本"，
        //    这时再合并一次就是**翻倍**（实测重连一次 51 → 102）。读档/重连必须只用存档里的内容。
        //  - 血量上限提升：上限已经写进存档，再抬一次会越滚越大。
        if (isNewRun)
        {
            if (TogetherSettingsSync.EffectiveMergeStarterDecks)
            {
                foreach (var echo in _echoes)
                {
                    MergeStarterDeckInto(echo, picked[0]);
                }
            }

            ApplyHpBonus(picked[0], _echoes);
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

        // 金币共享（可选）：新局把所有人的起始金币加起来（99 × n），读档/重连只做对齐。
        GoldMirror.OnArm(isNewRun);

        // 新开一局：把成员按种子记下来，供以后读档/重连找回。
        if (isNewRun)
        {
            TogetherSettingsStore.RememberSymbioticRun(SeedOf(runState), picked.Select(p => p.NetId));
        }

        // 把"本局成员名单"广播一次：读档/重连/新房间都会走 Arm，所以两端任何时刻拿到的是同一份。
        // （草蜢偷牌的"能不能各偷一张"就靠这份名单两边对齐。）
        RunMembersSync.Publish(picked.Select(p => p.NetId), isNewRun ? "run_armed_new" : "run_armed");

        Log.Info(
            $"[together] 共生体已激活：anchor={Describe(picked[0])} "
            + $"echoes=[{string.Join(",", _echoes.Select(Describe))}]"
            + $"（大厅共 {players.Count} 人，共享卡组 {picked[0].Deck.Cards.Count} 张）");

        // 兼容层补扫：有些 mod 的程序集可能晚于 mod 初始化才被加载，
        // 进局时再扫一遍（已扫过的程序集直接跳过，成本只有几十个字符串比较）。
        SameOwnerCheckCompat.Apply("run_armed");
    }

    /// <summary>
    /// 把 <paramref name="from" />（回声）的初始卡组并进 <paramref name="to" />（锚点）的卡组：
    /// 共享卡组 = p1 + p2 + …。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 逐张走"从原卡组摘掉 → 放进目标卡组 → 归属改成锚点"：牌还是那些牌，只是换了一副卡组，
    /// 所以<b>不会留下无主的牌</b>。
    /// </para>
    /// <para>必须发生在"回声的 Deck 字段被换成锚点那份"<b>之前</b>，否则读到的就是同一副卡组了。</para>
    /// <para>
    /// 必须直接读 <c>Deck</c> 的<b>字段</b>：此时 <c>_anchor/_echoes</c> 已赋值，
    /// 回声的 <c>Player.Deck</c> getter 会被重定向到锚点，用 getter 读会拿到目标那一副（合并会空转）。
    /// </para>
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
            card.GiveToAnotherPlayer(to);
        }

        Log.Info(
            $"[together] 共生体合并初始卡组：把 {from.Character.GetType().Name} 的 {cards.Count} 张"
            + $"并入共享卡组（合计 {toDeck.Cards.Count} 张）");
    }

    /// <summary>
    /// 共生体血量上限提升：把每个回声最大生命的 <c>HpBonusPercent</c>% 加进共享血池。
    /// </summary>
    /// <remarks>
    /// 上限和当前血一起抬（否则开局不是满血）。回声那边由 <see cref="BodyMirror" /> 对齐。
    /// </remarks>
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

    /// <summary>旧名字，等价于 <see cref="IsMember" />。</summary>
    public static bool IsPaired(Player? player)
    {
        return IsMember(player);
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

    /// <summary>
    /// 记录锚点当前的战斗状态。锚点重建 <see cref="PlayerCombatState" /> 时必须调用，
    /// 否则回声会一直指向上一场战斗的牌堆。
    /// </summary>
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

/// <summary>
/// 「本局共生体成员名单」的同步：由 <b>Arm（配对完成）</b> 驱动，主机权威、sidecar 同步。
/// </summary>
/// <remarks>
/// <para>
/// 为什么不用 <c>SymbiosisMembers</c> 那份名单：它只在**选人界面**被写，进局后不重建、读档/重连时是空的
/// （客户端还会在开始连接时清缓存）—— 生命周期和"本局配对"对不上，于是常常出现
/// "本地=[1,1317…] 同步=[]"这种不一致（实测：草蜢偷牌的判据就是被它坑的）。
/// </para>
/// <para>
/// 这里另开一个 topic，由 <see cref="TogetherPair.Arm" /> 在算出锚点+回声后发布一次：
/// 读取路径（读档 / 重连 / 新房间）都会走 Arm，所以两端任何时刻拿到的都是同一份名单。
/// </para>
/// </remarks>
internal static class RunMembersSync
{
    private const string Topic = "together.run_members";

    private static readonly Lock Gate = new();

    private static bool _initialized;

    private static ulong[]? _remote;

    /// <summary>远端（主机）发布的成员名单；没收到过返回 null。</summary>
    public static ulong[]? Remote
    {
        get
        {
            Initialize();
            lock (Gate)
            {
                return _remote;
            }
        }
    }

    public static void Initialize()
    {
        lock (Gate)
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            RitsuLibSidecarConfigSyncService.RegisterTopic<string, string>(
                Topic,
                string.Empty,
                (_, _) => false,
                (state, _) => state);
            RitsuLibSidecarConfigSyncService.TopicChanged += OnTopicChanged;
        }
    }

    /// <summary>主机把本局成员名单广播出去（Arm 完成时调用）。</summary>
    public static void Publish(IEnumerable<ulong> netIds, string reason)
    {
        Initialize();

        if (RunManager.Instance?.NetService is not NetHostGameService host)
        {
            return;
        }

        try
        {
            var payload = string.Join(",", netIds);

            lock (Gate)
            {
                _remote = Parse(payload);
            }

            RitsuLibSidecarConfigSyncService.PublishHostState(host, Topic, 0, $"{reason}:{payload}");
        }
        catch (Exception ex)
        {
            Main.Logger.Warn($"[together] 广播本局成员名单失败（{reason}）：{ex.Message}");
        }
    }

    public static void ClearRemote()
    {
        lock (Gate)
        {
            _remote = null;
        }
    }

    private static void OnTopicChanged(SidecarConfigTopicChangedEvent ev)
    {
        if (ev.Topic != Topic || RunManager.Instance?.NetService is not NetClientGameService)
        {
            return;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<string>(ev.StateJson) ?? string.Empty;
            var ids = Parse(payload);

            lock (Gate)
            {
                _remote = ids;
            }

            Log.Info($"[together] run.members：收到主机名单 [{string.Join(",", ids ?? [])}]");
        }
        catch (Exception ex)
        {
            Main.Logger.Warn($"[together] 解析主机成员名单失败：{ex.Message}");
        }
    }

    private static ulong[]? Parse(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        var ids = new List<ulong>();
        foreach (var part in payload.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (ulong.TryParse(part.Trim(), out var id) && id != 0UL)
            {
                ids.Add(id);
            }
        }

        return ids.Count > 0 ? ids.ToArray() : null;
    }
}
