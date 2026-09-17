using System.Globalization;

using MegaCrit.Sts2.Core.Runs;

using STS2RitsuLib;
using STS2RitsuLib.Settings;
using STS2RitsuLib.Utils.Persistence;

using Together.Core.Multiplayer;

namespace Together.Core.Config;

/// <summary>
/// 设置界面：只有一个总开关（共生体）。
/// </summary>
/// <remarks>
/// <para>
/// "谁和谁配对"不在这里选——那是在多人选人界面按「共生体」按钮确定的（见
/// <c>Together.Core.Multiplayer.SymbiosisMembers</c>），这样两个玩家可以选<b>任意角色</b>
/// （甚至不同角色）组成共生体。
/// </para>
/// <para>
/// 文案一律用 <c>ModSettingsText.Literal</c>，不依赖本地化表（本 mod 已经没有本地化文件了）。
/// </para>
/// </remarks>
internal static class TogetherModSettingsPage
{
    private static bool _registered;

    public static void Register()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;

        var symbiosisBinding = new ModSettingsValueBinding<TogetherSettings, bool>(
            Const.ModId,
            TogetherSettingsStore.DataKey,
            SaveScope.Global,
            // 读"本局实际生效"的值：客户端显示的是主机广播过来的那一份，
            // 而不是自己本地文件里的值 —— 否则客户端会看到一个自己改不动、也不生效的数字。
            _ => TogetherSettingsSync.EffectiveSymbiosisEnabled,
            (settings, value) =>
            {
                settings.SymbiosisEnabled = value;

                // 关掉共生体 = 取消这一次的共生体设定：把已经确定过的成员一并清掉并广播，
                // 否则下次再打开开关时，上一局按过按钮的人会"自动"回到组里。
                if (!value)
                {
                    SymbiosisMembers.Reset(RunManager.Instance?.NetService, "symbiosis_disabled");
                }

                // 主机改完立刻广播：配不配对要两端算得一样，否则会分叉。
                TogetherSettingsSync.PublishHostSettings("settings_changed");
            });

        var mergeBinding = new ModSettingsValueBinding<TogetherSettings, bool>(
            Const.ModId,
            TogetherSettingsStore.DataKey,
            SaveScope.Global,
            _ => TogetherSettingsSync.EffectiveMergeStarterDecks,
            (settings, value) =>
            {
                settings.MergeStarterDecks = value;
                TogetherSettingsSync.PublishHostSettings("settings_changed");
            });

