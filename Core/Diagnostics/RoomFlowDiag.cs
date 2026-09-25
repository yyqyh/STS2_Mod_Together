using System.Reflection;

using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using Together.Core.Common;
using Together.Core.Foundation;
using Together.Core.Ui;

namespace Together.Core.Diagnostics;
/// <summary>房间流程的"里程碑"日志（默认开着，每个点最多 30 条）。</summary>
/// <remarks>
/// 黑屏这种"没有异常、只是卡住"的问题，log 里什么都没有的话没法定位。
/// 关键节点打出来，卡住时就能看出**最后一个成功打印的里程碑**：<c>RunManager.EnterMapCoord</c>（点了地图节点）、
/// <c>EnterMapPointInternal</c>（开始进房间）、<c>NTransition.RoomFadeOut</c> / <c>RoomFadeIn</c>（转场淡出/淡入，
/// <b>只有 FadeOut 没有 FadeIn = 卡在转场里、画面就是黑的</b>）、<c>NRewardsScreen.RewardCollectedFrom</c>
/// （取走奖励）、<c>RunManager.ExitCurrentRoom</c>（退出房间）、<c>RewardsSetSynchronizer.CompleteRewardsSet</c>
/// （后端标记奖励集完成 —— 按钮都点完了但后端**没标记完成**时屏幕会一直等一个永远不来的信号，本体只打一句
/// <c>All rewards have been taken, but the rewards set is not complete on the backend!</c>）。
/// 都是<b>只看不改</b>的钩子；排查完可整体删掉，或把 <see cref="CappedLog" /> 的 Limit 调小。
/// </remarks>
[HarmonyPatch]
internal static class RoomFlowDiagPatches
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(NTransition), nameof(NTransition.RoomFadeOut));
        yield return AccessTools.Method(typeof(NTransition), nameof(NTransition.RoomFadeIn));
        yield return AccessTools.Method(typeof(RunManager), nameof(RunManager.EnterMapCoord));
        yield return AccessTools.Method(typeof(RunManager), nameof(RunManager.EnterMapPointInternal));
        yield return AccessTools.Method(typeof(RunManager), nameof(RunManager.Launch));
        yield return AccessTools.Method(typeof(RunManager), "ExitCurrentRoom");
        yield return AccessTools.Method(typeof(NRewardsScreen), nameof(NRewardsScreen.RewardCollectedFrom));
        yield return AccessTools.Method(typeof(RewardsSetSynchronizer), "CompleteRewardsSet");
    }

    [HarmonyPrefix]
    private static void Prefix(MethodBase __originalMethod, object[] __args)
    {
        switch (__originalMethod.Name)
        {
            case nameof(NTransition.RoomFadeOut):
                CappedLog.Info("flow.fade_out", "转场：房间淡出开始（画面开始变黑）");
                break;

            case nameof(NTransition.RoomFadeIn):
                CappedLog.Info("flow.fade_in", $"转场：房间淡入开始（showTransition={__args[0]}）—— 看到这行说明黑屏已经结束");
                break;

            case nameof(RunManager.EnterMapCoord):
                CappedLog.Info("flow.enter_coord", $"地图：点击节点 {__args[0]}");
                break;

            case nameof(RunManager.EnterMapPointInternal):
                CappedLog.Info("flow.enter_point", $"房间：开始建造 actFloor={__args[0]} pointType={__args[1]}");
                HangWatchdog.EnsureRunning();
                break;

            case "ExitCurrentRoom":
                CappedLog.Info("flow.exit_room", "房间：开始退出当前房间");
                break;

            case "CompleteRewardsSet":
                CappedLog.Info("flow.rewardset_done", $"后端：奖励集完成 set={__args[0]} state={__args[1]}");
                break;
        }
    }

    [HarmonyPostfix]
    private static void Postfix(MethodBase __originalMethod)
    {
        switch (__originalMethod.Name)
        {
            // 读档 / 重连 / ESC 重启房间都走这条路径，而它**不经过地图节点** —— 黑屏恰恰多数发生在这里。
            case nameof(RunManager.Launch):
                HangWatchdog.EnsureRunning();
                break;

            case nameof(NRewardsScreen.RewardCollectedFrom):
                CappedLog.Info("flow.reward_taken", "奖励屏：取走了一项奖励");
                break;
        }
    }
}

