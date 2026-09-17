using HarmonyLib;

using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;

using Together.Core.Config;

namespace Together.Core.Multiplayer;

/// <summary>
/// 金币共享（可选）：组内只有一个钱包。
/// </summary>
/// <remarks>
/// <para>
/// 本体的金币收口只有一处 —— <c>Player.Gold</c> 的 setter（<c>PlayerCmd.GainGold</c> / <c>LoseGold</c> /
/// <c>SetGold</c> 最终都是给这个属性赋值，并且会触发 <c>GoldChanged</c> 让顶栏刷新）。
/// 所以这里只挂 setter 的 Postfix：谁的钱变了就把同一个数值推给组里其他人。
/// </para>
/// <para>
/// 收敛性靠本体自己的判断：<c>if (value != Gold)</c> 才赋值，所以"值相同就不再触发"，不会来回推。
/// 另外还有一层重入守卫兜底。
/// </para>
/// <para>
/// <b>新开一局</b>时把所有人的起始金币<b>加起来</b>当共同余额（99 × 人数）；
/// 读档 / 重连只做"取最大值对齐"（存档里本来就是同一份，再求一次和就是每次重连都翻倍）。
/// 关掉这个开关时完全不管金币。
/// </para>
/// </remarks>
internal static class GoldMirror
{
    private static int _depth;

    /// <summary>
    /// 成组时对齐金币。
    /// </summary>
    /// <param name="isNewRun">
    /// 是不是新开一局。新局把所有人的起始金币<b>加起来</b>（99 × 人数）当共同余额；
    /// 读档 / 重连只取组内最大值对齐。
    /// </param>
    public static void OnArm(bool isNewRun)
    {
        if (!TogetherSettingsSync.EffectiveShareGold || !TogetherPair.IsActive)
        {
            return;
        }

        var members = TogetherPair.Members().ToList();
        if (members.Count < 2)
        {
            return;
        }

        // 关键：只有新开一局才"合并"（求和）。读档再求一次和 = 每次重连都翻倍。
        var shared = isNewRun
            ? members.Sum(member => member.Gold)
            : members.Max(member => member.Gold);

        _depth++;
        try
        {
            foreach (var member in members)
            {
                if (member.Gold != shared)
                {
                    member.Gold = shared;
                }
            }
        }
        finally
        {
            _depth--;
        }

        Log.Info(
            $"[together] 共生体金币{(isNewRun ? "合并" : "对齐")}：{members.Count} 人"
            + $"（{(isNewRun ? "起始金币求和" : "取最大值对齐")}）→ 共同余额 {shared}");
    }

    internal static void OnGoldChanged(Player player)
    {
        if (_depth > 0 || !TogetherSettingsSync.EffectiveShareGold)
        {
            return;
        }

        if (!TogetherPair.IsMember(player))
        {
            // 诊断：如果商店/事件拿的是"过期的 Player 对象"，这里会刷出来，一看就知道是对象引用的问题。
            CappedLog.Info(
                "gold.skip",
                $"金币变化没同步（这个对象不在当前共生体里）：netId={player.NetId} gold={player.Gold}");
            return;
        }

        var synced = 0;
        _depth++;
        try
        {
            foreach (var other in TogetherPair.OthersOf(player))
            {
                if (other.Gold != player.Gold)
                {
                    other.Gold = player.Gold;
                }

                synced++;
            }
        }
        finally
        {
            _depth--;
        }

        CappedLog.Info(
            "gold.changed",
            $"金币同步：netId={player.NetId} 变成 {player.Gold}（推给 {synced} 人）");
    }
}

/// <summary>金币变化的唯一收口：<c>Player.Gold</c> 的 setter。</summary>
[HarmonyPatch(typeof(Player), "Gold", MethodType.Setter)]
internal static class GoldMirrorPatch
{
    [HarmonyPostfix]
    private static void Postfix(Player __instance)
    {
        GoldMirror.OnGoldChanged(__instance);
    }
}

/// <summary>
/// 消费路径的双保险：<c>PlayerCmd.LoseGold</c>（商店买卡/删牌、事件扣钱都走它）。
/// </summary>
/// <remarks>
/// 本体所有金币变化的收口确实是 <c>Player.Gold</c> 的 setter，上面那个补丁理论上已经覆盖；
/// 这里再挂一条的原因很实际：<c>LoseGold</c> 是本体的"扣钱"语义入口，一旦将来有哪条扣钱路径
/// 绕过 setter（或者 setter 那条补丁因为别的原因没跑到），这条能兜住。
/// 两个补丁都是幂等的（值相同不会重复推）。
/// </remarks>
[HarmonyPatch(typeof(PlayerCmd), nameof(PlayerCmd.LoseGold))]
internal static class LoseGoldMirrorPatch
{
    [HarmonyPostfix]
    private static void Postfix(Player __1)
    {
        GoldMirror.OnGoldChanged(__1);
    }
}
