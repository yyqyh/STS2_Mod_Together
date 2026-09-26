using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib;
using STS2RitsuLib.RunData;
using Together.Core.Diagnostics;

namespace Together.Core.Foundation;

/// <summary>一位玩家在大厅里的"我要参加合作"那一票（按玩家槽位，每人一份）。</summary>
public sealed class CoopVote
{
    public bool Wants { get; set; }
}

/// <summary>本局的合作名单（全局槽位，由主机汇总后落笔；随 run snapshot 下发、随存档恢复）。</summary>
public sealed class CoopRoster
{
    public List<ulong> Members { get; set; } = [];

    /// <summary>
    /// 这一局的「开局一次性操作」（合并初始卡组 / 血量上限提升 / 金币求和）做过了没有。
    /// </summary>
    /// <remarks>
    /// <b>必须放在这里（run snapshot 自己的数据）而不是本机内存/文件</b>：判据两端必须算出同一个答案。
    /// 早期版本把它记在本机会话字段 + 本机设置文件里，结果一端走了 <c>CreateForNewRun</c>、另一端没走，
    /// 于是"一端合并了、一端没合并"（实测 chk=1 就分歧：一端卡组 14 张/血 106，另一端 4 张/血 66）。
    /// </remarks>
    public bool OpeningSetupDone { get; set; }
}

/// <summary>
/// 「谁参加合作」的唯一真相：RitsuLib 的 <b>RunSavedData 大厅暂存</b>（Lobby Scope）→ run snapshot。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么换成这套</b>：以前用 sidecar 自己同步一份名单，那是**异步广播 + 各端本地缓存**，
/// 开局时两端可能一个已经收到、一个还没收到 → <c>Arm</c> 算出不同名单 → 一端共享一端不共享。
/// 现在名单写进 <b>run snapshot</b>：开局时两端读的是**同一份数据**，物理上不可能不一致；
/// 读档 / 重连也自带（<see cref="RunSavedData{T}.Get" /> 直接就能读到）。
/// </para>
/// <para>
/// <b>形状为什么是"每人一票 + 主机汇总"</b>：全局槽位<b>只接受主机 net id 的贡献</b>，
/// 所以客户端只能写自己的 per-player 票（<c>SyncLobbyOnChange</c> 会把改动推给主机），
/// 汇总结果由主机写进全局槽位。这正是 RitsuLib 文档推荐的模式。
/// </para>
/// <para>
/// <b>key 一旦发布就不能改</b>（改了老玩家的存档就读不到这块数据）—— <c>coop_vote</c> / <c>coop_roster</c> 定死。
/// </para>
/// </remarks>
internal static class CoopLobbyData
{
    /// <summary>按玩家槽位：自己那一票。</summary>
    private const string VoteKey = "coop_vote";

    /// <summary>全局槽位：主机汇总出来的本局名单。</summary>
    private const string RosterKey = "coop_roster";

    private static RunSavedData<CoopRoster>? _roster;
    private static PlayerRunSavedData<CoopVote>? _vote;
    private static bool _registered;

    /// <summary>本机"我要参加合作"的意图（乐观值，UI 立刻生效；权威值仍然是槽位 / 主机汇总）。</summary>
    private static bool _myWanted;

    /// <summary>上面那个意图属于哪个大厅（换大厅时按槽位重置，避免跨局残留）。</summary>
    private static StartRunLobby? _myWantedLobby;

    /// <summary>在 mod 初始化时注册槽位并订阅事件（只做一次）。</summary>
    public static void Register()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;

        using (RitsuLibFramework.BeginModDataRegistration(Const.ModId))
        {
            var store = RitsuLibFramework.GetRunSavedDataStore(Const.ModId);

            _vote = store.RegisterPerPlayer(
                VoteKey,
                () => new CoopVote(),
                new RunSavedDataOptions { SyncLobbyOnChange = true });

            _roster = store.Register(
                RosterKey,
                () => new CoopRoster(),
                new RunSavedDataOptions
                {
                    SyncLobbyOnChange = true,
                    WritePolicy = RunSavedDataWritePolicy.WhenNonDefault,
                });

            // 共享卡组的 owner 槽位表（按 Deck 顺序的 netId 数组 + 指纹）：随 run snapshot / 存档同步，
            // 读档重建后按它还原每张牌原本的主人 —— 解决"两端 owner 互为镜像 → 变牌只有一端能过"。
            Together.Core.Shared.Deck.SharedDeckOwnerSlots.RegisterInto(store);
        }

