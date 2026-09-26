using System.Reflection;
using System.Threading.Tasks;

using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using Together.Core.Common;
using Together.Core.Foundation;

namespace Together.Core.Diagnostics;

/// <summary>
/// 「消耗一张牌」这条链的取证：牌、owner、它当前在哪口堆、目标消耗堆，以及这次调用最终是成功还是抛了。
/// </summary>
/// <remarks>
/// 起因：实测"打出余烬（Cinder，随机消耗一张手牌）"时，客户端那张牌<b>离开了手牌却没进消耗堆</b>、且没有报错
/// —— 而本体这句话只有三步（<c>CardCmd.Exhaust</c> → <c>CardPileCmd.Add(…, PileType.Exhaust, …)</c> → 钩子），
/// 光看现有日志分不出卡在哪一步。这里把"开始/结束"各打一条：结束那条会带上"任务是否异常"，
/// 所以哪怕异常被上层吞掉，也能从日志里看到。
/// <b>只在共享局出声</b>，单人/普通联机不产生任何日志。
/// </remarks>
[HarmonyPatch(
    typeof(CardCmd),
    nameof(CardCmd.Exhaust),
    new[] { typeof(PlayerChoiceContext), typeof(CardModel), typeof(bool), typeof(bool) })]
internal static class ExhaustPathProbePatch
{
    [HarmonyPrefix]
    private static void Prefix(CardModel card)
    {
        try
        {
            if (!TogetherPair.IsActive)
            {
                return;
            }

            CappedLog.Info("exhaust.begin", $"消耗开始：{Describe(card)}");
        }
        catch (Exception)
        {
            // 取证失败绝不能影响本体。
        }
    }

    [HarmonyPostfix]
    private static void Postfix(CardModel card, ref Task<CardPileAddResult?> __result)
    {
        try
        {
            if (!TogetherPair.IsActive || __result is null)
            {
                return;
            }

            var task = __result;

            // async 方法：Postfix 只在"第一个返回点"跑一次（那时 Task 往往还没完成），
            // 所以这里给 Task 挂一条续延，等它**真正结束/抛异常**时再打 —— 异常即使被上层吞掉也能看到。
            if (task.IsCompleted)
            {
                LogEnd(card, task);
                return;
            }

            task.ContinueWith(
                finished => LogEnd(card, finished),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch (Exception)
        {
            // 同上。
        }
    }

    private static void LogEnd(CardModel card, Task task)
    {
        try
        {
            var error = task.Exception?.GetBaseException();
            CappedLog.Info(
                "exhaust.end",
                error is null
                    ? $"消耗结束：{Describe(card)}"
                    : $"消耗结束（抛异常，可能被上层吞掉）：{Describe(card)} → {error.GetType().Name}: {error.Message}");
        }
        catch (Exception)
        {
            // 同上。
        }
    }

    /// <summary>把"这张牌 + 归属 + 现在在哪口堆 + 该去哪口消耗堆"压成一行。</summary>
    private static string Describe(CardModel? card)
    {
        if (card is null)
        {
            return "<card=null>";
        }

        var owner = card.Owner;
        var current = ModelAccess.PileOf(card);

        CardPile? target = null;
        try
        {
            target = owner is null ? null : PileType.Exhaust.GetPile(owner);
        }
        catch (Exception)
        {
            // 取不到就算了，日志里留个 null。
        }

        return $"card={card.Id.Entry} owner=netId{owner?.NetId}"
               + $" 当前堆={Describe(current)}（本机netId={MegaCrit.Sts2.Core.Context.LocalContext.NetId}）"
               + $" 目标消耗堆={Describe(target)}";
    }

    private static string Describe(CardPile? pile)
    {
        return pile is null
            ? "<null>"
            : $"{pile.Type}#{pile.GetHashCode()}（{pile.Cards.Count}张）";
    }
}
