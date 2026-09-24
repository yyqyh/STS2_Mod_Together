using System.Runtime.CompilerServices;

using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

namespace Together.Core.Combat;

/// <summary>记录"这次施加是谁发起的"（卡 / 药水来源），给镜像判据判断"这份能力是不是<b>能力自己衍生</b>出来的"。</summary>
/// <remarks>
/// 用途（见 <c>PowerMirror.OnPowerApplied</c>）：拦截的 <c>CoveredPower</c> 会在自己的 <c>AfterApplied</c> 里
/// 给掩护者挂一份 <c>InterceptPower</c>（<c>cardSource</c> 传 <c>null</c>）。那份"衍生能力"如果再被我们镜像，
/// 就会得到"自己掩护自己"这种自相矛盾的关系。判据是结构性的：<b>没有卡 / 药水来源的跨成员施加 = 衍生施加 → 不镜像</b>，
/// 而不是靠能力名字。
/// </remarks>
internal static class PowerApplySource
{
    private static readonly ConditionalWeakTable<PowerModel, object> Sources = new();

    private static readonly object NoSource = new();

    /// <summary>记下这次施加的来源（<c>null</c> 表示"由能力自己发起"）。</summary>
    public static void Record(PowerModel power, CardModel? cardSource)
    {
        try
        {
            Sources.Remove(power);
            Sources.Add(power, cardSource ?? NoSource);
        }
        catch (Exception)
        {
            // 记不上就按"有来源"处理（保守：照旧镜像）。
        }
    }

    /// <summary>这次施加有没有卡 / 药水来源（没记录过也算有，保持原有行为）。</summary>
    public static bool HasActionSource(PowerModel power)
    {
        try
        {
            return !Sources.TryGetValue(power, out var source) || !ReferenceEquals(source, NoSource);
        }
        catch (Exception)
        {
            return true;
        }
    }
}

/// <summary>在 <c>PowerCmd.Apply</c> 入口记下"这份能力是卡/药水打出来的，还是能力自己衍生出来的"。</summary>
[HarmonyPatch(typeof(PowerCmd), nameof(PowerCmd.Apply), new[]
{
    typeof(PlayerChoiceContext), typeof(PowerModel), typeof(Creature), typeof(decimal),
    typeof(Creature), typeof(CardModel), typeof(bool),
})]
internal static class PowerApplySourceRecordPatch
{
    [HarmonyPrefix]
    private static void Prefix(PowerModel __1, object[] __args)
    {
        // 用 __args 按位置取，而不是 `__5`：这样即使本体改了参数顺序也只是取错、不会让整个补丁类装不上。
        // （参数表是 ctx / power / target / amount / applier / cardSource / silent，cardSource 在索引 5。）
        PowerApplySource.Record(__1, __args.Length > 5 ? __args[5] as CardModel : null);
    }
}
