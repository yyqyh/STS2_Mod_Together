using System.Reflection;
using System.Text.Json;

using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib.Networking.Sidecar;
using Together.Core.Alignment;
using Together.Core.Foundation;
using Together.Core.Shared.Body;
using Together.Core.Shared.Gold;
using Together.Core.Shared.Orb;
using Together.Core.Shared.Pet;
using Together.Core.Shared.Power;
using Together;

namespace Together.Core.Settings;
/// <summary>联机时的"共享角色"设置同步：<b>以主机为准</b>。</summary>
/// <remarks>
/// 为什么必须同步：配不配对是<b>每台机器各自算</b>的（<c>TogetherPair.Arm</c> 依据"本局有几名玩家选了那个角色"）。
/// 要是两边设置不一样（主机选故障机器人、另一位还停在"关闭"），就会一台机器配对、另一台不配对 →
/// 两台机器的牌堆/血量立刻分叉 → 校验和当场把人踢下线。所以设置只认主机的，客户端跟随。
/// 机制照抄酒狐（<c>WineFoxRuntimeSettings</c> + <c>WineFoxMultiplayerSettingsSyncPatches</c>）：
/// 用 RitsuLib 的 sidecar 配置同步发布/订阅一个 topic；主机在"开服 / 有对端就绪 / 改设置"时发布，
/// 客户端缓存远端值并在连接开始时清掉旧缓存。判定"当前是不是客户端"用 <c>NetClientGameService</c>。
/// 取不到远端值（单人局、还没连上、主机没发过）时一律用本地设置——单人局本来就该听自己的。
/// </remarks>
internal static class TogetherSettingsSync
{
    private const string Topic = "together.symbiosis_enabled";

    private static readonly Lock Gate = new();

    private static bool _initialized;

    private static Snapshot? _remote;

    /// <summary>本局实际生效的"共生体开关"。</summary>
    /// <remarks>客户端优先用主机发布过来的值；其余情况用本地设置。<b>所有读设置的地方都应该走这里</b>，
    /// 不要直接读 <see cref="TogetherSettingsStore" />。</remarks>
    public static bool EffectiveSymbiosisEnabled
    {
        get
        {
            Initialize();

            lock (Gate)
            {
                if (_remote is { } remote)
                {
                    return remote.SymbiosisEnabled;
                }
            }

            return TogetherSettingsStore.SymbiosisEnabled;
        }
    }

    /// <summary>本局实际生效的"开局合并双方初始卡组"开关（同样是客户端跟随主机）。</summary>
    public static bool EffectiveMergeStarterDecks
    {
        get
        {
            Initialize();

            lock (Gate)
            {
                if (_remote is { } remote)
                {
                    return remote.MergeStarterDecks;
                }
            }

            return TogetherSettingsStore.MergeStarterDecks;
        }
    }

    /// <summary>本局实际生效的"共生体血量上限提升百分比"（0~100，客户端跟随主机）。</summary>
    public static int EffectiveHpBonusPercent
    {
        get
        {
            Initialize();

            lock (Gate)
            {
                if (_remote is { } remote)
                {
                    return Math.Clamp(remote.HpBonusPercent, 0, 100);
                }
            }

            return TogetherSettingsStore.HpBonusPercent;
        }
    }

    /// <summary>本局实际生效的"合作人数上限"（2~4，客户端跟随主机）。<b>历史字段：已不参与任何判定</b>，只为保持快照字段数不变。</summary>
    public static int EffectiveGroupSize
    {
        get
        {
            Initialize();

            lock (Gate)
            {
                if (_remote is { } remote)
                {
                    return Math.Clamp(remote.GroupSize, TogetherPair.MinMembers, TogetherPair.MaxMembers);
                }
            }

            return TogetherSettingsStore.GroupSize;
        }
    }

    /// <summary>本局实际生效的"是否共享金币"（客户端跟随主机）。</summary>
    public static bool EffectiveShareGold
    {
        get
        {
            Initialize();

            lock (Gate)
            {
                if (_remote is { } remote)
                {
                    return remote.ShareGold;
                }
            }

            return TogetherSettingsStore.ShareGold;
        }
    }

    /// <summary>本局实际生效的「牌序全序化」开关（客户端跟随主机）。</summary>
    public static bool EffectiveCompatDeterministicOrder => ReadCompat(snapshot => snapshot.CompatDeterministicOrder, TogetherSettingsStore.CompatDeterministicOrder);

