using System.Diagnostics;

using HarmonyLib;

using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

using Together.Core.Foundation;
using Together.Core.Shared.Deck;

namespace Together.Core.Diagnostics;

/// <summary>
/// "整副卡组的 owner 被谁改写"的取证（<b>硬写入口</b>那一半）。
/// </summary>
/// <remarks>
/// <para>
/// 起因：2026-09-25 的分歧 <c>#110</c> 里，两端 <c>[sync]</c> 行从<b>本局第一个校验点</b>就是
/// "整副卡组归同一个人"、而且两端互为镜像（主机全归回声、客户端全归锚点）。但
/// <see cref="OwnerTraceProbePatch" />（只挂 <c>CardModel.GiveToAnotherPlayer</c>）在那个窗口里<b>一条都没有</b>
/// —— 说明那次改写没走"改归属"那条路，走的是<b>牌堆被重建</b>：<c>SerializableCard</c> 不带 owner，
/// 重建时 <c>RunState.AddCard(card, owner)</c> 会执行 <c>card.Owner = owner</c>（<c>RunState.cs:404</c>），
/// 而那是 <c>Owner</c> setter，不是 <c>GiveToAnotherPlayer</c>。
/// </para>
/// <para>
/// 所以这里补两个探针，都<b>只在共享局出声</b>、都带前几个调用者帧：
/// ① <c>RunState.AddCard(CardModel, Player)</c> / <c>RemoveCard</c>（"牌进/出 run 状态"这条硬写路径）；
/// ② 卡组那口堆的 <c>AddInternal / RemoveInternal / Clear</c>（"整副卡组被换掉"必然在这里留下 26 增 26 删）。
/// </para>
/// <para>
/// <b>只取证，不改行为</b>：定位到是哪条路径之后再决定在哪里收口（现在收口点是
/// <see cref="SharedDeckOwnership.RepairCurrent" /> 的"读档 / 开战"两个时机）。
/// 排查完可以整体删掉这个文件，或者把 <c>CappedLog</c> 换成 <c>SelfCheck.Write</c>。
/// </para>
/// </remarks>
internal static class OwnerWriteProbe
{
    /// <summary>只报"归属真的变了"的那些；没变的不出声（噪音太大）。</summary>
    public static void ReportAdded(CardModel? card, Player? owner, Player? before)
    {
        if (!TogetherPair.IsActive || card is null)
        {
            return;
        }

        if (ReferenceEquals(before, owner))
        {
            return;
        }

        // 逐张流水账：默认静默，排查时用 TOGETHER_DRIFT=1 打开（见 DriftLog）。
        DriftLog.Info(
            "drift.addcard",
            $"AddCard：{card.Id.Entry} netId{before?.NetId} → netId{owner?.NetId}"
            + $"（本机netId={MegaCrit.Sts2.Core.Context.LocalContext.NetId}）｜调用者：{CallerHint()}");
    }

    /// <summary>牌堆增删的取证（只卡组那口堆）。</summary>
    public static void ReportPile(string op, CardPile? pile, CardModel? card)
    {
        if (!TogetherPair.IsActive || pile is null || pile.Type != PileType.Deck)
        {
            return;
        }

        DriftLog.Info(
            "drift.deck",
            $"卡组堆 {op}：{card?.Id.Entry ?? "<clear>"} owner=netId{SharedDeckOwnership.OwnerOf(card)?.NetId}"
            + $" 堆内={pile.Cards.Count}｜调用者：{CallerHint()}");
    }

    /// <summary>只取栈里最靠前的几个"有意义的"帧当线索（跳掉取证自己、Harmony 与 BCL）。</summary>
    public static string CallerHint()
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

            if (fullName.Contains(nameof(OwnerWriteProbe), StringComparison.Ordinal)
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

/// <summary>牌"进 run 状态"的硬写路径（<c>card.Owner = owner</c>）。</summary>
[HarmonyPatch(
    typeof(RunState),
    nameof(RunState.AddCard),
    new[] { typeof(CardModel), typeof(Player) })]
internal static class RunStateAddCardProbePatch
{
    [HarmonyPrefix]
    private static void Prefix(CardModel __0, out Player? __state)
    {
        __state = SharedDeckOwnership.OwnerOf(__0);
    }

    [HarmonyPostfix]
    private static void Postfix(CardModel __0, Player __1, Player? __state)
    {
        try
        {
            OwnerWriteProbe.ReportAdded(__0, __1, __state);
        }
        catch (Exception)
        {
            // 取证失败绝不能影响本体。
        }
    }
}

/// <summary>牌"被移出 run 状态"（<c>card.Owner = null</c>）。</summary>
[HarmonyPatch(typeof(RunState), nameof(RunState.RemoveCard), new[] { typeof(CardModel) })]
internal static class RunStateRemoveCardProbePatch
{
    [HarmonyPostfix]
    private static void Postfix(CardModel __0)
    {
        try
        {
            if (TogetherPair.IsActive)
            {
                DriftLog.Info(
                    "drift.addcard",
                    $"RemoveCard：{__0.Id.Entry}（owner 被置空）｜调用者：{OwnerWriteProbe.CallerHint()}");
            }
        }
        catch (Exception)
        {
            // 忽略。
        }
    }
}

/// <summary>卡组堆增牌。</summary>
[HarmonyPatch(typeof(CardPile), nameof(CardPile.AddInternal))]
internal static class DeckPileAddProbePatch
{
    [HarmonyPostfix]
    private static void Postfix(CardPile __instance, CardModel __0)
    {
        try
        {
            OwnerWriteProbe.ReportPile("+", __instance, __0);
        }
        catch (Exception)
        {
            // 忽略。
        }
    }
}

/// <summary>卡组堆删牌。</summary>
[HarmonyPatch(typeof(CardPile), nameof(CardPile.RemoveInternal))]
internal static class DeckPileRemoveProbePatch
{
    [HarmonyPostfix]
    private static void Postfix(CardPile __instance, CardModel __0)
    {
        try
        {
            OwnerWriteProbe.ReportPile("-", __instance, __0);
        }
        catch (Exception)
        {
            // 忽略。
        }
    }
}

/// <summary>卡组堆整口清空（"整副卡组被换掉"最常见的形态）。</summary>
[HarmonyPatch(typeof(CardPile), nameof(CardPile.Clear))]
internal static class DeckPileClearProbePatch
{
    [HarmonyPrefix]
    private static void Prefix(CardPile __instance)
    {
        try
        {
            if (TogetherPair.IsActive && __instance.Type == PileType.Deck && __instance.Cards.Count > 0)
            {
                DriftLog.Info(
                    "drift.deck",
                    $"卡组堆 clear：清掉 {__instance.Cards.Count} 张（清空前分布 {SharedDeckOwnership.DistributionOf(__instance)}）"
                    + $"｜调用者：{OwnerWriteProbe.CallerHint()}");
            }
        }
        catch (Exception)
        {
            // 忽略。
        }
    }
}
