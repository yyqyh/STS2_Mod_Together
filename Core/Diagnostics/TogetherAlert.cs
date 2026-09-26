using Godot;

using HarmonyLib;

using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Runs;

using Together.Core.Settings;

namespace Together.Core.Diagnostics;

/// <summary>
/// 「自检发现自己的 bug」时的统一出口：<b>先自动导出环境快照</b>，再<b>尝试</b>弹一个本体的原生弹窗告诉玩家发什么。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要有它</b>：玩家反馈里最贵的是"现场"。以前就算我们（或者本体）报了不同步，
/// 玩家也不知道要交什么，最后只剩一句"卡死了，没 log"。
/// 这里把两件事绑在一起：异常一出现就把 <c>together-modlist.txt</c> 落到日志目录（不依赖玩家点任何东西），
/// 然后弹窗告诉他"把这个文件 + <c>godot.log</c> 发作者"。
/// </para>
/// <para>
/// <b>和 RitsuLib 的分歧报告窗怎么共存（①先导出 ②有别的模态框就不弹）</b>：
/// 真出 <c>State divergence</c> 时 RitsuLib 自己会弹一个诊断面板（它占着 <c>NModalContainer</c>）。
/// 我们**永远先导出**（文件该有就有），弹窗只在"当前没有别的模态框"时才真的弹 ——
/// 这样绝不会两个窗叠在一起，也不会因为弹窗失败而丢掉证据。
/// </para>
/// <para>
/// <b>弹窗用的是本体自己的 <see cref="NErrorPopup" /> + <see cref="NModalContainer" /></b>（和本体报错同一个样式、
/// 同一套模态管理）；"打开文件夹"按钮借的是本体的"否"按钮位（隐藏状态，和 RitsuLib 的做法一致），
/// 所以不需要自己造控件、也不会和别的人抢位置。
/// </para>
/// <para>
/// <b>限流</b>：一次会话最多 <see cref="MaxPerSession" /> 次、两次至少隔 <see cref="MinIntervalMs" /> 毫秒 ——
/// 分歧往往连着一串，不限制会把玩家埋掉。
/// </para>
/// </remarks>
internal static class TogetherAlert
{
    private const int MaxPerSession = 3;

    private const long MinIntervalMs = 30_000;

    private static int _count;

    private static long _lastTicks = long.MinValue;