    /// <summary>本局实际生效的「钩子监听表去重」开关（客户端跟随主机）。</summary>
    public static bool EffectiveCompatHookDedupe => ReadCompat(snapshot => snapshot.CompatHookDedupe, TogetherSettingsStore.CompatHookDedupe);

    /// <summary>本局实际生效的「钩子派发组内放宽」开关（客户端跟随主机）。</summary>
    public static bool EffectiveCompatHookWiden => ReadCompat(snapshot => snapshot.CompatHookWiden, TogetherSettingsStore.CompatHookWiden);

    /// <summary>本局实际生效的「回声共享卡牌视图置空」开关（客户端跟随主机）。</summary>
    public static bool EffectiveCompatSharedCardView => ReadCompat(snapshot => snapshot.CompatSharedCardView, TogetherSettingsStore.CompatSharedCardView);

    /// <summary>本局实际生效的「回声不重复填充战斗牌堆」开关（客户端跟随主机）。</summary>
    public static bool EffectiveCompatEchoPopulateSkip => ReadCompat(snapshot => snapshot.CompatEchoPopulateSkip, TogetherSettingsStore.CompatEchoPopulateSkip);

    /// <summary>本局实际生效的「镜像副本回合末只结算一次」开关（客户端跟随主机）。</summary>
    public static bool EffectiveCompatMirroredPowerSingleFire => ReadCompat(snapshot => snapshot.CompatMirroredPowerSingleFire, TogetherSettingsStore.CompatMirroredPowerSingleFire);

    /// <summary>本局实际生效的「注能每场战斗只自动打出一次」开关（客户端跟随主机）。</summary>
    public static bool EffectiveCompatImbuedOnce => ReadCompat(snapshot => snapshot.CompatImbuedOnce, TogetherSettingsStore.CompatImbuedOnce);

    /// <summary>本局实际生效的「随机数预测（RandomForeseer）联动」开关（客户端跟随主机）。</summary>
    public static bool EffectiveCompatRandomForeseerSync => ReadCompat(snapshot => snapshot.CompatRandomForeseerSync, TogetherSettingsStore.CompatRandomForeseerSync);

    /// <summary>本局实际生效的"共享球位上限口径"（客户端跟随主机）。</summary>
    public static OrbCapMode EffectiveOrbCap
    {
        get
        {
            Initialize();

            lock (Gate)
            {
                if (_remote is { } remote)
                {
                    return remote.OrbCap;
                }
            }

            return TogetherSettingsStore.OrbCap;
        }
    }

