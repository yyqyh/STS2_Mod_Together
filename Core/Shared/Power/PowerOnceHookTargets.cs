using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using STS2RitsuLib;
using STS2RitsuLib.Utils.Persistence;
using Together.Core.Diagnostics;
using Together;

namespace Together.Core.Shared.Power;

/// <summary>
/// 「(B) 一次性触发」能力的结构判据：**重写了回合族钩子、并且那个钩子里真的产生了外部效果**。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要它：我们对每个能力都镜像一份（存在性对称，别的代码读 <c>GetPower</c> 两边一致），
/// 但本体的事件钩子是<b>广播</b> —— 原件和副本都会收到同一次派发。于是"每次触发只该发生一次"的能力
/// （幻术师的镜像开火、每回合召唤/加牌/造成一次伤害…）会变成"两份各来一次"，或者两端内部状态不齐时
/// 一端有一端没有（实测幻术师就是后者，直接 checksum 分叉踢人）。
/// </para>
/// <para>
/// <b>三道门</b>：① 是镜像副本（由调用方 <c>PowerMirror.IsMirrorCopy</c> 判）；
/// ② 该类型<b>重写</b>了下面表里的某个回合族钩子；
/// ③ 那个重写的方法体里<b>调用了会产生外部效果的命令</b>（<c>MegaCrit.Sts2.Core.Commands.*</c> 或 <c>Hook</c> 广播）。
/// 门 ③ 专门用来排除"回合末只是重置内部计数"的能力（杂耍 <c>JugglingPower</c> 跳掉会整局不重置）。
/// </para>
/// <para>
/// <b>async 的坑</b>：本体钩子多是 <c>async Task</c>，方法自己只剩"建状态机 + Start"，
/// 必须先取 <see cref="AsyncStateMachineAttribute" /> 的 <c>StateMachineType.MoveNext</c> 再扫 IL。
/// </para>
/// <para>
/// <b>只扫没扫过的</b>：按程序集 <c>MVID</c> 记账，同一个程序集只扫一次；结果按类型/方法缓存。
/// 后加载的 mod 由 <see cref="PowerOnceHookInstaller" /> 在 <c>ModManager.OnModDetected</c> 时触发增量扫描。
/// </para>
/// </remarks>
internal static class PowerOnceHookTargets
{
    /// <summary>要扫的"回合族钩子"：名字 + 参数签名（取自 <c>AbstractModel</c>）。</summary>
    private static readonly (string Name, Type[] Signature)[] RoundHooks =
    [
        ("AfterAutoPrePlayPhaseEnteredEarly", [typeof(PlayerChoiceContext), typeof(Player)]),
        ("AfterAutoPrePlayPhaseEntered", [typeof(PlayerChoiceContext), typeof(Player)]),
        ("AfterAutoPrePlayPhaseEnteredLate", [typeof(PlayerChoiceContext), typeof(Player)]),
        ("AfterAutoPostPlayPhaseEntered", [typeof(PlayerChoiceContext), typeof(Player)]),
        ("BeforeSideTurnStart", [typeof(PlayerChoiceContext), typeof(CombatSide), typeof(IReadOnlyList<Creature>), typeof(ICombatState)]),
        ("AfterSideTurnStart", [typeof(CombatSide), typeof(IReadOnlyList<Creature>), typeof(ICombatState)]),
        ("AfterSideTurnStartLate", [typeof(CombatSide), typeof(IReadOnlyList<Creature>), typeof(ICombatState)]),
        ("AfterPlayerTurnStartEarly", [typeof(PlayerChoiceContext), typeof(Player)]),
        ("AfterPlayerTurnStart", [typeof(PlayerChoiceContext), typeof(Player)]),
        ("AfterPlayerTurnStartLate", [typeof(PlayerChoiceContext), typeof(Player)]),
        ("BeforeSideTurnEndVeryEarly", [typeof(PlayerChoiceContext), typeof(CombatSide), typeof(IEnumerable<Creature>)]),
        ("BeforeSideTurnEndEarly", [typeof(PlayerChoiceContext), typeof(CombatSide), typeof(IEnumerable<Creature>)]),
        ("BeforeSideTurnEnd", [typeof(PlayerChoiceContext), typeof(CombatSide), typeof(IEnumerable<Creature>)]),
        ("AfterSideTurnEnd", [typeof(PlayerChoiceContext), typeof(CombatSide), typeof(IEnumerable<Creature>)]),
        ("AfterSideTurnEndLate", [typeof(PlayerChoiceContext), typeof(CombatSide), typeof(IEnumerable<Creature>)]),
    ];

    private static readonly object Gate = new();

