using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using Together.Core.Settings;

namespace Together.Core.Foundation;

/// <summary>
/// 选人阶段"这次开局要不要管合作模式"的判定：开关开着 + 有联机大厅。
/// </summary>
/// <remarks>
/// <b>只管"显不显示按钮"</b>。谁加入了、能不能成组、什么时候开局，全部交给
/// <see cref="CoopLobbyData" />（RitsuLib 的 RunSavedData 大厅暂存）——
/// 名单会随 run snapshot 下发，所以这里不再需要任何"名额 / 门控 / 名单查询"。
/// </remarks>
internal static class TogetherCoopGate
{
    /// <summary>本局是否开启合作模式（联机时以主机设置为准）。</summary>
    public static bool IsEnabled => TogetherSettingsSync.EffectiveSymbiosisEnabled;

    /// <summary>这局是不是联机局（合作模式只在联机里有意义）。</summary>
    public static bool IsMultiplayerLobby(StartRunLobby? lobby)
    {
        return lobby is not null && lobby.NetService.Type.IsMultiplayer();
    }

    /// <summary>这次开局要不要管合作模式这件事（= 选人界面要不要出现那个按钮）。</summary>
    public static bool Applies(StartRunLobby? lobby)
    {
        return IsEnabled && IsMultiplayerLobby(lobby);
    }
}
