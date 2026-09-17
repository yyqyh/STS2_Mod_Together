using HarmonyLib;

using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;

namespace Together.Core.Multiplayer;

/// <summary>
/// 自检日志：在生成校验和之前把共享状态打印出来。
/// </summary>
/// <remarks>
/// <para>
/// 校验和只会告诉你"两端分叉了"并当场把客户端踢下线（<c>NetError.StateDivergence</c>），
/// 但不会告诉你**哪一步先错**。这份日志把两个 creature 的生命 / 格挡 / 状态，
/// 以及两个玩家的四个牌堆数量打在一起，分叉时一眼能看出是哪一项先分歧。
/// </para>
/// <para>
/// 默认关闭（每次动作都会调一次校验和，开着会刷爆日志）。
/// 打开方式：启动游戏前设置环境变量 <c>TOGETHER_SELFCHECK=1</c>；
/// 顺带一提，本机双人（Local Multi-Control 的回环主机）没有真正的对端，
/// 校验和不会报分叉，所以本地测试时这份日志就是唯一的分叉探测器。
/// </para>
/// </remarks>
internal static class SelfCheck
{
    private static bool? _enabled;

    public static bool Enabled
    {
        get
        {
            _enabled ??= string.Equals(
                Environment.GetEnvironmentVariable("TOGETHER_SELFCHECK"),
                "1",
                StringComparison.Ordinal);

            return _enabled.Value;
        }
        set => _enabled = value;
    }

    /// <summary>
    /// 诊断日志的统一出口：只在自检开启时输出。
    /// </summary>
    /// <remarks>
    /// 排查期加的那些 <c>[together][diag]</c> 日志都走这里。默认<b>静默</b>，
    /// 需要时用环境变量 <c>TOGETHER_SELFCHECK=1</c> 打开，避免正常游玩时刷爆日志。
    /// </remarks>
    public static void Write(string message)
    {
        if (Enabled)
        {
            Log.Info(message);
        }
    }

    internal static void Report(string context)
    {
        if (!Enabled)
        {
            return;
        }

        if (TogetherPair.Anchor is not { } anchor || TogetherPair.MemberCount < 2)
        {
            return;
        }

        Log.Info($"[together][selfcheck] {context}");
        Log.Info($"[together][selfcheck]   anchor {Describe(anchor)}");

        foreach (var echo in TogetherPair.Echoes)
        {
            Log.Info($"[together][selfcheck]   echo   {Describe(echo)}");
        }
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
              + $",draw={combat.DrawPile.Cards.Count}"
              + $",discard={combat.DiscardPile.Cards.Count}"
              + $",exhaust={combat.ExhaustPile.Cards.Count}"
              + $",play={combat.PlayPile.Cards.Count}"
              + $",energy={combat.Energy}";

        return $"hp={creature?.CurrentHp}/{creature?.MaxHp} block={creature?.Block}"
               + $" powers=[{powers}] {piles}";
    }
}

/// <summary>
/// 校验和生成点的自检钩子。
/// </summary>
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
    private static void Postfix(string __0)
    {
        SelfCheck.Report(__0);
    }
}