    /// <summary>缓存文件里的规则版本：判据改了就把这个数字 +1（老缓存整份作废重扫）。</summary>
    /// <remarks>v2：门③从"钩子体里直接调 `Commands.*`"放宽成"**顺着调用链**往下看 N 层"（幻术师 `MirrorImagePower` 的
    /// `AfterPlayerTurnStartLate → FireAll → FireOne → CardCmd…` 是三层）。</remarks>
    private const int RulesVersion = 2;

    /// <summary>门③顺着调用链往下看几层（只跟"本类型/基类里定义的方法"）。</summary>
    private const int EffectCallDepth = 3;

    private const string CacheKey = "power_once_cache";

    private const string CacheFileName = "together_power_once.json";

    /// <summary>已经扫过的程序集（MVID）。</summary>
    private static readonly HashSet<string> ScannedAssemblies = [];

    /// <summary>方法 → 是否满足门 ②+③（缓存）。</summary>
    private static readonly Dictionary<MethodBase, bool> Verdicts = [];

    /// <summary>已经发现并报出去过的方法（避免重复挂补丁）。</summary>
    private static readonly HashSet<MethodBase> Reported = [];

    /// <summary>6 个基线能力（由 <c>MirroredPowerSingleFirePatch</c> 的整类补丁负责，这里不重复挂）。</summary>
    private static readonly HashSet<MethodBase> Baselines =
    [
        AccessTools.Method(typeof(TemporaryStrengthPower), nameof(TemporaryStrengthPower.AfterSideTurnEnd)),
        AccessTools.Method(typeof(TemporaryDexterityPower), nameof(TemporaryDexterityPower.AfterSideTurnEnd)),
        AccessTools.Method(typeof(TemporaryFocusPower), nameof(TemporaryFocusPower.AfterSideTurnEnd)),
        AccessTools.Method(typeof(WeakPower), nameof(WeakPower.AfterSideTurnEnd)),
        AccessTools.Method(typeof(VulnerablePower), nameof(VulnerablePower.AfterSideTurnEnd)),
        AccessTools.Method(typeof(FrailPower), nameof(FrailPower.AfterSideTurnEnd)),
    ];

    private static PowerOnceCacheData? _cache;

    private static bool _cacheLoaded;

    /// <summary>扫描"还没扫过的程序集"，返回新发现的方法（可能为空）。</summary>
    public static IReadOnlyList<MethodBase> Discover()
    {
        var found = new List<MethodBase>();
        var cache = LoadCache();
        var reset = cache is not null && cache.RulesVersion != RulesVersion;
        var updates = new List<PowerOnceCacheEntry>();
        var scanned = 0;
        var reused = 0;

        Assembly[] assemblies;
        try
        {
            assemblies = AppDomain.CurrentDomain.GetAssemblies();
        }
        catch (Exception)
        {
            return found;
        }

        foreach (var assembly in assemblies)
        {
            if (!TryClaimAssembly(assembly))
            {
                continue;
            }

            var mvid = MvidOf(assembly);

            // ① 缓存命中（同一个二进制 → 上次的结论仍然成立）：按记下来的"类型|钩子"直接反射取方法，
            //    **一个 IL 都不扫**。任何一个解不出来就当缓存失效，退回真扫。
            if (!reset
                && mvid is not null
                && cache?.Find(mvid) is { } hit
                && ResolveCached(assembly, hit.Methods) is { } cachedMethods)
            {
                reused++;
                foreach (var method in cachedMethods)
                {
                    if (Claim(method))
                    {
                        found.Add(method);
                    }
                }

                continue;
            }

            // ② 真扫这个程序集，并把结论记进缓存。
            scanned++;
            var discovered = new List<MethodBase>();

            foreach (var type in TypesOf(assembly))
            {
                if (type.IsAbstract || !typeof(PowerModel).IsAssignableFrom(type))
                {
                    continue;
                }

                foreach (var (name, signature) in RoundHooks)
                {
                    if (OverrideOf(type, name, signature) is { } method
                        && IsOncePerTrigger(method)
                        && !Baselines.Contains(method))
                    {
                        discovered.Add(method);
                    }
                }
            }

            if (mvid is not null)
            {
                updates.Add(new PowerOnceCacheEntry
                {
                    Mvid = mvid,
                    Name = assembly.GetName().Name ?? "<unknown>",
                    Methods = [.. discovered.Select(KeyOf)],
                });
            }

            foreach (var method in discovered)
            {
                if (Claim(method))
                {
                    found.Add(method);
                }
            }
        }

        if (updates.Count > 0)
        {
            SaveCache(reset, updates);
        }

        if (scanned > 0)
        {
            CappedLog.Info(
                "power.once",
                $"(B) 判据扫描：本次真扫 {scanned} 个程序集、复用缓存 {reused} 个"
                + $"（扫过的按 MVID 记账，同一进程内不再重扫）");
        }

        return found;
    }

