using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using Together.Core.Content;
using Together.Core.Settings;

namespace Together.Core.Combat;

/// <summary>
/// 选人阶段关于合作模式的两个查询：这次开局要不要管合作、谁已经加入了。
/// </summary>
/// <remarks>
/// 判定数据来自两处：大厅（谁是这局的玩家，本体全网同步）+ 合作成员集合
/// （<see cref="SymbiosisMembers" />，主机权威、sidecar 同步）。这两样都是两端一致的，
/// 所以"谁加入了合作"在两边看到的结果相同。
/// <b>没有名额限制、也没有起程门控</b>：
/// <list type="bullet">
///   <item>谁都能加入（几个人的大厅都行），加入的人自成一组；</item>
///   <item>只有 1 个人加入 → 开局时凑不够 <c>TogetherPair.MinMembers</c>，按<b>普通联机局</b>打；</item>
///   <item>能不能开局交给本体的"准备"流程（所有人都准备就开局），我们不拦。</item>
/// </list>
/// 真正的配对见 <c>TogetherPair.Arm</c>：按"加入了的人 + 至少 MinMembers"成组。
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

    /// <summary>这次开局要不要管合作模式这件事。</summary>
    public static bool Applies(StartRunLobby? lobby)
    {
        return IsEnabled && IsMultiplayerLobby(lobby);
    }

    /// <summary>大厅里已经加入合作的人数（只数还留在这个大厅里的玩家，避免带上上一局的残留）。</summary>
    public static int CountConfirmed(StartRunLobby? lobby)
    {
        if (lobby is null)
        {
            return 0;
        }

        var count = 0;
        foreach (var player in lobby.Players)
        {
            if (SymbiosisMembers.IsConfirmed(player.id))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>这位玩家在这局大厅里是不是已经加入了合作。</summary>
    public static bool IsConfirmed(StartRunLobby? lobby, ulong playerId)
    {
        if (lobby is null)
        {
            return false;
        }

        foreach (var player in lobby.Players)
        {
            if (player.id == playerId)
            {
                return SymbiosisMembers.IsConfirmed(playerId);
            }
        }

        return false;
    }
}
