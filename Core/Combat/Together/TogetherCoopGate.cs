using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using Together.Core.Content;
using Together.Core.Settings;

namespace Together.Core.Combat;

/// <summary>
/// 选人阶段关于共享卡组的两条规则：谁还能"确定"，以及能不能起程。
/// </summary>
/// <remarks>
/// <para>
/// 判定数据来自两处：大厅（谁是这局的玩家，本体全网同步）+ 共生体成员集合
/// （<see cref="SymbiosisMembers" />，主机权威、sidecar 同步）。
/// 这两样都是两端一致的，所以"第三个人按不动"在两边看到的结果相同。
/// </para>
/// <para>
/// 规则：名额 = 设置里的"人数上限"（默认 2）；已经确定过的人可以再按一次取消；名额满了别人按不动。
/// <b>起程门控</b>：0 人确定 = 普通联机局；<b>达到最小人数（2 人）就放行</b>——这些人共享卡组
/// （真正配对见 <c>TogetherPair.Arm</c>，它按"确定过的人 + 至少 MinMembers"成组，不要求凑满名额）。
/// 只有"确定了 1 个人"时拦住：否则会开出一局"一个人的普通游戏"，和按按钮时的预期不符。
/// </para>
/// </remarks>
internal static class TogetherCoopGate
{
    /// <summary>本局是否开启共生体（联机时以主机设置为准）。</summary>
    public static bool IsEnabled => TogetherSettingsSync.EffectiveSymbiosisEnabled;

    /// <summary>这局是不是联机局（共生体只在联机里有意义）。</summary>
    public static bool IsMultiplayerLobby(StartRunLobby? lobby)
    {
        return lobby is not null && lobby.NetService.Type.IsMultiplayer();
    }

    /// <summary>这次开局要不要管共生体这件事。</summary>
    public static bool Applies(StartRunLobby? lobby)
    {
        return IsEnabled && IsMultiplayerLobby(lobby);
    }

    /// <summary>大厅里已经确定的人数（只数还留在这个大厅里的玩家，避免带上上一局的残留）。</summary>
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

    /// <summary>这位玩家在这局大厅里是不是已经确定了。</summary>
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

    /// <summary>
    /// 这位玩家还能不能确定。
    /// </summary>
    /// <remarks>已经确定的人返回 true —— 那是"可以按"（按下去是取消）。</remarks>
    public static bool CanConfirm(StartRunLobby? lobby, ulong playerId)
    {
        if (!IsMultiplayerLobby(lobby))
        {
            return false;
        }

        return IsConfirmed(lobby, playerId) || CountConfirmed(lobby) < SymbiosisMembers.Capacity;
    }

    /// <summary>
    /// 能不能起程。
    /// </summary>
    /// <remarks>0 人（普通联机）或达到最小人数（2 人 → 共享卡组）都可以；只有"确定了 1 个人"时拦住。</remarks>
    public static bool CanEmbark(StartRunLobby? lobby)
    {
        if (!Applies(lobby))
        {
            // 没开共生体（或单人局）→ 完全不管。
            return true;
        }

        return CountConfirmed(lobby) is 0 or >= TogetherPair.MinMembers;
    }
}