/// <summary>黑屏探针 + 自愈：每秒看一眼转场遮罩，卡在黑色上超过几秒就强制淡回画面。</summary>
/// <remarks>
/// 黑屏有两种，处置方式完全不同：<b>主循环死了</b>（日志停在某个里程碑不动）→ 死锁，看最后一个里程碑；
/// <b>主循环活着但画面全黑</b> → 转场遮罩（<c>NTransition</c>）停在了"全黑"那一帧上。
/// 本体只有 <c>RoomFadeIn</c> 会把它淡回透明，而 <c>ExitCurrentRoom</c> 一旦中途出错、
/// 或者读档进入"战斗已打完、奖励未领"的房间，那次淡入就永远不会被调用。
/// 这里每秒检查一次遮罩透明度。判定为"黑着"之后：
/// 超过 <see cref="BlackScreenToleranceMs" /> 先跑一次本体的淡入（保底能把画面救回来），
/// 再不行就直接把遮罩透明度硬置 0（<see cref="HardReset" />）。
/// 日志只在"刚开始黑 / 强制救援 / 画面恢复"这几个节点打印，不刷屏。
/// <b>看门狗的 Timer 挂在场景根节点上</b>，而读档、ESC 重启房间都会让 UI 树重建。
/// 早期版本用一个 <c>static bool _running</c> 记"已经挂过了"，一旦那次重建把 Timer 带走，
/// 它就<b>静默失效</b>——表现正是"大部分时候有效果、某一次之后就再也没反应了"。
/// 现在每次都校验节点还在不在树上，不在就重挂。
/// </remarks>
internal static class HangWatchdog
{
    /// <summary>黑幕挂多久算"卡住"（正常转场最多 1 秒出头）。</summary>
    private const long BlackScreenToleranceMs = 4500;

    /// <summary>救援之后给它多少毫秒自己淡出去，还不走就硬复位。</summary>
    private const long HardResetGraceMs = 1200;

    /// <summary>两次救援之间的最短间隔（救援无效时避免刷屏）。</summary>
    private const long RescueCooldownMs = 3000;

    /// <summary>遮罩透明度超过这个值就算"画面被盖住"。</summary>
    private const float BlackAlphaThreshold = 0.8f;

    /// <summary><c>NTransition</c> 里那两层黑幕的私有字段名。</summary>
    private static readonly string[] MaskFields = ["_simpleTransition", "_gradientTransition"];

    private static Godot.Timer? _timer;

    private static bool _dark;
    private static bool _rescued;
    private static bool _scanned;
    private static long _darkSinceMs;
    private static long _rescuedAtMs;
    private static long _lastRescueMs;

    public static void EnsureRunning()
    {
        if (Engine.GetMainLoop() is not SceneTree tree || tree.Root is not { } root)
        {
            return;
        }

        // 节点还活着、还在树上才算数：读档 / 重启房间会把 UI 树重建，旧的 Timer 会被一起带走。
        if (_timer is not null && GodotObject.IsInstanceValid(_timer) && _timer.IsInsideTree())
        {
            return;
        }

        var rebuilt = _timer is not null;

        _timer = new Godot.Timer
        {
            Name = "TogetherHangWatchdog",
            WaitTime = 1.0,
            Autostart = true,
            OneShot = false,
        };

        root.AddChild(_timer);
        _timer.Timeout += Tick;

        ResetState();

        Log.Info(rebuilt
            ? "[together] 黑屏看门狗被场景重建带走了，已重新挂上（每秒检查一次转场遮罩）"
            : "[together] 黑屏看门狗已启动（每秒检查一次转场遮罩）");
    }