        RitsuLibFramework.SubscribeLifecycle<RunSavedDataLobbyStagingEvent>(OnStagingChanged);
        RitsuLibFramework.SubscribeLifecycle<RunSavedDataPreparingEvent>(OnRunPreparing);
        Main.Logger.Info("[together] 合作名单已改用 RunSavedData 槽位（大厅暂存 → run snapshot）");
    }

    // ======================================================================
    // 大厅：投票（写自己那一份）
    // ======================================================================

    /// <summary>写下/撤回"我要参加合作"这一票（<c>SyncLobbyOnChange</c> 会自动推给主机）。</summary>
    public static void SetMyVote(StartRunLobby lobby, bool wants)
    {
        _myWanted = wants;
        _myWantedLobby = lobby;

        if (_vote is null)
        {
            CappedLog.Info("coop.vote", "槽位没注册好（_vote=null）：只更新了本机意图");
            return;
        }

        try
        {
            var netId = lobby.NetService.NetId;
            _vote.Lobby.Modify(lobby, netId, vote => vote.Wants = wants);
            var readBack = _vote.Lobby.TryGet(lobby, netId, out var stored) && stored.Wants;
            CappedLog.Info(
                "coop.vote",
                $"本机投票：netId={netId} wants={wants}（写回槽位后再读={readBack}）");
        }
        catch (Exception ex)
        {
            CappedLog.Info("coop.vote", $"写入本机票失败（只更新了本机意图）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>本机这一票投了吗（选人界面按钮的显示直接用这个）。</summary>
    public static bool MyVote(StartRunLobby lobby)
    {
        // 换了大厅就按槽位重置一次本机意图（槽位里没有就是没投）。
        if (!ReferenceEquals(_myWantedLobby, lobby))
        {
            _myWantedLobby = lobby;
            _myWanted = ReadMyVoteFromSlot(lobby);
        }

        if (ReadMyVoteFromSlot(lobby))
        {
            _myWanted = true;   // 槽位里有就以它为准
        }

        // 槽位读不到（未注册 / 还没同步回来）时用本机意图兜底：按钮至少和玩家刚做的操作一致，
        // 而**权威名单**始终来自主机汇总的全局槽位，不受这里影响。
        return _myWanted;
    }

    private static bool ReadMyVoteFromSlot(StartRunLobby lobby)
    {
        if (_vote is null)
        {
            return false;
        }

        try
        {
            return _vote.Lobby.TryGet(lobby, lobby.NetService.NetId, out var vote) && vote.Wants;
        }
        catch (Exception ex)
        {
            CappedLog.Info("coop.vote", $"读取本机票失败（按未投处理）：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // ======================================================================
    // 局内：读本局名单
    // ======================================================================

    /// <summary>
    /// 「run snapshot 就绪」时让配对重跑一次 —— <b>这一步是必需的，不是兜底</b>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// RitsuLib 的时序：大厅暂存在 <c>StartRunLobby.BeginRunForAllPlayers</c> 的 Prefix 打包，
    /// 在 <c>RunManager.InitializeNewRun</c> 的 Prefix 导入进 run —— 而我们的 <c>Arm</c> 挂在
    /// <c>RunState.CreateForNewRun</c> 的 Postfix，<b>比导入更早</b>，那一刻槽位里还是空的
    /// （实测：大厅汇总已经拿到 [p1,p2]，进局时却读到 0 人 → 不成组）。
    /// </para>
    /// <para>
    /// 所以在这里再调一次 <see cref="TogetherPair.Arm" />：上一个"名单为空"的回合**什么都没改**
    /// （不成组那条路径只写日志），重跑等价于"开局时才成组"；已经成组过的会被 <c>Arm</c> 自己短路，
    /// 不会重复合并卡组。两端都会在这个时机跑（各自的 payload 导入之后），因此名单依然一致。
    /// </para>
    /// </remarks>
    private static void OnRunPreparing(RunSavedDataPreparingEvent ev)
    {
        try
        {
            var roster = RosterOf(ev.RunState);
            if (roster.Length < TogetherPair.MinMembers)
            {
                return;   // 没人成组（含单机 / 普通联机）：不插手
            }

            CappedLog.Info(
                "coop.preparing",
                $"run snapshot 就绪：本局名单 {roster.Length} 人[{string.Join(",", roster)}] → 让 Arm 重跑一次");

            // ★ 关键：只有"本局还没成过组"时才算"新局"，让它执行开局一次性操作
            //（合并初始卡组 / 血量上限提升 / 金币求和）。
            // 新局第一次 Arm 往往因为"名单还没同步到"而不成组就 return 了，之后的重建都走
            // FromSerializable（isNewRun=false）→ 那三项**永远不会补做**（实测：共享卡组只有锚点那 11 张）。
            // 反过来，读档 / 同局内重建时 IsBound 已经为真 → 传 false，避免重复合并、重复加血。
            var alreadyArmed = TogetherPair.MemberCount >= TogetherPair.MinMembers;
            TogetherPair.Arm(ev.RunState, isNewRun: !alreadyArmed);
        }
        catch (Exception ex)
        {
            CappedLog.Info("coop.preparing", $"名单就绪后重跑 Arm 失败（忽略）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>本局的合作名单（run snapshot 里的权威数据；没有就是空）。</summary>
    public static ulong[] RosterOf(IRunState runState)
    {
        // 槽位 API 收的是具体 RunState；接口形式（NullRunState 之类）直接按空处理。
        if (_roster is null || runState is not RunState concrete)
        {
            return [];
        }

        try
        {
            return _roster.Get(concrete)?.Members?.ToArray() ?? [];
        }
        catch (Exception ex)
        {
            CappedLog.Info("coop.roster", $"读取本局名单失败（按空处理）：{ex.GetType().Name}: {ex.Message}");
            return [];
        }
    }

    /// <summary>局内清空名单（解绑到 PVP 这一类"本局不再共享"的场景）。</summary>
    public static bool WasOpeningSetupDone(IRunState runState)
    {
        if (_roster is null || runState is not RunState concrete)
        {
            return false;
        }

        try
        {
            return _roster.Get(concrete)?.OpeningSetupDone == true;
        }
        catch (Exception ex)
        {
            CappedLog.Info("coop.roster", $"读取开局一次性标记失败（按未做处理）：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>记下"这一局的开局一次性操作已经做过"（写进 run 槽位 → 随 snapshot 两端一致、随存档恢复）。</summary>
    public static void MarkOpeningSetupDone(IRunState runState)
    {
        if (_roster is null || runState is not RunState concrete)
        {
            return;
        }

        try
        {
            _roster.Modify(concrete, roster => roster.OpeningSetupDone = true);
        }
        catch (Exception ex)
        {
            CappedLog.Info("coop.roster", $"写入开局一次性标记失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    public static void ClearRoster(IRunState? runState)
    {
        if (_roster is null || runState is not RunState concrete)
        {
            return;
        }

        try
        {
            _roster.Set(concrete, new CoopRoster());
            CappedLog.Info("coop.roster", "已清空本局名单（RunSavedData）");
        }
        catch (Exception ex)
        {
            CappedLog.Info("coop.roster", $"清空本局名单失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ======================================================================
    // 主机：把各人的票汇总成全局名单
    // ======================================================================

    /// <summary>主机侧：大厅里任何一次暂存变化（有人投票 / 有人进出 / 即将开局）都重新汇总一遍。</summary>
    private static void OnStagingChanged(RunSavedDataLobbyStagingEvent ev)
    {
        if (!ev.IsHost || _vote is null || _roster is null)
        {
            return;   // 只有主机能写全局槽位；单人局不走这条路
        }

        try
        {
            var members = new List<ulong>();
            foreach (var player in ev.Lobby.Players)
            {
                if (_vote.Lobby.TryGet(ev.Lobby, player.id, out var vote) && vote.Wants)
                {
                    members.Add(player.id);
                }
            }

            var current = _roster.Lobby.GetOrCreate(ev.Lobby);
            if (current.Members is not null && current.Members.SequenceEqual(members))
            {
                return;   // 没变化就别写（否则会自己触发自己）
            }

            _roster.Lobby.Set(ev.Lobby, new CoopRoster { Members = members });
            CappedLog.Info(
                "coop.staging",
                $"主机汇总合作票（{ev.Reason}）：[{string.Join(",", members)}]");
        }
        catch (Exception ex)
        {
            CappedLog.Info("coop.staging", $"汇总合作票失败：{ex.GetType().Name}: {ex.Message}");
        }
    }
}
