using System.Globalization;

using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib.Settings;
using STS2RitsuLib.Utils.Persistence;
using STS2RitsuLib;
using Together.Core.Content;
using Together.Core.Utils;
using Together;

namespace Together.Core.Settings;

/// <summary>
/// 设置界面：合作模式总开关 + 几个共享规则。
/// </summary>
/// <remarks>
/// <para>
/// "谁和谁配对"不在这里选——那是在多人选人界面按「加入合作模式」决定的（见
/// <c>Together.Core.Multiplayer.SymbiosisMembers</c>），这样两个玩家可以选<b>任意角色</b>
/// （甚至不同角色）组成合作组。
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

                // 关掉合作模式 = 取消这一次的配对：把已经加入过的成员一并清掉并广播，
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
            .WithTitle(T("together.settings.page.title", "Together · 合作模式"))
            .WithModDisplayName(ModSettingsText.Literal("Together"))
            // 局内改设置没有意义（配对在选人阶段就定下来了），直接只读，避免"改了没生效"的困惑。
            .WithReadOnlyOnHostSurfaces(ModSettingsHostSurface.RunPause | ModSettingsHostSurface.CombatPause)
            .AddSection("symbiosis", section => section
                .WithTitle(T("together.settings.section.title", "合作模式"))
                .AddToggle(
                    "symbiosis_enabled",
                    T("together.settings.enabled.label", "开启合作模式（共享卡组）"),
                    symbiosisBinding,
                    ModSettingsText.Dynamic(DescribeCurrent))
                .AddToggle(
                    "merge_starter_decks",
                    T("together.settings.merge.label", "开局合并双方初始卡组（共享卡组 = p1 + p2）"),
                    mergeBinding,
                    T(
                        "together.settings.merge.description",
                        "开启：开局时把另一位玩家（回声 / p2）的初始卡组【复制】进共享卡组，"
                        + "合作组的卡组就是两个人的牌合在一起。\n"
                        + "关闭：共享卡组只包含锚点（p1，先确定那位）的初始卡组，回声那副不参与。\n"
                        + "注意：因为是复制，两人选同一个角色时开启它会得到两份初始卡（两个静默猎手 = 24 张）；"
                        + "想要「同角色只要一份」就把它关掉。"))
                .AddString(
                    "hp_bonus_percent",
                    T("together.settings.hp.label", "血量上限提升：p2 最大生命的百分比（0~100）"),
                    hpBinding,
                    placeholder: T("together.settings.hp.placeholder", "例如 50"),
                    maxLength: 3,
                    description: T(
                        "together.settings.hp.description",
                        "把「回声（p2）最大生命」的百分之几加进共享血池：0 = 不加，100 = 把 p2 那一整份也加上。\n"
                        + "只在新开一局时生效一次（上限会写进存档，读档/重连不会重复加）。\n"
                        + "只填 0~100 的整数；填别的会被忽略并保留原值。"),
                    valueValidationVisual: IsValidPercent)
                .AddToggle(
                    "share_gold",
                    T("together.settings.gold.label", "共享金币（组内一个钱包）"),
                    shareGoldBinding,
                    T(
                        "together.settings.gold.description",
                        "开启：合作组成员共用一个金币余额 —— 谁捡到金币、谁在商店花掉，都是改同一份余额。\n"
                        + "开局把所有人的起始金币【加起来】当共同余额（99 × 人数；只在开新局时加一次，"
                        + "读档/重连不会重复加）；关闭时各花各的。\n"
                        + "联机时以主机设置为准。"))
                .AddParagraph(
                    "how_it_works",
                    T(
                        "together.settings.howto.description",
                        "开启后，多人选人界面的右下角会出现「加入合作模式」按钮：\n"
                        + "1. 想参加的人各自按下它 —— 按下去同时等于按了官方的「确认准备」；"
                        + "≥2 人加入即成组，共用一个身体\n"
                        + "   （血量 / 格挡 / 状态 / 卡组 / 抽牌堆 / 弃牌堆都是同一份）；\n"
                        + "2. 手牌与能量仍然各人各一份（你打你的、我打我的）；\n"
                        + "3. 没有名额限制，几个人都能加入；只有 1 个人加入时不成组，这一局按普通联机打；\n"
                        + "4. 再按一次（或按官方的「取消准备」）即退出合作，回到普通联机准备状态；\n"
                        + "5. 全员准备后由本体照常开局；\n"
                        + "6. 每个人可以选任意角色（甚至不同角色）；共享卡组里放谁的初始卡由上面"
                        + "「开局合并双方初始卡组」决定（开启时会把所有回声的初始卡都并进来）；\n"
                        + "7. 关闭开关时，本 mod 完全不介入任何对局（正常原版局）。\n"
                        + "联机时以主机设置为准。"))));
    }

    /// <summary>设置页文本的统一入口（中英按游戏语言切换，缺失时回落到中文原文）。</summary>
    private static ModSettingsText T(string key, string chinese)
    {
        return TogetherUiText.Settings(key, chinese);
    }

    private static string DescribeCurrent()
    {
        return TogetherSettingsSync.EffectiveSymbiosisEnabled
            ? TogetherUiText.Get(
                "together.settings.enabled.on",
                "当前：开启 —— 选人界面有「加入合作模式」按钮，≥2 人加入后共用一个身体。")
            : TogetherUiText.Get(
                "together.settings.enabled.off",
                "当前：关闭 —— 本 mod 不介入任何对局。");
    }

    /// <summary>文本框的"输入是否合法"提示：只认 0~100 的整数。</summary>
    private static bool IsValidPercent(string? value)
    {
        return int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent)
               && percent is >= 0 and <= 100;
    }

}
