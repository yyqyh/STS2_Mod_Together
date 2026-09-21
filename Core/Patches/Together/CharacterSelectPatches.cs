using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using Together.Core.Combat;
using Together.Core.Content;
using Together.Core.Utils;

namespace Together.Core.Patches;

/// <summary>
/// 选人界面的共生体逻辑（按钮 + 起程门控），共享实现放在这里，补丁类接在文件末尾。
/// </summary>
/// <remarks>
/// <b>补丁一律写成类级 <c>[HarmonyPatch]</c></b>：实测方法级 <c>[HarmonyPatch]</c>
/// 在这个 Harmony 构建下不会被识别（日志出现 <c>Harmony patched 0 method(s)</c>，门控静默失效）。
/// </remarks>
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

    /// <summary>把「共生体」按钮挂到选人界面上（关闭共生体 / 单人局时只是隐藏）。</summary>
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
            Text = "确定参加共生体",
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

        // 别人确定之后本地的按钮也要立刻变灰/变字：用一个 0.2s 的 Timer 对齐，
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
            if (GodotObject.IsInstanceValid(screen))
            {
                Refresh(screen);
            }
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
            button.Text = $"共生体：已确定（{count}/{capacity}）· 再按一下退出";
        }
        else if (count >= capacity)
        {
            button.Text = $"共生体：名额已满（{count}/{capacity}）· 没位置了";
            button.Disabled = true;
        }
        else
        {
            button.Text = $"共生体：未确定（{count}/{capacity}）· 按一下加入";
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

    /// <summary>起程门控：只有"确定了 1 个人"时拦住（0 人或满 2 人都放行）。</summary>
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

        if (TogetherCoopGate.CanEmbark(lobby))
        {
            embark.Enable();
        }
        else
        {
            embark.Disable();
        }
    }

    private static void FreeIfChildOf(Node? node, Node parent)
    {
        if (node is not null && GodotObject.IsInstanceValid(node) && node.GetParent() == parent)
        {
            node.QueueFree();
        }
    }
}

/// <summary>登记界面 + 装「共生体」按钮。</summary>
[HarmonyPatch(typeof(NCharacterSelectScreen), "OnSubmenuOpened")]
internal static class CharacterSelectOpenedPatch
{
    [HarmonyPostfix]
    private static void Postfix(NCharacterSelectScreen __instance)
    {
        CharacterSelectGateImpl.OnOpened(__instance);
    }
}

/// <summary>界面关闭时收掉自己加的节点。</summary>
[HarmonyPatch(typeof(NCharacterSelectScreen), "OnSubmenuClosed")]
internal static class CharacterSelectClosedPatch
{
    [HarmonyPostfix]
    private static void Postfix(NCharacterSelectScreen __instance)
    {
        CharacterSelectGateImpl.OnClosed(__instance);
    }
}

/// <summary>点选任意角色后刷新按钮与起程按钮。</summary>
[HarmonyPatch(typeof(NCharacterSelectScreen), nameof(NCharacterSelectScreen.SelectCharacter))]
internal static class CharacterSelectSelectPatch
{
    [HarmonyPostfix]
    private static void Postfix(NCharacterSelectScreen __instance)
    {
        CharacterSelectGateImpl.Refresh(__instance);
        CharacterSelectGateImpl.RefreshEmbark(__instance);
    }
}

/// <summary>队友改选后同样刷新（远端变化只会走到这里）。</summary>
[HarmonyPatch(typeof(NCharacterSelectScreen), nameof(NCharacterSelectScreen.PlayerChanged))]
internal static class CharacterSelectPlayerChangedPatch
{
    [HarmonyPostfix]
    private static void Postfix(NCharacterSelectScreen __instance)
    {
        CharacterSelectGateImpl.Refresh(__instance);
        CharacterSelectGateImpl.RefreshEmbark(__instance);
    }
}
