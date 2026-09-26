using System.Diagnostics;

using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using Together.Core.Foundation;

namespace Together.Core.Diagnostics;

/// <summary>
/// "这张牌的 owner 被谁改了"的取证：只要归属真的变了就打一条（含前几个调用者帧）。
/// </summary>
/// <remarks>
/// 起因：连续两次分歧（`#62` 余烬、`#101` 进阶之灾）都伴随同一个怪现象 ——
/// <b>两端对"卡组里的牌归谁"结论相反</b>（主机端 deck 全归回声、客户端端 deck 全归主机，即"各端都归对方"）。
/// 这会让"按 <c>card.Owner</c> 反查牌堆 / 搬运"的路径在一端落地失败，表现成"某张牌从某堆消失"。
/// <c>CardModel.GiveToAnotherPlayer</c> 是本体唯一"改归属"的入口（我们自己那几处也走它），
/// 而且它是<b>同步方法</b>（就一行 <c>_owner = player</c>），所以在这里取证最直接：一次复现就能看出是哪条路径改的。
/// <b>只在共享局出声</b>，而且"归属没变"时不打。
/// </remarks>
[HarmonyPatch(typeof(CardModel), nameof(CardModel.GiveToAnotherPlayer))]
internal static class OwnerTraceProbePatch
{
    [HarmonyPrefix]
    private static void Prefix(CardModel __instance, Player player)
    {
        try
        {
            if (!TogetherPair.IsActive)
            {
                return;
            }

            var from = __instance.Owner;
            if (ReferenceEquals(from, player))
            {
                return;
            }

            CappedLog.Info(
                "own.trace",
                $"归属变更：{__instance.Id.Entry} netId{from?.NetId} → netId{player?.NetId}"
                + $"（本机netId={MegaCrit.Sts2.Core.Context.LocalContext.NetId}）"
                + $"｜调用者：{CallerHint()}");
        }
        catch (Exception)
        {
            // 取证失败绝不能影响本体。
        }
    }

    /// <summary>只取栈里最靠前的几个"有意义的"帧当线索（跳掉取证自己、Harmony 与 BCL）。</summary>
    private static string CallerHint()
    {
        var frames = new StackTrace(2, false).GetFrames();
        if (frames is null)
        {
            return "<unknown>";
        }

        var parts = new List<string>(3);
        foreach (var frame in frames)
        {
            var method = frame.GetMethod();
            var type = method?.DeclaringType;
            if (method is null || type?.FullName is not { } fullName)
            {
                continue;
            }

            if (fullName.Contains(nameof(OwnerTraceProbePatch), StringComparison.Ordinal)
                || fullName.StartsWith("HarmonyLib", StringComparison.Ordinal)
                || fullName.StartsWith("System.", StringComparison.Ordinal)
                || fullName.StartsWith("MegaCrit.Sts2.Core.Entities", StringComparison.Ordinal))
            {
                continue;
            }

            parts.Add($"{type.Name}.{method.Name}");
            if (parts.Count >= 3)
            {
                break;
            }
        }

        return parts.Count == 0 ? "<unknown>" : string.Join(" ← ", parts);
    }
}
