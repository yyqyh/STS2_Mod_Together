using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Diagnostics;

using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using STS2RitsuLib;
using STS2RitsuLib.Utils.Persistence;
using Together;

namespace Together.Core.Patches.Deck;

/// <summary>
/// 通用兼容层：放行<b>任何自己重写了"同批 owner 必须一致"这条校验的 mod</b>。
/// </summary>
/// <remarks>
/// <para>
/// 背景：本体的批量 <c>CardPileCmd.Add</c> 有一条硬校验"同一次调用里所有牌 owner 必须一致"，
/// 它的前提是"每个玩家的牌堆各自独立"。共享牌库下这个前提不成立——共享的弃牌堆／抽牌堆本来
/// 就该同时装着两个人的牌，而<b>洗牌</b>正好是"把弃牌堆混进抽牌堆"的批量 Add。
/// 本体那一处已经由 <see cref="DifferentOwnersCheckPatch" /> 用转译器打掉；
/// 但<b>别的 mod 可能自己复制了同一份校验</b>（实测：RandomForeseer 的洗牌预测模拟
/// <c>CombatPredictionSimulator.AddToPile</c>），它们会在共生体局里抛异常。
/// </para>
/// <para>
/// 判据刻意<b>不写任何 mod 名字</b>：只要某个方法的 IL 里出现字符串常量
/// <c>different owners</c>，就说明它重写了这条校验 —— 而这条校验在共享牌库下永远是错的结论，
/// 所以按同一条规则放行即可。判据来自<b>代码内容</b>，不来自"是谁"。
/// </para>
/// <para>
/// 具体动作：把 <c>ldstr "…different owners…"</c> 后面的
/// <c>newobj InvalidOperationException</c> 换成 <c>pop</c>（吃掉已压栈的字符串、保持栈平衡），
/// 再把随后的 <c>throw</c> 换成 <c>nop</c>（让执行流继续往下走）。
/// 与本体那条补丁的改写方式完全一致，只是目标是扫描出来的。
/// </para>
/// <para>
/// 可审计：每放行一个方法都会在启动日志里打一行"兼容放行：程序集 / 类型 / 方法"。
/// 只扫非系统程序集（本体 <c>sts2</c> 已被上面那条补丁覆盖，不重复扫），
/// 且已扫过的程序集会被记住，重复调用只补扫新加载的。
/// </para>
/// </remarks>
internal static class SameOwnerCheckCompat
{
    /// <summary>错误信息的关键片段（与本体一致）。</summary>
    private const string Fragment = "different owners";

