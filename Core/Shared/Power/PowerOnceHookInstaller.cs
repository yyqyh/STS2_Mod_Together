using System.Reflection;

using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using Together.Core.Alignment;
using Together.Core.Diagnostics;

namespace Together.Core.Shared.Power;

/// <summary>
/// 把 <see cref="PowerOnceHookTargets" /> 扫出来的方法挂上"只由原件触发"的前缀。
/// </summary>
/// <remarks>
/// <para>
/// 为什么要一个安装器：<c>TargetMethods()</c> 是启动时求值的，那时后加载的 mod 能力还没出现。
/// 这里做两件事：① 启动时扫一遍已加载的程序集；② 之后每次 <c>ModManager.OnModDetected</c> 触一次
/// <b>增量</b>扫描（<see cref="PowerOnceHookTargets.Discover" /> 按程序集 MVID 记账，扫过的不会再扫）。
/// </para>
/// <para>
/// 前缀复用 <see cref="MirroredPowerSingleFirePatch" /> 那个（同一套开关 + <c>IsMirrorCopy</c> 判据），
/// 所以这里只负责"把新方法挂上"，不重复实现逻辑。
/// </para>
/// </remarks>
internal static class PowerOnceHookInstaller
{
    private static Harmony? _harmony;

    private static readonly HashSet<MethodBase> Patched = [];

    private static bool _subscribed;

    public static void Install(Harmony harmony)
    {
        _harmony = harmony;
        Sweep("init");

        if (_subscribed)
        {
            return;
        }

        ModManager.OnModDetected += OnModDetected;
        _subscribed = true;
    }

    private static void OnModDetected(Mod mod)
    {
        if (mod.state == ModLoadState.Loaded)
        {
            Sweep($"mod:{mod.manifest?.id}");
        }
    }

    private static void Sweep(string reason)
    {
        if (_harmony is null)
        {
            return;
        }

        // 命中清单**一次汇总**打出去：逐条打会被 CappedLog 的条数上限截断（实测 90 项时就被截了），
        // 而这份清单正是"到底扫到了谁"的唯一证据。
        var listed = new List<string>();

        foreach (var method in PowerOnceHookTargets.Discover())
        {
            lock (Patched)
            {
                if (!Patched.Add(method))
                {
                    continue;
                }
            }

            try
            {
                _harmony.Patch(
                    method,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(MirroredPowerSingleFirePatch), "Prefix")));

                listed.Add($"{method.DeclaringType?.Name}.{method.Name}");
            }
            catch (Exception ex)
            {
                // 挂不上就退回旧行为（该能力仍会两份各触发），不能影响别的补丁。
                CappedLog.Info(
                    "power.once",
                    $"挂载失败（忽略）：{method.DeclaringType?.Name}.{method.Name}：{ex.GetType().Name}: {ex.Message}");
            }
        }

        if (listed.Count == 0)
        {
            return;
        }

        CappedLog.Info(
            "power.once",
            $"列入「效果只算原件一次」{listed.Count} 项（{reason}）：{string.Join(", ", listed)}");
    }
}