    /// <summary>联机时读主机广播的开关，否则读本机设置。</summary>
    private static bool ReadCompat(Func<Snapshot, bool> fromRemote, bool local)
    {
        Initialize();

        lock (Gate)
        {
            if (_remote is { } remote)
            {
                return fromRemote(remote);
            }
        }

        return local;
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
            RegisterTopicFromLocalSettings();
            RitsuLibSidecarConfigSyncService.TopicChanged += OnTopicChanged;
        }
    }

    /// <summary>主机把当前设置广播出去（设置界面改完、开服、有对端就绪时调用）。</summary>
    public static void PublishHostSettings(string reason)
    {
        PublishHostSettings(RunManager.Instance?.NetService, reason);
    }

    public static void PublishHostSettings(INetGameService? netService, string reason)
    {
        if (netService is not NetHostGameService)
        {
            // 客户端 / 单人局：没有"广播"这回事。
            return;
        }

        try
        {
            lock (Gate)
            {
                RegisterTopicFromLocalSettings();
                _remote = null;
            }

            RitsuLibSidecarConfigSyncService.PublishHostState(netService, Topic, 0, reason);
        }
        catch (Exception ex)
        {
            Main.Logger.Warn($"[together] 广播共生体开关失败（{reason}）：{ex.Message}");
        }
    }

    /// <summary>清掉缓存的远端设置（客户端开始连接 / 断开连接时调用）。</summary>
    public static void ClearRemote()
    {
        lock (Gate)
        {
            _remote = null;
        }
    }

    private static void RegisterTopicFromLocalSettings()
    {
        RitsuLibSidecarConfigSyncService.RegisterTopic<Snapshot, Snapshot>(
            Topic,
            new Snapshot(
                TogetherSettingsStore.SymbiosisEnabled,
                TogetherSettingsStore.MergeStarterDecks,
                TogetherSettingsStore.HpBonusPercent,
                TogetherSettingsStore.GroupSize,
                TogetherSettingsStore.ShareGold,
                TogetherSettingsStore.CompatDeterministicOrder,
                TogetherSettingsStore.CompatHookDedupe,
                TogetherSettingsStore.CompatHookWiden,
                TogetherSettingsStore.CompatSharedCardView,
                TogetherSettingsStore.CompatEchoPopulateSkip,
                TogetherSettingsStore.CompatMirroredPowerSingleFire,
                TogetherSettingsStore.CompatImbuedOnce,
                TogetherSettingsStore.CompatRandomForeseerSync,
                TogetherSettingsStore.OrbCap),
            (_, _) => false,
            (state, _) => state);
    }

    private static void OnTopicChanged(SidecarConfigTopicChangedEvent ev)
    {
        if (ev.Topic != Topic || RunManager.Instance?.NetService is not NetClientGameService)
        {
            // 只有客户端要听主机的；主机（和单人局）永远用自己的设置。
            return;
        }

        Snapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<Snapshot>(ev.StateJson);
        }
        catch (Exception ex)
        {
            Main.Logger.Warn($"[together] 解析主机共生体开关失败：{ex.Message}");
            return;
        }

        if (snapshot is null)
        {
            return;
        }

        lock (Gate)
        {
            _remote = snapshot;
        }

        Main.Logger.Info(
            $"[together] 跟随主机设置：共生体={snapshot.SymbiosisEnabled}"
            + $" 合并初始卡组={snapshot.MergeStarterDecks} 血量提升={snapshot.HpBonusPercent}%"
            + $" 人数上限={snapshot.GroupSize} 共享金币={snapshot.ShareGold}"
            + $" 共享球位上限={snapshot.OrbCap}");
    }

    private sealed record Snapshot(
        bool SymbiosisEnabled,
        bool MergeStarterDecks,
        int HpBonusPercent,
        int GroupSize,
        bool ShareGold,
        // 兼容性开关：带默认值 → 老 payload（没有这几个字段）也能解析，不会因"字段变多"报错。
        bool CompatDeterministicOrder = true,
        bool CompatHookDedupe = true,
        bool CompatHookWiden = true,
        bool CompatSharedCardView = true,
        bool CompatEchoPopulateSkip = true,
        bool CompatMirroredPowerSingleFire = true,
        bool CompatImbuedOnce = true,
        bool CompatRandomForeseerSync = true,
        OrbCapMode OrbCap = OrbCapMode.Auto);
}

/// <summary>主机开服（ENet 直连 / Steam）时广播一次设置，并清掉上一局的大厅成员名单。</summary>
[HarmonyPatch]
internal static class HostStartSettingsSyncPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(NetHostGameService), nameof(NetHostGameService.StartENetHost));
        yield return AccessTools.Method(typeof(NetHostGameService), nameof(NetHostGameService.StartSteamHost));
    }

    [HarmonyPrefix]
    private static void Prefix(NetHostGameService __instance, MethodBase __originalMethod)
    {
        var reason = __originalMethod.Name == nameof(NetHostGameService.StartENetHost)
            ? "start_enet_host"
            : "start_steam_host";

        TogetherSettingsSync.PublishHostSettings(__instance, reason);
    }
}

/// <summary>有对端进入可广播状态时再补发一次（后进的人也能拿到）。</summary>
[HarmonyPatch(typeof(NetHostGameService), nameof(NetHostGameService.SetPeerReadyForBroadcasting))]
internal static class HostPeerReadySettingsSyncPatch
{
    [HarmonyPostfix]
    private static void Postfix(NetHostGameService __instance)
    {
        TogetherSettingsSync.PublishHostSettings(__instance, "peer_ready");
    }
}

/// <summary>客户端"开始连接"与"断开连接"都要清掉上一局缓存的主机设置与成员名单。</summary>
/// <remarks>两个时机各挂前缀与后缀各一次：清空是幂等的，宁可多清一次，也不去猜本体在这个方法里会不会读缓存。</remarks>
[HarmonyPatch]
internal static class ClientResetSettingsSyncPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(NetClientGameService), nameof(NetClientGameService.Initialize));
        yield return AccessTools.Method(typeof(NetClientGameService), nameof(NetClientGameService.OnDisconnectedFromHost));
    }

    [HarmonyPrefix]
    private static void Prefix()
    {
        Clear();
    }

    [HarmonyPostfix]
    private static void Postfix()
    {
        Clear();
    }

    private static void Clear()
    {
        TogetherSettingsSync.ClearRemote();
    }
}
