using System.Reflection;

using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using Together.Core.Combat;
using Together.Core.Content;
using Together.Core.Utils;

namespace Together.Core.Patches;

/// <summary>选人界面的共享卡组逻辑（按钮 + 起程门控），补丁接在文件末尾。</summary>
internal static class CharacterSelectGateImpl
{
    /// <summary>当前打开的角色选择界面（屏幕自己在 OnSubmenuOpened 时登记）。</summary>
    internal static NCharacterSelectScreen? CachedScreen;

    private static Button? _button;

    private static Godot.Timer? _timer;

    internal static readonly AccessTools.FieldRef<NCharacterSelectScreen, NConfirmButton> EmbarkButton =
        AccessTools.FieldRefAccess<NCharacterSelectScreen, NConfirmButton>("_embarkButton");

    /// <summary>打开选人界面：装按钮 + 对齐一次状态。</summary>
    internal static void OnOpened(NCharacterSelectScreen screen)
    {
        CachedScreen = screen;
        Install(screen);
        Refresh(screen);
        RefreshEmbark(screen);
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

    /// <summary>把「共享卡组」按钮挂到选人界面上（关闭开关 / 单人局时只是隐藏）。</summary>
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
            Name = "TogetherSymbiosisButton",
            Text = "共享卡组",
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

        // 别人确定之后本地的按钮/官方确认键都要立刻跟上：用一个 0.2s 的 Timer 对齐，
        // 不直接在网络回调里碰 UI 节点（那个回调不保证在主线程）。
        var timer = new Godot.Timer
        {
            Name = "TogetherSymbiosisRefresh",
            WaitTime = 0.2,
            Autostart = true,
            OneShot = false,
        };
        screen.AddChild(timer);
        timer.Timeout += () =>
        {
            if (!GodotObject.IsInstanceValid(screen))
            {
                return;
            }

            Refresh(screen);

            // 官方确认键也要一起刷新：本体的 NConfirmButton.Disable() 是把按钮滑出屏幕，
            // 而它自己只在"打开界面 / 换人"时 Enable 一次，所以"另一个人按下确定"这种网络变化
            // 不会让按钮回来 —— 这就是"两人确认完还得主机取消再确认"的原因。
            RefreshEmbark(screen);
        };

        _button = button;
        _timer = timer;
    }

    /// <summary>刷新按钮文字与可用状态。</summary>
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

        // 主机侧：选人界面阶段 RunManager 还没装上网络服务，先补一次。
        // 否则主机会把客户端发来的"确定参加共生体"请求静默丢掉（非主机那边看起来就是按了没反应）。
        SymbiosisMembers.BindHostService(lobby.NetService);
        SymbiosisMembers.PrepareClientLobby(lobby.NetService);

        if (!TogetherCoopGate.Applies(lobby))
        {
            button.Visible = false;
            button.Disabled = true;
            return;
        }

        var localId = lobby.LocalPlayer.id;
        if (localId == 0UL)
        {
            // LocalPlayer 还没落定（大厅刚建立）→ 先不显示可点的按钮。
            button.Visible = false;
            button.Disabled = true;
            return;
        }

        var capacity = SymbiosisMembers.Capacity;
        var confirmed = TogetherCoopGate.IsConfirmed(lobby, localId);
        var count = TogetherCoopGate.CountConfirmed(lobby);

        button.Visible = true;
        button.Disabled = false;

        // 文案要说清"能不能再按一次取消" —— 光写"确定参加共生体"没人知道还能退出。
        if (confirmed)
        {
            button.Text = $"共享卡组：已确定（{count}/{capacity}）· 再按一下退出";
        }
        else if (count >= capacity)
        {
            button.Text = $"共享卡组：名额已满（{count}/{capacity}）· 没位置了";
            button.Disabled = true;
        }
        else
        {
            button.Text = $"共享卡组：未确定（{count}/{capacity}）· 按一下加入";
        }
    }

    /// <summary>按下按钮：确定 / 取消自己参加共生体。</summary>
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

        var confirm = !TogetherCoopGate.IsConfirmed(lobby, localId);

        if (confirm && !TogetherCoopGate.CanConfirm(lobby, localId))
        {
            CappedLog.Info("symbiosis.reject_local", $"共生体名额已满，本地点击忽略：netId={localId}");
            Refresh(screen);
            return;
        }

        if (!SymbiosisMembers.TrySet(lobby.NetService, localId, confirm, "lobby_button"))
        {
            CappedLog.Info("symbiosis.reject_send", $"共生体请求未被接受：netId={localId} confirm={confirm}");
        }

        Refresh(screen);
        RefreshEmbark(screen);
    }

    /// <summary>
    /// 起程门控：0 人确定（普通联机）或达到最小人数（≥2，这些人共享卡组）都放行；只有"确定了 1 个人"时拦住。
    /// </summary>
    /// <remarks>
    /// 必须被反复调用（0.2 秒的轮询 + 每次换人）：官方确认键被拦下时是<b>滑出屏幕</b>（<c>NConfirmButton.Disable</c>），
    /// 而本体只在"打开界面 / 换人"时 Enable 一次，网络侧的人数变化不会触发它。
    /// <c>Enable/Disable</c> 本身幂等，重复调用不会重播动画，所以这里放心跟着轮询一起刷。
    /// </remarks>
    internal static void RefreshEmbark(NCharacterSelectScreen screen)
    {
        var lobby = screen.Lobby;
        if (lobby is null || !TogetherCoopGate.Applies(lobby))
        {
            return;
        }

        NConfirmButton embark;
        try
        {
            embark = EmbarkButton(screen);
        }
        catch (Exception)
        {
            return;
        }

        if (embark is null)
        {
            return;
        }

        var canEmbark = TogetherCoopGate.CanEmbark(lobby);
        if (embark.IsEnabled != canEmbark)
        {
            CappedLog.Info(
                "symbiosis.embark",
                $"官方确认键：{(canEmbark ? "放行" : "拦住（只确定了 1 人）")}"
                + $"（已确定 {TogetherCoopGate.CountConfirmed(lobby)} 人；达到 {TogetherPair.MinMembers} 人即共享卡组）");
        }

        embark.SetEnabled(canEmbark);
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
/// 选人界面：打开时登记界面 + 装「共生体」按钮，关闭时收掉自己加的节点，
/// 换人（本地点选 / 队友改选）后刷新按钮与起程按钮。
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
                CharacterSelectGateImpl.RefreshEmbark(__instance);
                break;
        }
    }
}
