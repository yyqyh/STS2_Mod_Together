using System.Runtime.CompilerServices;

using MegaCrit.Sts2.Core.Models;

namespace Together.Core.Shared.Deck;

/// <summary>「这张牌是不是共享卡组的一员」的登记表。</summary>
/// <remarks>
/// <para>
/// 用途只有一个：给 <c>EventFlow</c> 里那三条"失效选择守卫"（附魔 / 从卡组移除 / 变牌）当判据 ——
/// 共享游戏里"同一张牌被两个人同时选"才会出现"另一份事件实例先动过它"的并发冲突，
/// 所以只有<b>属于共享卡组</b>的失效项才该被我们静默过滤掉；别的 mod 自己的牌该怎么报错还是怎么报错
/// （早先那版是"共享局里任何失效选择都过滤掉"，会吞掉别的 mod 拿异常当控制流的逻辑）。
/// </para>
/// <para>
/// 登记时机（两处，都在已有的遍历里顺手做，不额外扫牌）：
/// ① <see cref="SharedDeckOwnerNormalizePatch" /> 每次 RunState 重建后遍历共享卡组时；
/// ② <c>SharedHookOwnerWidenPatch</c> 那条"牌进主卡组"的通路上（开局之后抓进卡组的牌）。
/// </para>
/// <para>
/// 用 <see cref="ConditionalWeakTable{TKey,TValue}" />：按<b>引用</b>认牌（同名牌是不同对象），
/// 牌对象回收后条目自动消失，跨局/读档不需要手工清理。
/// </para>
/// </remarks>
internal static class SharedDeckRegistry
{
    private static readonly ConditionalWeakTable<CardModel, object> Shared = new();

    private static readonly object Marker = new();

    /// <summary>把一张牌登记为"共享卡组的一员"（幂等）。</summary>
    public static void Register(CardModel? card)
    {
        if (card is null)
        {
            return;
        }

        Shared.Remove(card);
        Shared.Add(card, Marker);
    }

    /// <summary>这张牌是不是共享卡组的一员。</summary>
    public static bool IsShared(CardModel? card)
    {
        return card is not null && Shared.TryGetValue(card, out _);
    }
}