    /// <summary>这个方法（某个能力的某个回合族钩子重写）是不是"会产生外部效果"。</summary>
    private static bool IsOncePerTrigger(MethodBase method)
    {
        lock (Gate)
        {
            if (Verdicts.TryGetValue(method, out var cached))
            {
                return cached;
            }
        }

        var verdict = CallsEffectCommand(method);

        lock (Gate)
        {
            Verdicts[method] = verdict;
        }

        return verdict;
    }

    private static bool Claim(MethodBase method)
    {
        lock (Gate)
        {
            return Reported.Add(method);
        }
    }

    /// <summary>这个程序集是不是第一次扫（同一个程序集只扫一次）。</summary>
    private static bool TryClaimAssembly(Assembly assembly)
    {
        try
        {
            if (assembly.IsDynamic || IsFramework(assembly))
            {
                return false;
            }

            var mvid = assembly.ManifestModule.ModuleVersionId.ToString();
            lock (Gate)
            {
                return ScannedAssemblies.Add(mvid);
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>框架程序集直接跳过（不可能有 <c>PowerModel</c> 派生类）。</summary>
    private static bool IsFramework(Assembly assembly)
    {
        var name = assembly.GetName().Name ?? string.Empty;

        return name.StartsWith("System", StringComparison.Ordinal)
               || name.StartsWith("Microsoft", StringComparison.Ordinal)
               || name.StartsWith("Godot", StringComparison.Ordinal)
               || name.StartsWith("Mono", StringComparison.Ordinal)
               || name.StartsWith("netstandard", StringComparison.Ordinal)
               || name.StartsWith("mscorlib", StringComparison.Ordinal)
               || name.StartsWith("HarmonyLib", StringComparison.Ordinal)
               || name.StartsWith("0Harmony", StringComparison.Ordinal);
    }

    private static IEnumerable<Type> TypesOf(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // 有类型加载失败时仍然尽量扫能加载的那部分。
            return ex.Types.Where(type => type is not null)!;
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>该类型自己重写了这个钩子（不是继承基类空实现）时返回那个方法。</summary>
    private static MethodInfo? OverrideOf(Type type, string name, Type[] signature)
    {
        try
        {
            var method = type.GetMethod(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: signature,
                modifiers: null);

            return method is null || method.DeclaringType == typeof(AbstractModel) ? null : method;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>门 ③：方法体（含它调用的本类型方法，最多 <see cref="EffectCallDepth" /> 层）里有没有调"效果类命令"或 Hook 广播。</summary>
    /// <remarks>
    /// 为什么要顺链：这类能力的钩子方法体往往只有 `FireAll(...)` / `FireOne(...)` 这种<b>自己类里的辅助方法</b>，
    /// 真正的 `CardCmd`/`PowerCmd` 在更深一层 —— 只看最外层会漏（实测幻术师 `MirrorImagePower` 就是这么漏的）。
    /// </remarks>
    private static bool CallsEffectCommand(MethodBase method)
    {
        return CallsEffectCommand(method, EffectCallDepth, []);
    }

    private static bool CallsEffectCommand(MethodBase method, int depth, HashSet<MethodBase> visited)
    {
        try
        {
            var body = BodyOf(method);
            if (!visited.Add(body))
            {
                return false;
            }

            var bytes = body.GetMethodBody()?.GetILAsByteArray();
            if (bytes is null)
            {
                return false;
            }

            for (var i = 0; i + 4 < bytes.Length; i++)
            {
                if (bytes[i] != OpCodes.Call.Value && bytes[i] != OpCodes.Callvirt.Value)
                {
                    continue;
                }

                var token = BitConverter.ToInt32(bytes, i + 1);
                try
                {
                    if (body.Module.ResolveMethod(token) is not { } target)
                    {
                        continue;
                    }

                    var declaring = target.DeclaringType;
                    if (declaring is null)
                    {
                        continue;
                    }

                    if (declaring == typeof(Hook)
                        || declaring.Namespace?.StartsWith("MegaCrit.Sts2.Core.Commands", StringComparison.Ordinal) == true)
                    {
                        return true;
                    }

                    // 只跟"本类型/基类里定义的方法"（其他类的方法由它们自己的钩子负责）。
                    if (depth > 0
                        && target is not null
                        && declaring is not null
                        && typeof(PowerModel).IsAssignableFrom(declaring)
                        && CallsEffectCommand(target, depth - 1, visited))
                    {
                        return true;
                    }
                }
                catch (Exception)
                {
                    // 这个 token 不是方法，继续扫。
                }
            }
        }
        catch (Exception)
        {
            // 读不到方法体 → 当作"没有外部效果"，退回旧行为。
        }

        return false;
    }

    /// <summary>拿到真正装代码的方法：<c>async</c> 取状态机的 <c>MoveNext</c>，其余取自己。</summary>
    private static MethodBase BodyOf(MethodBase method)
    {
        try
        {
            if (method is MethodInfo info
                && info.GetCustomAttribute<AsyncStateMachineAttribute>() is { StateMachineType: { } stateMachine }
                && AccessTools.Method(stateMachine, "MoveNext") is { } moveNext)
            {
                return moveNext;
            }
        }
        catch (Exception)
        {
            // 拿不到特性就按同步方法扫。
        }

        return method;
    }

    // ======================================================================
    // 跨启动缓存（按程序集 MVID；mod 更新 → MVID 变 → 自动失效重扫）
    // ======================================================================

    internal sealed class PowerOnceCacheData
    {
        public int RulesVersion { get; set; }

        public List<PowerOnceCacheEntry> Assemblies { get; set; } = [];

        public PowerOnceCacheEntry? Find(string mvid)
        {
            return Assemblies.FirstOrDefault(e => string.Equals(e.Mvid, mvid, StringComparison.OrdinalIgnoreCase));
        }

        public void Upsert(PowerOnceCacheEntry entry)
        {
            Assemblies.RemoveAll(e => string.Equals(e.Mvid, entry.Mvid, StringComparison.OrdinalIgnoreCase));
            Assemblies.Add(entry);
        }
    }

    /// <summary>单个程序集的扫描结论。</summary>
    internal sealed class PowerOnceCacheEntry
    {
        public string Mvid { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        /// <summary>满足 (B) 判据的方法，格式 "<c>类型全名|钩子名</c>"。</summary>
        public List<string> Methods { get; set; } = [];
    }

    private static string? MvidOf(Assembly assembly)
    {
        try
        {
            return assembly.ManifestModule.ModuleVersionId.ToString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string KeyOf(MethodBase method)
    {
        return $"{method.DeclaringType?.FullName}|{method.Name}";
    }

    /// <summary>把缓存里记的"类型|钩子"解析回方法；有一个解不出来就返回 null（缓存失效，退回真扫）。</summary>
    private static List<MethodBase>? ResolveCached(Assembly assembly, List<string> keys)
    {
        var resolved = new List<MethodBase>(keys.Count);

        foreach (var key in keys)
        {
            var split = key.LastIndexOf('|');
            if (split <= 0)
            {
                return null;
            }

            var typeName = key[..split];
            var hookName = key[(split + 1)..];

            if (SignatureOf(hookName) is not { } signature
                || assembly.GetType(typeName, throwOnError: false) is not { } type
                || OverrideOf(type, hookName, signature) is not { } method)
            {
                return null;
            }

            resolved.Add(method);
        }

        return resolved;
    }

    private static Type[]? SignatureOf(string hookName)
    {
        foreach (var (name, signature) in RoundHooks)
        {
            if (string.Equals(name, hookName, StringComparison.Ordinal))
            {
                return signature;
            }
        }

        return null;
    }

    private static PowerOnceCacheData? LoadCache()
    {
        if (_cacheLoaded)
        {
            return _cache;
        }

        _cacheLoaded = true;

        try
        {
            using (RitsuLibFramework.BeginModDataRegistration(Const.ModId, false))
            {
                RitsuLibFramework.GetDataStore(Const.ModId).Register(
                    CacheKey,
                    CacheFileName,
                    SaveScope.Global,
                    defaultFactory: () => new PowerOnceCacheData(),
                    autoCreateIfMissing: true);
            }

            RitsuLibFramework.GetDataStore(Const.ModId).InitializeGlobal();
            _cache = RitsuLibFramework.GetDataStore(Const.ModId).Get<PowerOnceCacheData>(CacheKey);
        }
        catch (Exception ex)
        {
            _cache = null;
            CappedLog.Info("power.once", $"缓存不可用（本次仍会完整扫描）：{ex.GetType().Name}: {ex.Message}");
        }

        return _cache;
    }

    private static void SaveCache(bool reset, List<PowerOnceCacheEntry> updates)
    {
        try
        {
            var store = RitsuLibFramework.GetDataStore(Const.ModId);

            store.Modify<PowerOnceCacheData>(CacheKey, data =>
            {
                if (reset)
                {
                    data.Assemblies.Clear();
                }

                data.RulesVersion = RulesVersion;

                foreach (var entry in updates)
                {
                    data.Upsert(entry);
                }
            });

            store.Save(CacheKey);

            CappedLog.Info(
                "power.once",
                $"扫描结论已写入缓存 {CacheFileName}（{updates.Count} 个程序集；更新过 mod 的程序集 MVID 一变就会自动重扫）");
        }
        catch (Exception ex)
        {
            CappedLog.Info("power.once", $"缓存写入失败（忽略）：{ex.GetType().Name}: {ex.Message}");
        }
    }
}
