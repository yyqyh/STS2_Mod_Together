using System.Runtime.CompilerServices;
using System.Threading.Tasks;

using HarmonyLib;

using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;

namespace Together.Core.Multiplayer;

/// <summary>
/// 记住每张牌"最后一次躺在谁的手牌里"。
/// </summary>
/// <remarks>
/// <para>
/// 共生体下共享牌堆里的牌必须<b>统一归锚点</b>：本体的 <c>CardModel.Pile</c> 是用
/// <c>_owner.Piles</c> 反查自己所在的堆的，牌要是带着别人的归属躺在锚点的堆里，
/// 迟早会有一处反查不到（更别说洗牌那条"同一堆的牌 owner 必须一致"的校验）。
/// </para>
/// <para>
/// 但"归锚点"会丢掉一个信息：<b>这张牌原本是谁的</b>。而本体不少遗物/能力恰恰是靠
/// <c>card.Owner == 自己</c> 来认领"我的牌"的 —— 例如金纸（<c>JossPaper</c>）：
/// <c>AfterCardExhausted</c> 里 <c>if (card.Owner == base.Owner)</c> 才累计消耗数。
/// 于是 P2 消耗自己的手牌时，牌已经被归一成锚点，P2 的金纸永远不计数。
/// </para>
/// <para>所以这里把"最后的手牌主人"单独记一份，供下面的补丁在派发钩子时临时还原。</para>
/// </remarks>
internal static class CardLastHandOwner
{
    private static readonly ConditionalWeakTable<CardModel, Player> LastHand = new();

    public static void Remember(CardModel card, Player owner)
    {
        lock (LastHand)
        {
            LastHand.Remove(card);
            LastHand.Add(card, owner);
        }
    }

    public static bool TryGet(CardModel card, out Player? owner)
    {
        return LastHand.TryGetValue(card, out owner);
    }
}

/// <summary>
/// <c>AfterCardExhausted</c> 派发期间，把卡牌的归属临时还原成"最后持有它的玩家"。
/// </summary>
/// <remarks>
/// <para>
/// 牌进消耗堆时已经按共享牌堆的规则归一成锚点了，而归属于谁正是金纸这类遗物的判据。
/// 派发钩子前临时改回、钩子跑完（含其中的 await）再改回来，既让遗物认得出"这是我的牌"，
/// 又不破坏共享牌堆那条"堆里的牌归属一致"的不变量。
/// </para>
/// <para>
/// 因为 <c>Hook.AfterCardExhausted</c> 是 async 方法，Prefix/Postfix 都跑在同步段里，
/// 所以恢复动作要把返回的 <c>Task</c> 包一层，等它真正结束再执行。
/// </para>
/// </remarks>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardExhausted))]
internal static class ExhaustedOwnerForHooksPatch
{
    /// <summary>参数位置：combatState=0, choiceContext=1, <b>card=2</b>, causedByEthereal=3。</summary>
    [HarmonyPrefix]
    private static void Prefix(CardModel __2, ref Player? __state)
    {
        __state = null;

        if (!TogetherPair.IsActive || __2 is null)
        {
            return;
        }

        if (!CardLastHandOwner.TryGet(__2, out var lastOwner) || lastOwner is null)
        {
            return;
        }

        if (ReferenceEquals(__2.Owner, lastOwner))
        {
            return;
        }

        __state = __2.Owner;
        __2.GiveToAnotherPlayer(lastOwner);

        CappedLog.Info(
            "owner.restore",
            $"消耗结算：把 {__2.Id.Entry} 的归属临时还给 netId={lastOwner.NetId}"
            + $"（结算前已按共享牌堆归一为 netId={__state?.NetId}）");
    }

    [HarmonyPostfix]
    private static void Postfix(CardModel __2, Player? __state, ref Task __result)
    {
        if (__state is null || __result is null || __2 is null)
        {
            return;
        }

        __result = RestoreAfterAsync(__result, __2, __state);
    }

    private static async Task RestoreAfterAsync(Task task, CardModel card, Player original)
    {
        try
        {
            await task;
        }
        finally
        {
            card.GiveToAnotherPlayer(original);
        }
    }
}