    private static void ResetState()
    {
        _dark = false;
        _rescued = false;
        _scanned = false;
        _darkSinceMs = 0;
        _rescuedAtMs = 0;
        _lastRescueMs = 0;
    }

    private static void Tick()
    {
        var transition = NGame.Instance?.Transition;
        var readable = TryReadMask(transition, out var alpha);
        var inTransition = transition is not null
            && GodotObject.IsInstanceValid(transition)
            && transition.InTransition;

        // 遮罩读不到时（比如本体换了节点名）退化成老逻辑：只看 InTransition。
        var dark = readable ? inTransition || alpha >= BlackAlphaThreshold : inTransition;
        var now = System.Environment.TickCount64;

        if (!dark)
        {
            if (_dark)
            {
                Log.Info(
                    $"[together] 画面已恢复正常（转场遮罩 alpha={(readable ? alpha : -1f):F2}，"
                    + $"转场中={inTransition}）");
            }

            ResetState();
            return;
        }

        if (!_dark)
        {
            _dark = true;
            _rescued = false;
            _scanned = false;
            _darkSinceMs = now;

            Log.Info(
                $"[together] 检测到画面变黑（遮罩 alpha={(readable ? alpha : -1f):F2}，转场中={inTransition}），"
                + $"开始计时：{(BlackScreenToleranceMs / 1000.0):F1} 秒内没自己淡出去就强制恢复。");
            return;
        }

        // 救援已经发出去了，还黑着 → 直接硬复位（本体那条淡入链路本身没走通）。
        if (_rescued && now - _rescuedAtMs >= HardResetGraceMs)
        {
            _rescued = false;
            HardReset(transition, alpha);
            return;
        }

        if (now - _darkSinceMs < BlackScreenToleranceMs || now - _lastRescueMs < RescueCooldownMs)
        {
            return;
        }

        // 第一次超时就顺便把"到底是谁挡着画面"扫出来：如果扫出来的不是 NTransition，
        // 说明黑幕来自别处（别的 mod 的遮罩 / 3D 后处理），光靠本体的淡入救不回来。
        if (!_scanned)
        {
            _scanned = true;
            Log.Warn($"[together] 黑幕超过 {(BlackScreenToleranceMs / 1000.0):F1} 秒没消失，挡在最前面的全屏节点：{DescribeOpaqueLayers()}");
        }

        _rescued = true;
        _rescuedAtMs = now;
        _lastRescueMs = now;

        Log.Warn(
            "[together] 检测到转场黑幕挂了超过 "
            + $"{(BlackScreenToleranceMs / 1000.0):F1} 秒（画面全黑、但游戏在正常运行），强制淡回房间画面。"
            + "若是读档进入『战斗已打完、奖励未领』的房间，这属于本体/多控读档链路的已知问题，"
            + "建议读档前先把奖励领掉。");

        if (transition is not null && GodotObject.IsInstanceValid(transition))
        {
            TaskHelper.RunSafely(transition.RoomFadeIn(showTransition: true));
        }
    }

    /// <summary>读 <c>NTransition</c> 里两层遮罩的透明度（取较大的那个）。</summary>
    /// <remarks>
    /// <c>_gradientTransition</c> 平时只是"停在屏幕外的不透明渐变条"，光看 alpha 会误判，
    /// 所以它额外要求位置已经扫进屏幕（<c>Position.Y</c> 接近 0）才算挡着画面。
    /// </remarks>
    private static bool TryReadMask(NTransition? transition, out float alpha)
    {
        alpha = 0f;

        if (transition is null || !GodotObject.IsInstanceValid(transition))
        {
            return false;
        }

        var found = false;
        var screenHeight = transition.GetViewportRect().Size.Y;

        foreach (var name in MaskFields)
        {
            var control = ReadMaskControl(transition, name);
            if (control is null || !GodotObject.IsInstanceValid(control) || !control.Visible)
            {
                continue;
            }

            found = true;

            // 渐变层在屏幕外时不算（它平时就是这个状态）。
            if (name == "_gradientTransition" && screenHeight > 0f && control.Position.Y > screenHeight * 0.25f)
            {
                continue;
            }

            alpha = Math.Max(alpha, control.Modulate.A);
        }

        return found;
    }

