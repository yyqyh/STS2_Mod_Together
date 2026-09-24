using System.Reflection;

using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using Together.Core.Combat;
using Together.Core.Content;
using Together.Core.Utils;

namespace Together.Core.Patches;

/// <summary>
/// 选人界面右下角的「合作模式」按钮：按一下 = 加入合作 + 把本机切到"已准备"，
/// 再按一下 = 退出合作 + 取消准备。补丁接在文件末尾。
/// </summary>
/// <remarks>
/// <b>不碰官方确认键。</b>本体自己那套"按下确认 → 收起选人界面 → 等其他人 → 全员准备后开局"的流程是完整的，
/// 我们只要在按自己按钮时借它的入口走一遍即可（<c>OnEmbarkPressed</c> / <c>OnUnreadyPressed</c> 都是 private，用反射调）。
/// 旧版本会跟着 0.2 秒的轮询反复 <c>SetEnabled</c> 官方确认键，两端的准备状态互相打架 ——
/// 典型现象是"p1、p2 都确认了合作，官方确认键反而消失，主机得取消一次再确认才回来"。
/// 那套起程门控已经<b>整块删掉</b>：开局时该不该成组只看"按下合作按钮的名单"（≥2 人即成组）。
/// </remarks>
internal static class CharacterSelectGateImpl
{
    /// <summary>还没加入时的按钮文字（中文原文当回退，英文在 localization/mod_settings/eng.json）。</summary>
    private static string TextJoin => TogetherUiText.Get("together.button.join", "加入合作模式");

    /// <summary>已经加入时的按钮文字（再按一下即退出）。</summary>
    private static string TextLeave => TogetherUiText.Get("together.button.leave", "退出合作模式");

    /// <summary>当前打开的角色选择界面（屏幕自己在 OnSubmenuOpened 时登记）。</summary>
    internal static NCharacterSelectScreen? CachedScreen;

    private static Button? _button;

    private static Godot.Timer? _timer;

    /// <summary>本体的"按下确认"（private，签名 <c>void (NButton _)</c>）。</summary>
    private static readonly MethodInfo? EmbarkPressed =
        AccessTools.Method(typeof(NCharacterSelectScreen), "OnEmbarkPressed");

    /// <summary>本体的"取消准备"（private，签名 <c>void (NButton _)</c>）。</summary>
    private static readonly MethodInfo? UnreadyPressed =
        AccessTools.Method(typeof(NCharacterSelectScreen), "OnUnreadyPressed");

    /// <summary>打开选人界面：装按钮 + 对齐一次状态。</summary>
    internal static void OnOpened(NCharacterSelectScreen screen)
    {
        CachedScreen = screen;
        Install(screen);
        Refresh(screen);
    }

    /// <summary>关闭选人界面：把自己加的两个节点收掉。</summary>
    internal static void OnClosed(NCharacterSelectScreen screen)
    {
        if (ReferenceEquals(CachedScreen, screen))
        {
            CachedScreen = null;
        }

        FreeIfChildOf(_button, screen);
        FreeIfChildOf(_timer, screen);
        _button = null;
        _timer = null;
    }

    /// <summary>把「合作模式」按钮挂到选人界面上（关闭开关 / 单人局时只是隐藏）。</summary>
    private static void Install(NCharacterSelectScreen screen)
    {
        if (_button is not null && GodotObject.IsInstanceValid(_button) && _button.GetParent() == screen)
        {
            return;
        }

        _button = null;
        _timer = null;

        var button = new Button
        {
            Name = "TogetherCoopButton",
            Text = TextJoin,
            CustomMinimumSize = new Vector2(340, 52),
            ZIndex = 100,
        };
        button.AddThemeFontSizeOverride("font_size", 18);
        screen.AddChild(button);

        // 钉在右下角（相对锚点的偏移，和屏幕尺寸无关）。
        button.SetAnchorsPreset(Control.LayoutPreset.BottomRight, keepOffsets: true);
        button.OffsetLeft = -380;
        button.OffsetRight = -40;
        button.OffsetTop = -150;
        button.OffsetBottom = -98;
        button.Pressed += () => OnPressed(screen);

        // 别人加入 / 退出之后本地的按钮文字要跟上：用一个 0.2 秒的 Timer 对齐，
        // 不直接在网络回调里碰 UI 节点（那个回调不保证在主线程）。
        var timer = new Godot.Timer
        {
            Name = "TogetherCoopRefresh",
            WaitTime = 0.2,
            Autostart = true,
            OneShot = false,
        };
        screen.AddChild(timer);
        timer.Timeout += () =>
        {
            if (GodotObject.IsInstanceValid(screen))
            {
                Refresh(screen);
            }
        };

        _button = button;
        _timer = timer;
    }

    /// <summary>刷新按钮文字与可见性。</summary>
    internal static void Refresh(NCharacterSelectScreen screen)
    {
        var button = _button;
        if (button is null || !GodotObject.IsInstanceValid(button))
        {
            return;
        }

        var lobby = screen.Lobby;
        if (lobby is null)
        {
            button.Visible = false;
            button.Disabled = true;
            return;
        }

        // 主机侧：选人界面阶段一局还没开始，RunManager 上还没有网络服务，先补一次。
        // 否则主机会把客户端发来的"加入合作模式"请求静默丢掉（非主机那边看起来就是按了没反应）。
        SymbiosisMembers.BindHostService(lobby.NetService);
        SymbiosisMembers.PrepareClientLobby(lobby.NetService);

        if (!TogetherCoopGate.Applies(lobby) || lobby.LocalPlayer.id == 0UL)
        {
            // 单人局 / 关了合作开关 / 自己的 id 还没落定 → 不显示。
            button.Visible = false;
            button.Disabled = true;
            return;
        }

        // 不带 "n/上限" 这种悬浮分数：几个人的大厅都能用，加入的人自成一组。
        button.Visible = true;
        button.Disabled = false;
        button.Text = TogetherCoopGate.IsConfirmed(lobby, lobby.LocalPlayer.id) ? TextLeave : TextJoin;
    }

