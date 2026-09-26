using System.Globalization;

using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib.Settings;
using STS2RitsuLib.Utils.Persistence;
using STS2RitsuLib;
using Together.Core.Common;
using Together.Core.Diagnostics;
using Together.Core.Foundation;
using Together.Core.Ui;
using Together;

namespace Together.Core.Settings;
/// <summary>
/// 设置界面：合作模式总开关 + 几个共享规则。
/// </summary>
/// <remarks>
/// <para>
/// "谁和谁配对"不在这里选 —— 那是在多人选人界面按「加入合作模式」决定的（见
/// <c>Together.Core.Foundation.CoopLobbyData</c>），这样两个玩家可以选<b>任意角色</b>
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

                // 不用清名单：投票是**每个大厅会话**的暂存数据（RunSavedData Lobby Scope），
                // 进新大厅自然是空的；关掉开关后按钮也跟着消失（见 TogetherCoopGate.Applies）。

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

        // 兼容性开关（B 类"改本体行为"的补丁各一条）：默认开 = 保持既有体验；关掉 = 回到原版行为。
        var compatOrderBinding = new ModSettingsValueBinding<TogetherSettings, bool>(
            Const.ModId,
            TogetherSettingsStore.DataKey,
            SaveScope.Global,
            _ => TogetherSettingsSync.EffectiveCompatDeterministicOrder,
            (settings, value) =>
            {
                settings.CompatDeterministicOrder = value;
                TogetherSettingsSync.PublishHostSettings("settings_changed");
            });

        var compatDedupeBinding = new ModSettingsValueBinding<TogetherSettings, bool>(
            Const.ModId,
            TogetherSettingsStore.DataKey,
            SaveScope.Global,
            _ => TogetherSettingsSync.EffectiveCompatHookDedupe,
            (settings, value) =>
            {
                settings.CompatHookDedupe = value;
                TogetherSettingsSync.PublishHostSettings("settings_changed");
            });

        var compatWidenBinding = new ModSettingsValueBinding<TogetherSettings, bool>(
            Const.ModId,
            TogetherSettingsStore.DataKey,
            SaveScope.Global,
            _ => TogetherSettingsSync.EffectiveCompatHookWiden,
            (settings, value) =>
            {
                settings.CompatHookWiden = value;
                TogetherSettingsSync.PublishHostSettings("settings_changed");
            });

        var compatCardViewBinding = new ModSettingsValueBinding<TogetherSettings, bool>(
            Const.ModId,
            TogetherSettingsStore.DataKey,
            SaveScope.Global,
            _ => TogetherSettingsSync.EffectiveCompatSharedCardView,
            (settings, value) =>
            {
                settings.CompatSharedCardView = value;
                TogetherSettingsSync.PublishHostSettings("settings_changed");
            });

        var compatEchoPopulateBinding = new ModSettingsValueBinding<TogetherSettings, bool>(
            Const.ModId,
            TogetherSettingsStore.DataKey,
            SaveScope.Global,
            _ => TogetherSettingsSync.EffectiveCompatEchoPopulateSkip,
            (settings, value) =>
            {
                settings.CompatEchoPopulateSkip = value;
                TogetherSettingsSync.PublishHostSettings("settings_changed");
            });

        var orbCapBinding = new ModSettingsValueBinding<TogetherSettings, OrbCapMode>(
            Const.ModId,
            TogetherSettingsStore.DataKey,
            SaveScope.Global,
            _ => TogetherSettingsSync.EffectiveOrbCap,
            (settings, value) =>
            {
                settings.OrbCap = value;
                TogetherSettingsSync.PublishHostSettings("settings_changed");
            });

        var compatMirroredPowerBinding = new ModSettingsValueBinding<TogetherSettings, bool>(
            Const.ModId,
            TogetherSettingsStore.DataKey,
            SaveScope.Global,
            _ => TogetherSettingsSync.EffectiveCompatMirroredPowerSingleFire,
            (settings, value) =>
            {
                settings.CompatMirroredPowerSingleFire = value;
                TogetherSettingsSync.PublishHostSettings("settings_changed");
            });

        var compatImbuedBinding = new ModSettingsValueBinding<TogetherSettings, bool>(
            Const.ModId,
            TogetherSettingsStore.DataKey,
            SaveScope.Global,
            _ => TogetherSettingsSync.EffectiveCompatImbuedOnce,
            (settings, value) =>
            {
                settings.CompatImbuedOnce = value;
                TogetherSettingsSync.PublishHostSettings("settings_changed");
            });

        var compatRandomForeseerBinding = new ModSettingsValueBinding<TogetherSettings, bool>(
            Const.ModId,
            TogetherSettingsStore.DataKey,
            SaveScope.Global,
            _ => TogetherSettingsSync.EffectiveCompatRandomForeseerSync,
            (settings, value) =>
            {
                settings.CompatRandomForeseerSync = value;
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

        // 弹窗开关：**本机偏好**（另一个数据文件），不进联机同步 —— 主机不该替客户端决定要不要被打扰。
        var alertPopupBinding = new ModSettingsValueBinding<TogetherUiPrefs, bool>(
            Const.ModId,
            TogetherUiPrefsStore.DataKey,
            SaveScope.Global,
            prefs => prefs.AlertPopup,
            (prefs, value) => prefs.AlertPopup = value);

        RitsuLibFramework.RegisterModSettings(Const.ModId, page => page
            .WithTitle(T("together.settings.page.title", "Together · 合作模式"))
            .WithModDisplayName(ModSettingsText.Literal("Together"))
            // 局内改设置没有意义（配对在选人阶段就定下来了），所以**设置类小节**在局内只读，
            // 避免"改了没生效"的困惑。★ 注意只加在小节上、不加在页面上：
            // 「诊断」那一节必须任何时候都能点 —— 卡死 / 报 bug 时最需要导出 mod 列表。
            // 兼容性开关：这几条改的是"本体自己也会走的路径"，与别的 mod 冲突时可以逐条关掉回退。
            .AddSection("compat", section => section
                .WithTitle(T("together.settings.compat.title", "兼容性（高风险补丁）"))
                .WithReadOnlyOnHostSurfaces(ModSettingsHostSurface.RunPause | ModSettingsHostSurface.CombatPause)
                .AddToggle(
                    "compat_order",
                    T("together.settings.compat.order.label", "牌序全序化（CardModel.CompareTo）"),
                    compatOrderBinding,
                    T(
                        "together.settings.compat.order.description",
                        "开启：让卡牌之间有一个确定的全序，两端排序/洗牌算得一样（共享局的顺序一致性靠它）。\n"
                        + "关闭：用本体原本的比较器 —— 顺序可能两端漂移（只在与其他 mod 冲突时关）。"))
                .AddToggle(
                    "compat_dedupe",
                    T("together.settings.compat.dedupe.label", "钩子监听表去重"),
                    compatDedupeBinding,
                    T(
                        "together.settings.compat.dedupe.description",
                        "开启：共享牌堆/共享主卡组让同一张牌被收集两次时按引用去重。\n"
                        + "关闭：保留本体的重复监听者（可能让注能类能力重复触发）。"))
                .AddToggle(
                    "compat_widen",
                    T("together.settings.compat.widen.label", "遗物钩子「组内放宽」"),
                    compatWidenBinding,
                    T(
                        "together.settings.compat.widen.description",
                        "开启：共享局里「牌进卡组」的通知按组内每个成员各派发一次 —— 五轮书这类按「牌属于谁」认领的遗物会按全组记账。\n"
                        + "关闭：回到本体行为（只通知牌的所有者那一位），两端仍然一致，只是回声侧那本遗物不再涨。"))
                .AddToggle(
                    "compat_cardview",
                    T("together.settings.compat.cardview.label", "回声共享卡牌视图置空"),
                    compatCardViewBinding,
                    T(
                        "together.settings.compat.cardview.description",
                        "开启：怪物招式期间，回声那一侧的「共享卡牌视图」返回空 —— 同一批共享牌只被处理一次。\n"
                        + "关闭：恢复全员可见（可能让「遍历共享牌」的怪招重复处理、数值翻倍）。"))
                .AddToggle(
                    "compat_echo_populate",
                    T("together.settings.compat.echopopulate.label", "回声不重复填充战斗牌堆"),
                    compatEchoPopulateBinding,
                    T(
                        "together.settings.compat.echopopulate.description",
                        "开启：进战斗时只让锚点把主卡组复制进抽牌堆 —— 主卡组只有一份，回声再填一次就是双倍卡组。\n"
                        + "关闭：回声也照常填充（共享卡组会变成两份）。只在「另一个 mod 自己接管了回声的战斗牌堆」时才关。"))
                .AddToggle(
                    "compat_mirrored_power",
                    T("together.settings.compat.mirroredpower.label", "镜像能力的回合末结算只算一次"),
                    compatMirroredPowerBinding,
                    T(
                        "together.settings.compat.mirroredpower.description",
                        "开启：临时力量/敏捷/集中、虚弱/易伤/脆弱这几个「会改身体数值」的回合末能力只由原件结算一次（镜像副本不重复扣）。\n"
                        + "关闭：每份镜像各扣一次（数值会翻倍甚至变负）。"))
                .AddToggle(
                    "compat_imbued",
                    T("together.settings.compat.imbued.label", "注能每场战斗只自动打出一次"),
                    compatImbuedBinding,
                    T(
                        "together.settings.compat.imbued.description",
                        "开启：同一张注能牌在本场战斗里只自动打出一次（按对象引用去重）。\n"
                        + "关闭：本体照常派发（配合上面那条「只跑锚点那次」一般也不会重复）。"))
                .AddToggle(
                    "compat_random_foreseer",
                    T("together.settings.compat.randomforeseer.label", "随机数预测联动（RandomForeseer）"),
                    compatRandomForeseerBinding,
                    T(
                        "together.settings.compat.randomforeseer.description",
                        "开启：装了「随机数预测」时，让它每次预测只建一份共享牌堆 / 球队列副本 —— 修掉「抽牌预测连续都是第一张牌」和「充能球伤害预测不准」。\n"
                        + "关闭：完全不碰对方的预测内核（预测退回它原本的算法）。\n"
                        + "没装那个 mod 时这项没有任何效果。"))
                .AddParagraph(
                    "compat_note",
                    T(
                        "together.settings.compat.note",
                        "这一组默认全开。它们改的是本体自己也会走的路径，所以只在「与某个 mod 冲突」时逐条关掉回退。\n"
                        + "联机时以主机设置为准（否则一端关一端开会直接分叉）。")))
            .AddSection("symbiosis", section => section
                .WithTitle(T("together.settings.section.title", "合作模式"))
                .WithReadOnlyOnHostSurfaces(ModSettingsHostSurface.RunPause | ModSettingsHostSurface.CombatPause)
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
                .AddEnumChoice(
                    "orb_cap_mode",
                    T("together.settings.orbcap.label", "共享球位上限"),
                    orbCapBinding,
                    value => value switch
                    {
                        OrbCapMode.Vanilla => T("together.settings.orbcap.option.vanilla", "原版 10 格"),
                        OrbCapMode.PerMember => T("together.settings.orbcap.option.per_member", "10 × 全部人数"),
                        _ => T("together.settings.orbcap.option.auto", "自动（10 × 有球位人数）"),
                    },
                    T(
                        "together.settings.orbcap.description",
                        "几个人的充能球位是同一口队列，上限按这个口径放大。\n"
                        + "自动：10 × 本局「有球位」的成员数（两个故障机器人 = 20；只有一个人有球位就还是 10）——默认。\n"
                        + "原版 10 格：完全照原版，共享时球位容易不够放。\n"
                        + "10 × 全部人数：不看角色，按组内人数算（3 人局就算只有 1 个机器人也给 30 格）。\n"
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
                        + "联机时以主机设置为准。"))
            )
            // 诊断：唯一一节"任何界面都能点"的东西 —— 出问题时玩家就是靠它把环境交出来的。
            .AddSection("diagnostics", section => section
                .WithTitle(T("together.settings.diag.title", "诊断 / 反馈"))
                .AddToggle(
                    "alert_popup",
                    T("together.settings.diag.alertpopup.label", "自检到异常时弹窗提醒（本机设置）"),
                    alertPopupBinding,
                    T(
                        "together.settings.diag.alertpopup.description",
                        "开启（默认）：检测到「两端不同步 / 我们自己的断言失败 / 补丁没装全」时弹一个窗，"
                        + "告诉你该发哪两个文件。\n"
                        + "关闭：不弹窗 —— 但环境快照**照旧自动导出**、log 里**照旧**有"
                        + "「⚠ 自检异常（…）｜反馈包：…」那一行，只是不打扰你。\n"
                        + "这是**每台机器自己**的设置（不跟主机同步）：主机关掉不会让客户端也不弹，反之亦然。"))
                .AddButton(
                    "export_modlist",
                    T("together.settings.diag.export.label", "导出当前环境（mod 列表 / 设置 / 会话状态）"),
                    T("together.settings.diag.export.button", "一键导出"),
                    () => ModListExport.ExportBundle("settings_button", openFolder: true),
                    ModSettingsButtonTone.Accent,
                    T(
                        "together.settings.diag.export.description",
                        "把「当前启用的 mod 列表 + 本机生效设置 + 本局会话状态」写成一个 txt，"
                        + "放进游戏的日志目录（和 godot.log 同一个文件夹）：together-modlist.txt。\n"
                        + "点完会自动打开那个文件夹。反馈问题时把它和 godot.log 一起发过来就够了。\n"
                        + "这条按钮在战斗中暂停界面也能点（其他设置项在局内是只读的）。"))
                .AddParagraph(
                    "diag_note",
                    T(
                        "together.settings.diag.note",
                        "导出内容全部来自公开接口（本体的 mod 加载顺序 + RitsuLib 的 mod 清单），"
                        + "同时会以 [together][env] 前缀写进 log —— 只会复制 log 的话，现场也一样在里面。"))
                // 测试按钮：走的是**完全真实**的那条路（自动导出 → 弹窗 → 「打开文件夹」），
                // 只是跳过限流，方便连点确认。真实 bug 触发的那三次额度不会被它占掉。
                .AddButton(
                    "test_alert",
                    T("together.settings.diag.test.label", "测试异常弹窗（确认「自动导出 + 弹窗」这条链路）"),
                    T("together.settings.diag.test.button", "测试一下"),
                    TestAlert,
                    ModSettingsButtonTone.Normal,
                    T(
                        "together.settings.diag.test.description",
                        "点一下会：① 立刻导出一份环境快照；② 弹一个和真出 bug 时一模一样的窗；"
                        + "③ 窗里的「打开文件夹」能直接定位到那个文件。\n"
                        + "如果此刻已经有别的弹窗（比如分歧诊断面板）占着，就只导出、不弹窗 —— 和真实情况的行为一致。"))));
    }

    /// <summary>设置页的「测试弹窗」：走真实的 <see cref="TogetherAlert" /> 链路（跳过限流，不占真实额度）。</summary>
    private static void TestAlert()
    {
        TogetherAlert.Notify(
            "测试",
            "这是手动触发的测试弹窗：用来确认「自动导出环境快照 + 弹窗 + 打开文件夹」这条链路是通的。",
            ignoreRateLimit: true);
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
