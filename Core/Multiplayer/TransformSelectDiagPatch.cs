using HarmonyLib;

using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace Together.Core.Multiplayer;

/// <summary>
/// 诊断："从卡组里选一张牌"的界面到底有没有弹、候选有几张。
/// </summary>
/// <remarks>
/// <para>
/// 变牌 / 附魔 / 先古克隆 / 休息处强化 走的都是 <see cref="CardSelectCmd" /> 的
/// <c>FromDeck*</c> 系列：先按玩家取候选（<c>PileType.Deck.GetPile(player).Cards</c> 再加过滤），
/// <b>候选为空就直接返回空数组</b>，连界面都不创建；否则再由 <c>ShouldSelectLocalCard(player)</c>
/// 决定"本机弹界面"还是"等远端"。
/// </para>
/// <para>
/// 实测（真实联机，共生体）：休息处的"强化"上，回声那边正常弹出并选中了
/// <c>PILLAR_OF_CREATION</c>，而锚点这边在同一个选项上返回了
/// <c>PlayerChoiceResult deck</c>（空）且 <c>success False</c> —— 也就是候选集算成了空。
/// 这条日志把"取到几张口牌、能选几张口牌、本机是谁"一起打出来，用来分辨
/// 究竟是<b>候选为空</b>（界面不会弹）还是<b>界面开给了别人</b>（卡住）。
/// </para>
/// <para>默认打印、每个键有上限（<see cref="CappedLog" />），排查完可以整体删掉。</para>
/// </remarks>
internal static class CardSelectDiag
{
    public static void Log(string entry, Player? player)
    {
        if (player is null)
        {
            return;
        }

        CardPile? pile;
        try
        {
            pile = PileType.Deck.GetPile(player);
        }
        catch (Exception ex)
        {
            CappedLog.Info("select.entry", $"{entry}：取卡组抛异常 {ex.GetType().Name}: {ex.Message}");
            return;
        }

        var pileCount = pile?.Cards.Count ?? -1;
        var fieldCount = player.Deck.Cards.Count;
        var upgradable = pile?.Cards.Count(IsUpgradable) ?? -1;
        var transformable = pile?.Cards.Count(IsTransformable) ?? -1;

        CappedLog.Info(
            "select.entry",
            $"{entry}：player={player.NetId} isAnchor={TogetherPair.IsAnchor(player)} "
            + $"isEcho={TogetherPair.IsEcho(player)} 共生局={TogetherPair.IsActive} 本机={LocalContext.NetId} "
            + $"| GetPile={pileCount} 张、player.Deck={fieldCount} 张、可升级={upgradable}、可变形={transformable}"
            + $"（两处是同一口堆={ReferenceEquals(pile, player.Deck)}）");
    }

    private static bool IsUpgradable(CardModel card)
    {
        try
        {
            return card.IsUpgradable;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsTransformable(CardModel card)
    {
        try
        {
            return card.IsTransformable;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>变牌（<c>FromDeckForTransformation</c>）。</summary>
[HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromDeckForTransformation))]
internal static class TransformSelectDiagPatch
{
    [HarmonyPrefix]
    private static void Prefix(Player __0)
    {
        CardSelectDiag.Log("变牌选牌", __0);
    }
}

/// <summary>通用的"从卡组选一张"（<c>FromDeckGeneric</c>：先古克隆等不少事件走它）。</summary>
[HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromDeckGeneric))]
internal static class DeckSelectDiagPatch
{
    [HarmonyPrefix]
    private static void Prefix(Player __0)
    {
        CardSelectDiag.Log("卡组选牌", __0);
    }
}

/// <summary>休息处强化（<c>FromDeckForUpgrade</c>）。</summary>
[HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromDeckForUpgrade))]
internal static class UpgradeSelectDiagPatch
{
    [HarmonyPrefix]
    private static void Prefix(Player __0)
    {
        CardSelectDiag.Log("强化选牌", __0);
    }
}

/// <summary>附魔（<c>FromDeckForEnchantment</c>）。</summary>
[HarmonyPatch(
    typeof(CardSelectCmd),
    nameof(CardSelectCmd.FromDeckForEnchantment),
    new[] { typeof(Player), typeof(EnchantmentModel), typeof(int), typeof(CardSelectorPrefs) })]
internal static class EnchantSelectDiagPatch
{
    [HarmonyPrefix]
    private static void Prefix(Player __0)
    {
        CardSelectDiag.Log("附魔选牌", __0);
    }
}
