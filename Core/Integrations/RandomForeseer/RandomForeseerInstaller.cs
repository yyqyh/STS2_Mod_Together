using System.Reflection;

using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using Together.Core.Diagnostics;

namespace Together.Core.Integrations.RandomForeseer;

/// <summary>
/// 联动补丁的安装器：<b>等 RandomForeseer 真的加载了</b>再把
/// <see cref="RandomForeseerBridge.PatchCategory" /> 这一类补丁挂上去。
/// </summary>
/// <remarks>
/// 这些补丁类只带 <c>[HarmonyPatchCategory]</c>，不带普通的整类安装（<c>Main.ApplyPatches</c> 会跳过带类别的类），
/// 所以对方没装时它们<b>完全不参与</b>：既不会让启动行出现失败计数，也不会留下半挂状态。
/// 加载顺序无关：对方后加载时由 <c>ModManager.OnModDetected</c> 补挂。
/// </remarks>
internal static class RandomForeseerInstaller
{
    private static Harmony? _harmony;

    private static Assembly? _assembly;

    private static bool _subscribed;

    private static bool _patched;

    public static void Install(Harmony harmony, Assembly assembly)
    {
        _harmony = harmony;
        _assembly = assembly;

        if (TryPatch("init"))
        {
            return;
        }

        if (_subscribed)
        {
            return;
        }

        ModManager.OnModDetected += OnModDetected;
        _subscribed = true;
        CappedLog.Info("rf.bridge", "RandomForeseer 还没加载：等它加载后再挂联动补丁");
    }

    private static void OnModDetected(Mod mod)
    {
        if (mod.state != ModLoadState.Loaded || mod.manifest?.id != RandomForeseerBridge.ModId)
        {
            return;
        }

        if (TryPatch("mod_detected") && _subscribed)
        {
            ModManager.OnModDetected -= OnModDetected;
            _subscribed = false;
        }
    }

    private static bool TryPatch(string reason)
    {
        if (_patched)
        {
            return true;
        }

        if (_harmony is null || _assembly is null || !RandomForeseerBridge.TryResolve())
        {
            return false;
        }

        try
        {
            _harmony.PatchCategory(_assembly, RandomForeseerBridge.PatchCategory);
            _patched = true;
            CappedLog.Info("rf.bridge", $"联动补丁已挂上（{reason}）：共享牌堆 / 球位 / 冻眼抽牌堆主人 三处收口");
        }
        catch (Exception ex)
        {
            // 挂不上就当作"没有联动"：预测保持原样，不能影响 together 本体。
            CappedLog.Info("rf.bridge", $"联动补丁挂载失败（{reason}，忽略）：{ex.GetType().Name}: {ex.Message}");
            _patched = true;   // 不再重试，避免反复报错
        }

        return true;
    }
}