    private static Control? ReadMaskControl(NTransition transition, string fieldName)
    {
        return ModelAccess.FieldOf(typeof(NTransition), fieldName)?.GetValue(transition) as Control;
    }

    /// <summary>最后的兜底：不管本体的淡入链路为什么失败，直接把遮罩透明度按回 0、解除鼠标拦截。</summary>
    /// <remarks>
    /// 覆盖的是这种情况：<c>RoomFadeIn</c> 因为材质不是 ShaderMaterial 之类的分支提前 return，
    /// 结果 <c>InTransition</c> 已经是 false、但 <c>_simpleTransition</c> 的 alpha 还停在 1（全黑）。
    /// 这种状态老版本是"永远救不回来"的，因为老版本只认 <c>InTransition</c>。
    /// </remarks>
    private static void HardReset(NTransition? transition, float alpha)
    {
        if (transition is null || !GodotObject.IsInstanceValid(transition))
        {
            return;
        }

        foreach (var name in MaskFields)
        {
            var control = ReadMaskControl(transition, name);
            if (control is null || !GodotObject.IsInstanceValid(control))
            {
                continue;
            }

            var modulate = control.Modulate;
            modulate.A = 0f;
            control.Modulate = modulate;
        }

        if (transition.Material is ShaderMaterial material)
        {
            material.SetShaderParameter("threshold", 0f);
        }

        transition.MouseFilter = Control.MouseFilterEnum.Ignore;
        ForceInTransitionFalse(transition);

        Log.Warn($"[together] 黑幕兜底复位：直接把转场遮罩透明度置 0（复位前 alpha={alpha:F2}）。");
    }

    private static void ForceInTransitionFalse(NTransition transition)
    {
        var field = AccessTools.Field(typeof(NTransition), "<InTransition>k__BackingField");
        if (field is null)
        {
            return;
        }

        try
        {
            field.SetValue(transition, false);
        }
        catch (Exception ex)
        {
            SelfCheck.Write($"[together][diag] 复位 InTransition 失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 扫一遍场景树，把"盖住整个视口的不透明控件"列出来（黑屏时用来确定是谁在挡着）。
    /// </summary>
    private static string DescribeOpaqueLayers()
    {
        if (Engine.GetMainLoop() is not SceneTree tree || tree.Root is not { } root)
        {
            return "-";
        }

        var found = new List<string>();
        CollectOpaqueLayers(root, found, 0);
        return found.Count == 0
            ? "（没找到全屏不透明控件，黑可能来自 3D / 后处理或窗口层）"
            : string.Join(" | ", found);
    }

    private static void CollectOpaqueLayers(Node node, List<string> found, int depth)
    {
        if (depth > 12 || found.Count >= 6)
        {
            return;
        }

        if (node is Control control && control.Visible && control.Modulate.A >= 0.9f)
        {
            var viewport = control.GetViewportRect().Size;
            var size = control.Size;
            var coversScreen = viewport.X > 0f
                && viewport.Y > 0f
                && size.X >= viewport.X * 0.9f
                && size.Y >= viewport.Y * 0.9f;
            var opaque = control is not ColorRect rect || rect.Color.A * control.Modulate.A >= 0.9f;

            if (coversScreen && opaque)
            {
                found.Add($"{control.GetPath()}（{control.GetType().Name}）");
            }
        }

        foreach (var child in node.GetChildren())
        {
            CollectOpaqueLayers(child, found, depth + 1);
        }
    }
}