    /// <summary>明确跳过的程序集（简单名精确匹配）。</summary>
    private static readonly HashSet<string> IgnoredNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "together",      // 自己（本体的那一处由 DifferentOwnersCheckPatch 处理）
        "sts2",          // 本体：上面那条补丁已经覆盖
        "GodotSharp",
        "GodotSharpEditor",
        "0Harmony",
        "HarmonyLib",
        "mscorlib",
        "netstandard",
        "System",
        "System.Core",
    };

    /// <summary>按前缀跳过的程序集（框架 / 运行时 / 遥测等）。</summary>
    private static readonly string[] IgnoredPrefixes =
    [
        "System.",
        "Microsoft.",
        "Mono.",
        "Godot.",
        "JetBrains.",
        "Sentry",
        "Steamworks",
        "Newtonsoft",
        "Coverlet",
        "xunit",
    ];

    /// <summary>
    /// 判定规则版本。<b>以后新增判定片段时必须 +1</b>，好让旧缓存整份作废重扫。
    /// </summary>
    private const int RulesVersion = 1;

    private const string CacheKey = "hook_compat_cache";

    private const string CacheFileName = "together_hook_compat.json";

    private static readonly HashSet<string> ScannedAssemblies = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ReleasedKeys = new(StringComparer.OrdinalIgnoreCase);

    private static HarmonyLib.Harmony? _harmony;
    private static HookCompatCacheData? _cache;
    private static bool _cacheLoaded;

    /// <summary>
    /// 跨启动缓存：记住"哪些程序集已经验证过、结论是什么"。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>键是程序集的 MVID</b>（模块版本 id）—— 同一个二进制永远是同一个 MVID，
    /// 而 mod 只要重新编译过 MVID 就会变。所以：
    /// </para>
    /// <list type="bullet">
    /// <item><description>下次启动遇到同样的二进制 → 直接跳过扫描（<c>Tokens</c> 为空时连放行动作都不用做）。</description></item>
    /// <item><description>mod 更新过 → MVID 变了 → 缓存自动失效、重新扫描。<b>因此不需要"重新验证"按钮。</b></description></item>
    /// <item><description>想手动强制重扫：删掉 <c>together_hook_compat.json</c>（在 mod 数据目录里）即可。</description></item>
    /// </list>
    /// <para>
    /// 需要放行的程序集也不吃亏：记下的是<b>元数据 token</b>，下次直接 <c>ResolveMethod</c> 拿回方法去装补丁，
    /// 同样不用扫 IL。
    /// </para>
    /// </remarks>
    internal sealed class HookCompatCacheData
    {
        public int RulesVersion { get; set; }

        public List<HookCompatCacheEntry> Assemblies { get; set; } = [];

        public HookCompatCacheEntry? Find(string name)
        {
            return Assemblies.FirstOrDefault(
                e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        public void Upsert(HookCompatCacheEntry entry)
        {
            Assemblies.RemoveAll(e => string.Equals(e.Name, entry.Name, StringComparison.OrdinalIgnoreCase));
            Assemblies.Add(entry);
        }
    }

    /// <summary>单个程序集的验证结论。</summary>
    internal sealed class HookCompatCacheEntry
    {
        public string Name { get; set; } = string.Empty;

        /// <summary>验证时那个二进制的模块版本 id。</summary>
        public string Mvid { get; set; } = string.Empty;

        /// <summary>需要放行的方法的元数据 token；<b>空表示这个程序集干净</b>，下次可直接跳过。</summary>
        public List<int> Tokens { get; set; } = [];

        /// <summary>人工核对用的描述（程序集 → 类型.方法）。</summary>
        public List<string> Released { get; set; } = [];
    }

    /// <summary>
    /// 扫描并放行。可以重复调用：只会补扫"上次之后新加载的程序集"。
    /// </summary>
    /// <param name="reason">触发来源（只用于日志）。</param>
    public static void Apply(HarmonyLib.Harmony harmony, string reason)
    {
        _harmony = harmony;
        Apply(reason);
    }

    /// <summary>重复调用入口（复用首次传入的 Harmony 实例）。</summary>
    public static void Apply(string reason)
    {
        var harmony = _harmony;
        if (harmony is null)
        {
            return;
        }

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var scanned = 0;
            var reused = 0;
            var released = 0;

            var cache = LoadCache();
            var resetCache = cache is not null && cache.RulesVersion != RulesVersion;
            var updates = new List<HookCompatCacheEntry>();

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!ShouldScan(assembly))
                {
                    continue;
                }

                var key = KeyOf(assembly);
                if (!ScannedAssemblies.Add(key))
                {
                    continue;
                }

                var mvid = MvidOf(assembly);

                // ① 缓存命中：同一个二进制（MVID 一致）→ 上次的结论依然成立，连 IL 都不用扫。
                if (!resetCache
                    && cache is not null
                    && mvid is not null
                    && cache.Find(key) is { } hit
                    && hit.Mvid == mvid
                    && TryReplay(harmony, assembly, hit, reason, out var replayed))
                {
                    released += replayed;
                    reused++;
                    continue;
                }

                // ② 未命中（新装的 mod / mod 更新过 → MVID 变了 / 缓存不可用）→ 老老实实扫一遍。
                scanned++;
                var tokens = new List<int>();
                var descriptions = new List<string>();

                foreach (var method in Scan(assembly))
                {
                    if (Release(harmony, method, reason))
                    {
                        released++;
                        tokens.Add(TokenOf(method));
                        descriptions.Add(Describe(method));
                    }
                }

                if (mvid is not null)
                {
                    updates.Add(new HookCompatCacheEntry
                    {
                        Name = key,
                        Mvid = mvid,
                        Tokens = tokens,
                        Released = descriptions,
                    });
                }
            }

            stopwatch.Stop();

            if (cache is not null && (resetCache || updates.Count > 0))
            {
                SaveCache(resetCache, updates);
            }

            if (scanned > 0 || reused > 0)
            {
                Log.Info(
                    $"[together] 兼容放行扫描（{reason}）：新扫 {scanned} 个程序集、"
                    + $"复用已验证缓存 {reused} 个、放行 {released} 个方法，耗时 {stopwatch.ElapsedMilliseconds}ms");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[together] 兼容放行扫描失败（{reason}）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 按缓存里记下的元数据 token 直接放行（不扫 IL）。
    /// </summary>
    /// <returns>缓存是否可用；false 表示这个程序集要重扫。</returns>
    private static bool TryReplay(
        HarmonyLib.Harmony harmony,
        Assembly assembly,
        HookCompatCacheEntry entry,
        string reason,
        out int released)
    {
        released = 0;

        // 上次判定"干净" → 直接跳过，这就是缓存的主要收益。
        if (entry.Tokens.Count == 0)
        {
            return true;
        }

        var resolved = new List<MethodBase>(entry.Tokens.Count);
        foreach (var token in entry.Tokens)
        {
            if (TryResolve(assembly, token) is not { } method)
            {
                // 理论上有 MVID 兜着不会发生；真发生就老实重扫，别让放行漏掉。
                return false;
            }

            resolved.Add(method);
        }

        foreach (var method in resolved)
        {
            if (Release(harmony, method, reason))
            {
                released++;
            }
        }

        return true;
    }

    private static HookCompatCacheData? LoadCache()
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
                    defaultFactory: () => new HookCompatCacheData(),
                    autoCreateIfMissing: true);
            }

            RitsuLibFramework.GetDataStore(Const.ModId).InitializeGlobal();
            _cache = RitsuLibFramework.GetDataStore(Const.ModId).Get<HookCompatCacheData>(CacheKey);
        }
        catch (Exception ex)
        {
            _cache = null;
            Log.Warn(
                $"[together] 兼容放行缓存不可用（本次仍会完整扫描，只是失去跨启动加速）："
                + $"{ex.GetType().Name}: {ex.Message}");
        }

        return _cache;
    }

    private static void SaveCache(bool reset, List<HookCompatCacheEntry> updates)
    {
        try
        {
            var store = RitsuLibFramework.GetDataStore(Const.ModId);

            store.Modify<HookCompatCacheData>(CacheKey, data =>
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

            var dirtyNames = string.Join(", ", updates.Where(u => u.Tokens.Count > 0).Select(u => u.Name));
            Log.Info(
                "[together] 兼容放行缓存已更新："
                + $"{updates.Count} 个程序集已验证"
                + (string.IsNullOrEmpty(dirtyNames) ? "（全部干净）" : $"（需要放行：{dirtyNames}）")
                + $"；文件 {CacheFileName}");
        }
        catch (Exception ex)
        {
            Log.Warn($"[together] 兼容放行缓存写入失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string? MvidOf(Assembly assembly)
    {
        try
        {
            return assembly.ManifestModule.ModuleVersionId.ToString("N");
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int TokenOf(MethodBase method)
    {
        try
        {
            return method.MetadataToken;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static MethodBase? TryResolve(Assembly assembly, int token)
    {
        if (token == 0)
        {
            return null;
        }

        try
        {
            return assembly.ManifestModule.ResolveMethod(token);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>放行一个方法；返回是否真的动了它。</summary>
    private static bool Release(HarmonyLib.Harmony harmony, MethodBase method, string reason)
    {
        var key = KeyOf(method);
        if (!ReleasedKeys.Add(key))
        {
            return false;
        }

        try
        {
            harmony.Patch(
                method,
                transpiler: new HarmonyMethod(typeof(SameOwnerCheckCompat), nameof(Transpiler)));

            Log.Info(
                $"[together] 兼容放行：{Describe(method)}"
                + $"（它自己重写了\"同批 owner 必须一致\"的校验；触发={reason}）");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn(
                $"[together] 兼容放行失败：{Describe(method)} → {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>扫描一个程序集里所有"IL 里出现目标字符串常量"的方法。</summary>
    private static IEnumerable<MethodBase> Scan(Assembly assembly)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t is not null).ToArray()!;
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (var type in types)
        {
            MethodBase[] methods;
            try
            {
                methods = type.GetMethods(AccessTools.all | BindingFlags.DeclaredOnly);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var method in methods)
            {
                if (ContainsFragment(method))
                {
                    yield return method;
                }
            }
        }
    }

    /// <summary>
    /// 粗查：方法的 IL 里有没有目标字符串常量。
    /// </summary>
    /// <remarks>
    /// 刻意不解析成指令序列（那样要给每个方法建列表，几千个方法会明显拖慢启动）：
    /// 直接扫原始 IL 字节里的 <c>ldstr</c>（0x72）操作码，用 <c>ResolveString</c> 取出它引用的字符串。
    /// 0x72 也可能只是别的指令的操作数字节，那种情况 <c>ResolveString</c> 会抛，吞掉即可
    /// （最坏是多解析一次指令序列，不影响正确性）。
    /// </remarks>
    private static bool ContainsFragment(MethodBase method)
    {
        try
        {
            var bytes = method.GetMethodBody()?.GetILAsByteArray();
            if (bytes is null)
            {
                return false;
            }

            for (var i = 0; i + 4 < bytes.Length; i++)
            {
                if (bytes[i] != 0x72)
                {
                    continue;
                }

                var token = BitConverter.ToInt32(bytes, i + 1);
                try
                {
                    var text = method.Module.ResolveString(token);
                    if (!string.IsNullOrEmpty(text)
                        && text.Contains(Fragment, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch (Exception)
                {
                    // 不是字符串 token，继续扫。
                }
            }
        }
        catch (Exception)
        {
            // 动态方法 / 没有方法体 / 元数据读不出来 —— 都当"没有"。
        }

        return false;
    }

    /// <summary>把目标字符串后面的 <c>newobj + throw</c> 改成 <c>pop + nop</c>。</summary>
    /// <remarks>
    /// 与本体那条补丁的区别：这里是"扫描出来的方法"，形状可能不完全是预期的那种，
    /// 所以<b>找不到就原样返回并打一行警告</b>，绝不抛异常打断其它 mod 的补丁安装。
    /// </remarks>
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var list = instructions.ToList();

        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].opcode != OpCodes.Ldstr
                || list[i].operand is not string text
                || !text.Contains(Fragment, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            for (var j = i + 1; j < Math.Min(i + 12, list.Count); j++)
            {
                if (list[j].opcode != OpCodes.Newobj)
                {
                    continue;
                }

                for (var k = j + 1; k < Math.Min(j + 4, list.Count); k++)
                {
                    if (list[k].opcode != OpCodes.Throw)
                    {
                        continue;
                    }

                    // 命中：复制一份再改写，保留标签/异常块（这两条指令可能是跳转目标）。
                    var patched = new List<CodeInstruction>(list);
                    patched[j] = new CodeInstruction(OpCodes.Pop)
                    {
                        labels = list[j].labels,
                        blocks = list[j].blocks,
                    };
                    patched[k] = new CodeInstruction(OpCodes.Nop)
                    {
                        labels = list[k].labels,
                        blocks = list[k].blocks,
                    };
                    return patched;
                }
            }
        }

        Log.Warn("[together] 兼容放行：目标方法里没找到 \"ldstr + newobj + throw\" 的常规形状，已原样跳过。");
        return list;
    }

    private static bool ShouldScan(Assembly assembly)
    {
        try
        {
            if (assembly.IsDynamic)
            {
                return false;
            }

            var name = assembly.GetName().Name ?? string.Empty;
            if (IgnoredNames.Contains(name))
            {
                return false;
            }

            return !IgnoredPrefixes.Any(
                prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string KeyOf(Assembly assembly)
    {
        try
        {
            return assembly.FullName ?? assembly.GetName().Name ?? Guid.NewGuid().ToString();
        }
        catch (Exception)
        {
            return Guid.NewGuid().ToString();
        }
    }

    private static string KeyOf(MethodBase method)
    {
        try
        {
            return $"{method.DeclaringType?.FullName}::{method.Name}";
        }
        catch (Exception)
        {
            return method.Name;
        }
    }

    private static string Describe(MethodBase method)
    {
        try
        {
            var assembly = method.DeclaringType?.Assembly.GetName().Name ?? "?";
            return $"{assembly} → {method.DeclaringType?.FullName}.{method.Name}";
        }
        catch (Exception)
        {
            return method.Name;
        }
    }
}