        var hpBinding = new ModSettingsValueBinding<TogetherSettings, string>(
            Const.ModId,
            TogetherSettingsStore.DataKey,
            SaveScope.Global,
            _ => Math
                .Clamp(TogetherSettingsSync.EffectiveHpBonusPercent, 0, 100)
                .ToString(CultureInfo.InvariantCulture),
            (settings, value) =>
            {
                if (int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent))
                {
                    settings.HpBonusPercent = Math.Clamp(percent, 0, 100);
                }

                TogetherSettingsSync.PublishHostSettings("settings_changed");
            });

        var groupSizeBinding = new ModSettingsValueBinding<TogetherSettings, string>(
            Const.ModId,
            TogetherSettingsStore.DataKey,
            SaveScope.Global,
            _ => Math
                .Clamp(TogetherSettingsSync.EffectiveGroupSize, TogetherPair.MinMembers, TogetherPair.MaxMembers)
                .ToString(CultureInfo.InvariantCulture),
            (settings, value) =>
            {
                if (int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
                {
                    settings.GroupSize = Math.Clamp(size, TogetherPair.MinMembers, TogetherPair.MaxMembers);
                }

                TogetherSettingsSync.PublishHostSettings("settings_changed");
            });

        var shareGoldBinding = new ModSettingsValueBinding<TogetherSettings, bool>(
            Const.ModId,
            TogetherSettingsStore.DataKey,
            SaveScope.Global,
            _ => TogetherSettingsSync.EffectiveShareGold,
            (settings, value) =>
            {
                settings.ShareGold = value;
                TogetherSettingsSync.PublishHostSettings("settings_changed");
            });

        RitsuLibFramework.RegisterModSettings(Const.ModId, page => page
            .WithTitle(ModSettingsText.Literal("Together · 共生体"))
            .WithModDisplayName(ModSettingsText.Literal("Together"))
            // 局内改设置没有意义（配对在选人阶段就定下来了），直接只读，避免"改了没生效"的困惑。
            .WithReadOnlyOnHostSurfaces(ModSettingsHostSurface.RunPause | ModSettingsHostSurface.CombatPause)
            .AddSection("symbiosis", section => section
                .WithTitle(ModSettingsText.Literal("共生体"))
                .AddToggle(
                    "symbiosis_enabled",
                    ModSettingsText.Literal("开启共生体"),
                    symbiosisBinding,
                    ModSettingsText.Dynamic(DescribeCurrent))
                .AddString(
                    "group_size",
                    ModSettingsText.Literal("共生体人数上限（2~4）"),
                    groupSizeBinding,
                    placeholder: ModSettingsText.Literal("例如 3"),
                    maxLength: 2,
                    description: ModSettingsText.Literal(
                        "共生体最多几个人共用身体：填 2 就是原来的双人共生体，填 3/4 可以让三个人一起操作。\n"
                        + "实际人数看选人界面里按「共生体」的人数：≥2 人即成组，没按按钮的玩家照常各玩各的。\n"
                        + "只填 2~4 的整数；填别的会被忽略并保留原值。"),
                    valueValidationVisual: IsValidGroupSize)
                .AddToggle(
                    "merge_starter_decks",
                    ModSettingsText.Literal("开局合并双方初始卡组（共享卡组 = p1 + p2）"),
                    mergeBinding,
                    ModSettingsText.Literal(
                        "开启：开局时把另一位玩家（回声 / p2）的初始卡组【复制】进共享卡组，"
                        + "共生体的卡组就是两个人的牌合在一起。\n"
                        + "关闭：共享卡组只包含锚点（p1，先确定那位）的初始卡组，回声那副不参与。\n"
                        + "注意：因为是复制，两人选同一个角色时开启它会得到两份初始卡（两个静默猎手 = 24 张）；"
                        + "想要「同角色只要一份」就把它关掉。"))
                .AddString(
                    "hp_bonus_percent",
                    ModSettingsText.Literal("血量上限提升：p2 最大生命的百分比（0~100）"),
                    hpBinding,
                    placeholder: ModSettingsText.Literal("例如 50"),
                    maxLength: 3,
                    description: ModSettingsText.Literal(
                        "把「回声（p2）最大生命」的百分之几加进共享血池：0 = 不加，100 = 把 p2 那一整份也加上。\n"
                        + "只在新开一局时生效一次（上限会写进存档，读档/重连不会重复加）。\n"
                        + "只填 0~100 的整数；填别的会被忽略并保留原值。"),
                    valueValidationVisual: IsValidPercent)
                .AddToggle(
                    "share_gold",
                    ModSettingsText.Literal("共享金币（组内一个钱包）"),
                    shareGoldBinding,
                    ModSettingsText.Literal(
                        "开启：共生体成员共用一个金币余额 —— 谁捡到金币、谁在商店花掉，都是改同一份余额。\n"
                        + "开局把所有人的起始金币【加起来】当共同余额（99 × 人数；只在开新局时加一次，"
                        + "读档/重连不会重复加）；关闭时各花各的。\n"
                        + "联机时以主机设置为准。"))
                .AddParagraph(
                    "how_it_works",
                    ModSettingsText.Literal(
                        "开启后，多人选人界面会出现「共生体」按钮：\n"
                        + "1. 想参加的人各自按下它，表示确定自己参加共生体；≥2 人确定后共用一个身体\n"
                        + "   （血量 / 格挡 / 状态 / 卡组 / 抽牌堆 / 弃牌堆都是同一份）；\n"
                        + "2. 手牌与能量仍然各人各一份（你打你的、我打我的）；\n"
                        + "3. 名额（= 上面的人数上限）满了以后，其他人按不了它；\n"
                        + "4. 只有一个人确定时不能起程（再按一次可以取消）；\n"
                        + "5. 每个人可以选任意角色（甚至不同角色）；共享卡组里放谁的初始卡由上面"
                        + "「开局合并双方初始卡组」决定（开启时会把所有回声的初始卡都并进来）；\n"
                        + "6. 关闭开关时，本 mod 完全不介入任何对局（正常原版局）。\n"
                        + "联机时以主机设置为准。"))));
    }

    private static string DescribeCurrent()
    {
        return TogetherSettingsSync.EffectiveSymbiosisEnabled
            ? "当前：开启 —— 选人界面有「共生体」按钮，两人确定后共用一个身体。"
            : "当前：关闭 —— 本 mod 不介入任何对局。";
    }

    /// <summary>文本框的"输入是否合法"提示：只认 0~100 的整数。</summary>
    private static bool IsValidPercent(string? value)
    {
        return int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent)
               && percent is >= 0 and <= 100;
    }

    /// <summary>文本框的"输入是否合法"提示：只认 2~4 的整数。</summary>
    private static bool IsValidGroupSize(string? value)
    {
        return int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size)
               && size is >= TogetherPair.MinMembers and <= TogetherPair.MaxMembers;
    }
}
