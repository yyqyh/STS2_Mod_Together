using System.Text.Json;

using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib.Networking.Sidecar;
using Together.Core.Combat;
using Together.Core.Content;
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

    /// <summary>本局实际生效的"共生体人数上限"（2~4，客户端跟随主机）。</summary>
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
                TogetherSettingsStore.ShareGold),
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
            + $" 人数上限={snapshot.GroupSize} 共享金币={snapshot.ShareGold}");
    }

    private sealed record Snapshot(
        bool SymbiosisEnabled,
        bool MergeStarterDecks,
        int HpBonusPercent,
        int GroupSize,
        bool ShareGold);
}

/// <summary>主机开 ENet 服（直连）时广播一次。</summary>
[HarmonyPatch(typeof(NetHostGameService), nameof(NetHostGameService.StartENetHost))]
internal static class HostStartENetSettingsSyncPatch
{
    [HarmonyPrefix]
    private static void Prefix(NetHostGameService __instance)
    {
        TogetherSettingsSync.PublishHostSettings(__instance, "start_enet_host");

        // 新大厅开始：清掉上一局的共生体成员，并把空名单广播出去。
        SymbiosisMembers.Reset(__instance, "start_enet_host");
    }
}

/// <summary>主机开 Steam 服时广播一次。</summary>
[HarmonyPatch(typeof(NetHostGameService), nameof(NetHostGameService.StartSteamHost))]
internal static class HostStartSteamSettingsSyncPatch
{
    [HarmonyPrefix]
    private static void Prefix(NetHostGameService __instance)
    {
        TogetherSettingsSync.PublishHostSettings(__instance, "start_steam_host");
        SymbiosisMembers.Reset(__instance, "start_steam_host");
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
        SymbiosisMembers.PublishHostState(__instance, "peer_ready");
    }
}

/// <summary>客户端开始连接前，先把上一局缓存的主机设置清掉。</summary>
[HarmonyPatch(typeof(NetClientGameService), nameof(NetClientGameService.Initialize))]
internal static class ClientInitializeSettingsResetPatch
{
    [HarmonyPrefix]
    private static void Prefix()
    {
        TogetherSettingsSync.ClearRemote();
        SymbiosisMembers.ClearRemote();
    }
}

/// <summary>客户端与主机断开后同样清掉。</summary>
[HarmonyPatch(typeof(NetClientGameService), nameof(NetClientGameService.OnDisconnectedFromHost))]
internal static class ClientDisconnectedSettingsResetPatch
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        TogetherSettingsSync.ClearRemote();
        SymbiosisMembers.ClearRemote();
    }
}
