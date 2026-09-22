using System.Reflection;

using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using Together.Core.Combat;
using Together.Core.Utils;

namespace Together.Core.Patches.Deck;

/// <summary>
/// 诊断："从卡组里选一张牌"的界面到底有没有弹、候选有几张。
/// </summary>
/// <remarks>
/// <para>
/// 变牌 / 附魔 / 先古克隆 / 休息处强化 走的都是 <c>CardSelectCmd.FromDeck*</c>：
/// 先按玩家取候选（<c>PileType.Deck.GetPile(player).Cards</c> 再加过滤），<b>候选为空就直接返回空数组</b>、
/// 连界面都不建；否则再由 <c>ShouldSelectLocalCard(player)</c> 决定"本机弹界面"还是"等远端"。
/// </para>
/// <para>
/// 这条日志把"取到几张口牌、能选几张口牌、本机是谁"一起打出来，
/// 用来分辨究竟是<b>候选为空</b>（界面不会弹）还是<b>界面开给了别人</b>（卡住）。
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

/// <summary>四条"从卡组选牌"入口都打一条（变牌 / 通用 / 强化 / 附魔）。</summary>
[HarmonyPatch]
internal static class CardSelectDiagPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(CardSelectCmd), nameof(CardSelectCmd.FromDeckForTransformation));
        yield return AccessTools.Method(typeof(CardSelectCmd), nameof(CardSelectCmd.FromDeckGeneric));
        yield return AccessTools.Method(typeof(CardSelectCmd), nameof(CardSelectCmd.FromDeckForUpgrade));
        yield return AccessTools.Method(
            typeof(CardSelectCmd),
            nameof(CardSelectCmd.FromDeckForEnchantment),
            new[] { typeof(Player), typeof(EnchantmentModel), typeof(int), typeof(CardSelectorPrefs) });
    }

    [HarmonyPrefix]
    private static void Prefix(Player __0, MethodBase __originalMethod)
    {
        CardSelectDiag.Log(Label(__originalMethod.Name), __0);
    }

    private static string Label(string method)
    {
        return method switch
        {
            nameof(CardSelectCmd.FromDeckForTransformation) => "变牌选牌",
            nameof(CardSelectCmd.FromDeckForUpgrade) => "强化选牌",
            nameof(CardSelectCmd.FromDeckForEnchantment) => "附魔选牌",
            _ => "卡组选牌",
        };
    }
}
