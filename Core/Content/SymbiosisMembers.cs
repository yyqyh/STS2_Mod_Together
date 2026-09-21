using System.Globalization;
using System.Text.Json;

using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib.Networking.Sidecar;
using Together.Core.Settings;

namespace Together.Core.Content;

/// <summary>
/// 共生体成员：在选人界面按了"确定为共生体"的玩家集合。
/// </summary>
/// <remarks>
/// <para>
/// <b>主机权威</b>：客户端只能发"我想确定/取消"的请求，由主机校验（只能确定自己 + 最多两个名额）后落库并广播。
/// 用的是 RitsuLib 的 sidecar 配置同步（和设置开关同一套机制，WineFox 也在用）：
/// </para>
/// <list type="bullet">
/// <item><description>主机：<c>RegisterTopic</c> 写入本地集合 → <c>PublishHostState</c> 广播。</description></item>
/// <item><description>客户端：<c>TryRequestClientChange</c> 发请求 → 主机批准后广播 → 客户端在 <c>TopicChanged</c> 里更新缓存。</description></item>
/// </list>
/// <para>
/// 这样"两个人确定后第三个人不能确定"在两端都是同一个判定，不会出现一边能按一边不能按的分叉。
/// </para>
/// <para>
/// 单机（本地多控）时网络服务是 Host，走"主机"这条路：本地判定 + 广播，天然支持一台机器上轮流操作多个本地玩家。
/// </para>
/// </remarks>
internal static class SymbiosisMembers
{
    /// <summary>
    /// 名额：共生体最多几个人（设置里的"共生体人数上限"，2~4，联机以主机为准）。
    /// </summary>
    public static int Capacity => TogetherSettingsSync.EffectiveGroupSize;

    private const string Topic = "together.symbiosis_members";

    private static readonly Lock Gate = new();

    private static readonly List<string> Local = [];

    private static bool _initialized;

    /// <summary>已经为哪个客户端大厅重置过本地状态（避免选人界面每 0.2 秒重置一次）。</summary>
    private static INetGameService? _preparedClientService;

    /// <summary><c>RunManager.NetService</c> 是 get-only 自动属性，这里拿它的 backing field。</summary>
    private static readonly AccessTools.FieldRef<RunManager, INetGameService> RunManagerNetService =
        AccessTools.FieldRefAccess<RunManager, INetGameService>("<NetService>k__BackingField");

    /// <summary>集合发生变化（本地操作或远端同步）时触发，用来刷新选人界面的按钮。</summary>
    public static event Action? Changed;

    public static void Initialize()
    {
        lock (Gate)
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
        }