    /// <summary>按下按钮：加入合作（并确认准备）/ 退出合作（并取消准备）。</summary>
    private static void OnPressed(NCharacterSelectScreen screen)
    {
        var lobby = screen.Lobby;
        if (lobby is null)
        {
            return;
        }

        SymbiosisMembers.BindHostService(lobby.NetService);

        var localId = lobby.LocalPlayer.id;
        if (localId == 0UL)
        {
            return;
        }

        if (TogetherCoopGate.IsConfirmed(lobby, localId))
        {
            // 退出合作：先取消登记，再走本体的"取消准备"（它会把选人界面、官方确认键都恢复回来）。
            SymbiosisMembers.TrySet(lobby.NetService, localId, confirm: false, "lobby_button");
            if (lobby.LocalPlayer.isReady)
            {
                InvokeBodyHandler(screen, UnreadyPressed, "symbiosis.leave");
            }
        }
        else
        {
            // 加入合作：登记 + 走本体的"确认"（等价于按下官方确认键：收起选人界面、切到"已准备"、等其他人）。
            if (!SymbiosisMembers.TrySet(lobby.NetService, localId, confirm: true, "lobby_button"))
            {
                CappedLog.Info("symbiosis.reject_send", $"合作模式请求未被接受：netId={localId}");
            }

            if (!lobby.LocalPlayer.isReady)
            {
                InvokeBodyHandler(screen, EmbarkPressed, "symbiosis.join");
            }
        }

        Refresh(screen);
    }

    /// <summary>
    /// 反射调本体的两个 private 处理函数（等价于玩家自己按了官方"确认 / 取消准备"键）。
    /// </summary>
    /// <remarks>参数是 <c>NButton _</c>，本体自己也会传 <c>null</c> 进来（FTUE 回调那条路），所以传 null 是安全的。</remarks>
    private static void InvokeBodyHandler(NCharacterSelectScreen screen, MethodInfo? handler, string tag)
    {
        if (handler is null)
        {
            CappedLog.Info(tag, "本体方法没找到（游戏版本变了？）：只登记了合作状态");
            return;
        }

        try
        {
            handler.Invoke(screen, new object?[] { null });
        }
        catch (Exception ex)
        {
            CappedLog.Info(tag, $"调用本体方法失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>玩家用本体自带的"取消准备"退出来时，顺手把合作登记也取消（否则会以为自己退了、其实还在组里）。</summary>
    internal static void OnBodyUnready(NCharacterSelectScreen screen)
    {
        var lobby = screen.Lobby;
        if (lobby is null || !TogetherCoopGate.Applies(lobby))
        {
            return;
        }

        var localId = lobby.LocalPlayer.id;
        if (localId == 0UL || !TogetherCoopGate.IsConfirmed(lobby, localId))
        {
            return;
        }

        SymbiosisMembers.BindHostService(lobby.NetService);
        SymbiosisMembers.TrySet(lobby.NetService, localId, confirm: false, "lobby_unready");
        Refresh(screen);
    }

    private static void FreeIfChildOf(Node? node, Node parent)
    {
        if (node is not null && GodotObject.IsInstanceValid(node) && node.GetParent() == parent)
        {
            node.QueueFree();
        }
    }
}

/// <summary>
/// 选人界面：打开时登记界面 + 装「合作模式」按钮，关闭时收掉自己加的节点，
/// 换人（本地点选 / 队友改选）后刷新按钮。
/// </summary>
[HarmonyPatch]
internal static class CharacterSelectPatches
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(NCharacterSelectScreen), "OnSubmenuOpened");
        yield return AccessTools.Method(typeof(NCharacterSelectScreen), "OnSubmenuClosed");
        yield return AccessTools.Method(typeof(NCharacterSelectScreen), nameof(NCharacterSelectScreen.SelectCharacter));
        yield return AccessTools.Method(typeof(NCharacterSelectScreen), nameof(NCharacterSelectScreen.PlayerChanged));
    }

    [HarmonyPostfix]
    private static void Postfix(NCharacterSelectScreen __instance, MethodBase __originalMethod)
    {
        switch (__originalMethod.Name)
        {
            case "OnSubmenuOpened":
                CharacterSelectGateImpl.OnOpened(__instance);
                break;

            case "OnSubmenuClosed":
                CharacterSelectGateImpl.OnClosed(__instance);
                break;

            default:
                CharacterSelectGateImpl.Refresh(__instance);
                break;
        }
    }
}

/// <summary>本体自带的"取消准备"键：按下去时同步取消合作登记（见 <see cref="CharacterSelectGateImpl.OnBodyUnready" />）。</summary>
[HarmonyPatch]
internal static class CharacterSelectUnreadyPatches
{
    private static MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(NCharacterSelectScreen), "OnUnreadyPressed");
    }

    [HarmonyPostfix]
    private static void Postfix(NCharacterSelectScreen __instance)
    {
        CharacterSelectGateImpl.OnBodyUnready(__instance);
    }
}
