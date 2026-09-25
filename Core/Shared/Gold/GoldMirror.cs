using System.Reflection;
using System.Runtime.CompilerServices;

using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using Together.Core.Alignment;
using Together.Core.Common;
using Together.Core.Diagnostics;
using Together.Core.Foundation;
using Together.Core.Settings;
using Together.Core.Shared.Body;
using Together.Core.Shared.Orb;
using Together.Core.Shared.Pet;
using Together.Core.Shared.Power;
using Together.Core.Ui;

namespace Together.Core.Shared.Gold;

/// <summary>金币共享（可选）：组内只有一个钱包。</summary>
/// <remarks>
/// 金币收口只有一处 —— <c>Player.Gold</c> 的 setter（<c>GainGold</c>/<c>LoseGold</c>/<c>SetGold</c>
/// 最终都给它赋值并触发 <c>GoldChanged</c> 刷新顶栏），所以只挂 setter 的 Postfix：谁的钱变了就推给组里其他人。
/// 收敛性靠本体的 <c>if (value != Gold)</c>，另有一层重入守卫兜底。
/// <b>新开一局</b>把所有人起始金币<b>加起来</b>当共同余额（99 × 人数）；读档/重连只"取最大值对齐"
/// （存档里本来就是同一份，再求和会每次重连都翻倍）。开关关掉时完全不管金币。
/// </remarks>
internal static class GoldMirror
{
    private static int _depth;

    /// <summary>成组时对齐金币。<paramref name="isNewRun" /> = 新局求和（99 × 人数），否则取组内最大值对齐。</summary>
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
            // 保存 / 读档 / 重连会把 RunState 重建一遍：重建窗口里新实例已经被改成存档值，
            // 成员判定还是旧的（引用相等），所以新实例走到这里 —— 按 netId 认领回当前实例。
            var claimed = TogetherPair.MemberByNetId(player.NetId);
            if (claimed is null)
            {
                // 真的不在组里（没开共享 / 不是这局的成员）：留一条诊断，一眼能看出是对象引用的问题。
                CappedLog.Info(
                    "gold.skip",
                    $"金币变化没同步（这个对象不在当前共生体里）：netId={player.NetId} gold={player.Gold}");
                return;
            }

            // 认领回来后**用当前实例的值**继续镜像，不推旧身带的那个值：
            // 实测旧身带的是"存档里那一份"——Setsuna 那局旧回声是 99，而共享余额已经是 198，
            // 推出去等于把钱包打回去，紧接着 Arm 的"取最大值对齐"还会把这个错值固化下来。
            CappedLog.Info(
                "gold.reclaim",
                $"金币变化来自重建中的实例（RunState 重建窗口）：netId={player.NetId}"
                + $" 该实例={player.Gold} 当前成员={claimed.Gold} → 改用当前成员继续镜像");
            player = claimed;
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

/// <summary>消费路径的双保险：<c>PlayerCmd.LoseGold</c>（商店买卡/删牌、事件扣钱都走它）。</summary>
/// <remarks>
/// setter 那条理论上已覆盖，这里再挂一条是兜底：将来若有扣钱路径绕过 setter（或 setter 补丁没跑到）也能接住。
/// 两个补丁都幂等（值相同不重复推）。
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