        RegisterTopicFromLocal();
        RitsuLibSidecarConfigSyncService.TopicChanged += OnTopicChanged;
    }

    /// <summary>
    /// 把大厅的网络服务提前挂到 <see cref="RunManager" /> 上（只做主机侧）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 共生体成员同步用的是 RitsuLib 的主机权威配置同步：客户端发请求，主机在
    /// <c>RitsuLibSidecarConfigSyncService.OnRequestMessage</c> 里校验并落库。
    /// 而那个方法第一句就是
    /// <c>var netService = RunManager.Instance?.NetService; if (netService is not NetHostGameService) return;</c>
    /// </para>
    /// <para>
    /// 问题在于：<b>选人界面阶段一局还没开始</b>，<c>RunManager</c> 要等
    /// <c>SetUpNewMultiplayer</c> 才会把 <c>lobby.NetService</c> 装进去，
    /// 所以此刻 <c>RunManager.Instance.NetService</c> 还是未初始化的值 →
    /// 主机把客户端的请求<b>静默丢掉</b>（一条日志都不留）。
    /// 实测表现就是"联机时非主机按右下角的「确定参加共生体」没有任何反应"。
    /// </para>
    /// <para>
    /// 大厅用的服务对象与进局时 <c>SetUpNewMultiplayer</c> 收到的 <c>lobby.NetService</c>
    /// <b>是同一个</b>，所以这里提前绑定不会造成两端分叉；进局时本体再赋一次同样的值。
    /// </para>
    /// </remarks>
    public static void BindHostService(INetGameService? netService)
    {
        // 只有主机需要：客户端那条链路本来就不做主机校验。
        if (netService is not NetHostGameService)
        {
            return;
        }

        var runManager = RunManager.Instance;
        if (runManager is null || ReferenceEquals(runManager.NetService, netService))
        {
            return;
        }

        try
        {
            RunManagerNetService(runManager) = netService;
            Log.Info(
                $"[together] 已把大厅网络服务提前挂到 RunManager（{netService.Type} netId={netService.NetId}），"
                + "否则主机会把选人界面的共生体请求丢掉");
        }
        catch (Exception ex)
        {
            Log.Warn($"[together] 提前绑定大厅网络服务失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 客户端进入一间大厅时，把本地这一份重置成"空名单 + 修订号归 1"。
    /// </summary>
    /// <remarks>
    /// RitsuLib 的快照接收会丢掉"修订号不比自己新"的广播
    /// （<c>if (current.Revision &gt; ctx.Message.Revision) return;</c>）。
    /// 同一个客户端进程连着玩好几局时，本地修订号可能已经被上一局抬得比主机这一局高，
    /// 于是<b>主机的广播会被全部忽略</b>——表现就是"一直显示没确定"。
    /// 这里在刚进大厅时把本地修订号按回 1，之后主机的快照（修订号 ≥ 1）就能正常落到本地。
    /// </remarks>
    public static void PrepareClientLobby(INetGameService? netService)
    {
        if (netService is not NetClientGameService || ReferenceEquals(_preparedClientService, netService))
        {
            return;
        }

        _preparedClientService = netService;

        lock (Gate)
        {
            Local.Clear();
        }

        RegisterTopicFromLocal();
        Log.Info("[together] 客户端：本地共生体名单已重置（修订号归 1，等待主机广播）");
        Changed?.Invoke();
    }

    /// <summary>这位玩家是不是已经确定参加共生体。</summary>
    public static bool IsConfirmed(ulong playerId)
    {
        lock (Gate)
        {
            return Local.Contains(Key(playerId));
        }
    }

    /// <summary>已经确定的人数。</summary>
    public static int Count
    {
        get
        {
            lock (Gate)
            {
                return Local.Count;
            }
        }
    }

    /// <summary>这位玩家还能不能确定（已经在名额里、或者还有空名额）。</summary>
    public static bool HasFreeSeat(ulong playerId)
    {
        lock (Gate)
        {
            return Local.Contains(Key(playerId)) || Local.Count < Capacity;
        }
    }

    public static ulong[] Snapshot()
    {
        lock (Gate)
        {
            return Local
                .Select(key => ulong.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                    ? id
                    : 0UL)
                .Where(id => id != 0UL)
                .ToArray();
        }
    }

    /// <summary>
    /// 确定 / 取消一位玩家的共生体身份。
    /// </summary>
    /// <returns>请求是否被本地接受（客户端只代表"已发出"，最终以主机广播为准）。</returns>
    public static bool TrySet(INetGameService? netService, ulong playerId, bool confirm, string reason)
    {
        Initialize();

        if (netService is NetClientGameService)
        {
            // 客户端：请求主机，校验和落库都在主机那侧。
            return RitsuLibSidecarConfigSyncService.TryRequestClientChange(
                netService,
                Topic,
                new Delta(Key(playerId), confirm),
                reason);
        }

        if (!CanApply(playerId, confirm))
        {
            Log.Info($"[together] 共生体名额已满，拒绝 player={playerId} 的确定请求");
            return false;
        }

        lock (Gate)
        {
            ApplyLocal(Key(playerId), confirm);
        }

        RegisterTopicFromLocal();

        if (netService is not null)
        {
            PublishHostState(netService, reason);
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>主机把当前集合广播出去（开服 / 有对端就绪 / 集合变化时）。</summary>
    public static void PublishHostState(INetGameService? netService, string reason)
    {
        if (netService is not NetHostGameService)
        {
            return;
        }

        try
        {
            RegisterTopicFromLocal();
            RitsuLibSidecarConfigSyncService.PublishHostState(netService, Topic, 0, reason);
        }
        catch (Exception ex)
        {
            Log.Warn($"[together] 广播共生体成员失败（{reason}）：{ex.Message}");
        }
    }

    /// <summary>主机开新大厅时清空（上一局的成员不该带到下一局）。</summary>
    public static void Reset(INetGameService? netService, string reason)
    {
        Initialize();

        lock (Gate)
        {
            if (Local.Count == 0)
            {
                return;
            }

            Local.Clear();
        }

        RegisterTopicFromLocal();
        PublishHostState(netService, reason);
        Changed?.Invoke();
    }

    /// <summary>客户端开始连接 / 断开连接时清掉本地缓存。</summary>
    public static void ClearRemote()
    {
        lock (Gate)
        {
            Local.Clear();
        }

        Changed?.Invoke();
    }

    private static bool CanApply(ulong playerId, bool confirm)
    {
        if (!confirm)
        {
            return true;
        }

        lock (Gate)
        {
            return Local.Contains(Key(playerId)) || Local.Count < Capacity;
        }
    }

    private static void ApplyLocal(string key, bool confirm)
    {
        if (confirm)
        {
            if (!Local.Contains(key))
            {
                Local.Add(key);
            }
        }
        else
        {
            Local.Remove(key);
        }
    }

    private static void RegisterTopicFromLocal()
    {
        string[] members;
        lock (Gate)
        {
            members = Local.ToArray();
        }

        RitsuLibSidecarConfigSyncService.RegisterTopic<State, Delta>(
            Topic,
            new State(members),
            CanClientRequest,
            ApplyDelta);
    }

    /// <summary>
    /// 主机的校验：只允许"确定自己"，且最多两个名额。
    /// </summary>
    /// <remarks>
    /// 这段在 RitsuLib 的主题锁里执行，所以只碰我们自己的 <see cref="Gate" />，
    /// 并且绝不在持有 <see cref="Gate" /> 时回调 RitsuLib（避免锁顺序反转）。
    /// </remarks>
    private static bool CanClientRequest(ulong sender, Delta delta)
    {
        if (delta.Player != Key(sender))
        {
            Log.Warn($"[together] 拒绝他人的共生体请求：sender={sender} target={delta.Player}");
            return false;
        }

        if (!delta.Confirm)
        {
            return true;
        }

        lock (Gate)
        {
            return Local.Contains(delta.Player) || Local.Count < Capacity;
        }
    }

    private static State ApplyDelta(State state, Delta delta)
    {
        var set = new HashSet<string>(state.Members ?? []);

        if (delta.Confirm)
        {
            set.Add(delta.Player);
        }
        else
        {
            set.Remove(delta.Player);
        }

        return new State(set.ToArray());
    }

    private static void OnTopicChanged(SidecarConfigTopicChangedEvent ev)
    {
        if (ev.Topic != Topic)
        {
            return;
        }

        State? state;
        try
        {
            state = JsonSerializer.Deserialize<State>(ev.StateJson);
        }
        catch (Exception ex)
        {
            Log.Warn($"[together] 解析共生体成员失败：{ex.Message}");
            return;
        }

        if (state is null)
        {
            return;
        }

        lock (Gate)
        {
            Local.Clear();
            Local.AddRange((state.Members ?? []).Where(m => !string.IsNullOrWhiteSpace(m)));
        }

        // reason/changedBy 用来区分"主机广播的正常更新"和"被某个重置流程清空"。
        Log.Info(
            $"[together] 共生体成员更新[{ev.Reason}] by={ev.ChangedByPeer} rev={ev.Revision}："
            + $"{string.Join(",", Local)}");
        Changed?.Invoke();
    }

    private static string Key(ulong playerId)
    {
        return playerId.ToString(CultureInfo.InvariantCulture);
    }

    private sealed record State(string[]? Members);

    private sealed record Delta(string Player, bool Confirm);
}