    /// <summary>报一个"我们自己的 bug"：自动导出 + 尝试弹窗。</summary>
    /// <param name="kind">短标签（进日志和弹窗标题）。</param>
    /// <param name="detail">具体信息（尽量带 id / 数值 / 异常文本）。</param>
    /// <param name="ignoreRateLimit">
    /// 是否跳过限流。给设置页的「测试弹窗」按钮用：测试不该占掉"真出 bug 时的 3 次额度"，
    /// 而且玩家要能连点几次看效果。真实触发点一律传 <c>false</c>。
    /// </param>
    public static void Notify(string kind, string detail, bool ignoreRateLimit = false)
    {
        try
        {
            var now = System.Environment.TickCount64;
            if (!ignoreRateLimit)
            {
                if (_count >= MaxPerSession)
                {
                    return;
                }

                if (_lastTicks != long.MinValue && now - _lastTicks < MinIntervalMs)
                {
                    return;
                }

                _count++;
                _lastTicks = now;
            }

            // ① 先落盘 + 打包：这一步不依赖玩家点任何东西，也不依赖界面能不能弹出来，
            //    更不受下面那个"弹窗开关"影响 —— 玩家就算把弹窗关了，证据也一样会自动存好。
            var bundle = ModListExport.ExportBundle($"alert:{kind}");

            Log.Warn(
                $"[together] ⚠ 自检异常（{kind}）：{detail}"
                + $"｜反馈包：{bundle ?? "生成失败（见上面那条警告）"}");

            // ② 弹窗开关（本机偏好，见 TogetherUiPrefs）：关掉就只导出、只写 log。
            if (!TogetherUiPrefsStore.AlertPopup)
            {
                Log.Info("[together] 异常弹窗已按设置关闭（只导出反馈包 + 写 log）");
                return;
            }

            var body = BuildBody(kind, detail, bundle);

            // ③ 再弹窗：没有模态框才弹（RitsuLib 的分歧面板在的时候就让位，避免两个窗叠一起）。
            Callable.From(() => ShowDeferred(kind, body, bundle)).CallDeferred();
        }
        catch (Exception ex)
        {
            Log.Warn($"[together] 自检异常上报失败（忽略）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>两端校验和不一致（本体 <c>StateDiverged</c>）。</summary>
    public static void OnStateDiverged(ulong remoteId, NetFullCombatState state)
    {
        Notify("不同步", $"校验和不一致：对端 netId={remoteId}（本机 netId={MegaCrit.Sts2.Core.Context.LocalContext.NetId}）");
    }

    private static string BuildBody(string kind, string detail, string? path)
    {
        var where = path ?? "（打包失败：log 里有一条警告，请直接把整个 logs 文件夹发我）";

        return
            $"[b]Together 自检到异常[/b]：{kind}\n"
            + $"{detail}\n\n"
            + "[b]出问题时要用的文件（已经帮你打包好了，点下面「打开文件夹」就能看到）[/b]\n"
            + $"　{where}\n"
            + "　· godot.log —— 游戏自己的运行日志，卡死 / 报错的现场就在最后几行\n"
            + "　· together-modlist.txt —— 启用的 mod 列表 + 本机生效设置 + 本局会话状态\n"
            + "　· ritsulib_state_divergence_*.zip —— 分歧诊断包（只有出「不同步」时才会有）\n\n"
            + "想反馈就把上面这个文件夹（或里面的两个文件）发到"
            + " mod 的创意工坊页面评论区 / 你所在的 mod 群，再补一句「当时在做什么」"
            + "（哪个事件 / 哪张牌 / 谁先按的）就够了；不想反馈也完全没关系，文件留着即可。\n\n"
            + "[b]Together detected a problem[/b]: " + kind + "\n"
            + detail + "\n"
            + "godot.log + together-modlist.txt (both in the folder above) are everything a bug report needs. "
            + "Whether to report, and where, is entirely up to you.";
    }

    private static void ShowDeferred(string kind, string body, string? path)
    {
        try
        {
            var container = NModalContainer.Instance;
            if (container is null)
            {
                Log.Info("[together] 异常弹窗跳过：还没有 NModalContainer（启动早期 / 单机菜单）");
                return;
            }

            if (container.OpenModal is not null)
            {
                Log.Info("[together] 异常弹窗跳过：当前已有模态框（多半是 RitsuLib 的分歧面板）—— 环境快照已导出，见上一条日志");
                return;
            }

            var popup = NErrorPopup.Create($"Together：检测到异常（{kind}）", body, showReportBugButton: false);
            if (popup is null)
            {
                return;
            }

            container.Add(popup);
            Log.Info($"[together] 异常弹窗已弹出（{kind}）");

            AttachButtons(popup, path);
        }
        catch (Exception ex)
        {
            Log.Warn($"[together] 异常弹窗失败（忽略）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 把本体的"否"按钮（默认隐藏）改成「打开文件夹」。
    /// </summary>
    /// <remarks>
    /// 只改本体的"是"按钮（它自己还会关窗）：把文案换成「打开文件夹」并在按下时打开反馈包。
    /// 本体的"否"按钮<b>保持隐藏</b> —— 我们<b>不</b>给"一键去某个反馈页"的入口：
    /// <b>要不要反馈、发到哪里，由玩家自己决定</b>（弹窗只负责把现场文件准备好、说清它们是什么）。
    /// 顺带也就不会和 RitsuLib 抢 <c>NErrorPopup</c> 的第二个按钮位。
    /// </remarks>
    private static void AttachButtons(NErrorPopup popup, string? path)
    {
        var vertical = popup.GetNodeOrNull<NVerticalPopup>("VerticalPopup");
        if (vertical is null)
        {
            return;
        }

        Callable.From(() =>
        {
            try
            {
                if (!GodotObject.IsInstanceValid(vertical))
                {
                    return;
                }

                // 「是」按钮：本体原样会关窗；我们顺手让它再把反馈包文件夹打开。
                if (path is not null)
                {
                    var yes = vertical.YesButton;
                    if (yes is not null)
                    {
                        yes.SetText("打开文件夹 / Open folder");
                        yes.Connect(
                            NClickableControl.SignalName.Released,
                            Callable.From<NClickableControl>(_ => OpenInFileManager(path)));
                    }
                }

                // ★ 不再提供"一键去某个反馈页"的按钮：要不要反馈、发到哪里交给玩家自己决定
                //   （所以"否"按钮保持隐藏 —— 本体的默认行为）。
            }
            catch (Exception ex)
            {
                Log.Warn($"[together] 异常弹窗的按钮挂载失败（忽略）：{ex.GetType().Name}: {ex.Message}");
            }
        }).CallDeferred();
    }

    private static void OpenInFileManager(string path)
    {
        try
        {
            var error = OS.ShellShowInFileManager(path);
            if (error != Error.Ok)
            {
                Log.Warn($"[together] 打开文件夹失败（{error}）：{path}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[together] 打开文件夹异常：{ex.GetType().Name}: {ex.Message}");
        }
    }

}

/// <summary>订阅本体的"状态分歧"事件（<c>ChecksumTracker.StateDiverged</c>，公开事件，不用打私有方法）。</summary>
/// <remarks>
/// 挂构造函数 Postfix 是为了拿到实例 —— <c>ChecksumTracker</c> 由 <c>RunManager</c> 持有，
/// 但那条属性的名字在本体的反编译里是改过的，直接订阅事件最稳。
/// 两端都会触发（主机在收到回执、客户端在收到消息时各一次），所以两台机器都会导出 + 弹窗。
/// </remarks>
[HarmonyPatch(
    typeof(ChecksumTracker),
    MethodType.Constructor,
    new[] { typeof(INetGameService), typeof(IRunState) })]
internal static class ChecksumDivergenceAlertPatch
{
    [HarmonyPostfix]
    private static void Postfix(ChecksumTracker __instance)
    {
        try
        {
            __instance.StateDiverged += TogetherAlert.OnStateDiverged;
            Log.Info("[together] 已订阅校验和分歧事件（StateDiverged）：出事会自动导出环境快照并提示玩家发送");
        }
        catch (Exception ex)
        {
            Log.Warn($"[together] 订阅校验和分歧事件失败：{ex.GetType().Name}: {ex.Message}");
        }
    }
}
