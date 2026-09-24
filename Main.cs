using System.Reflection;

using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using STS2RitsuLib.Interop;
using STS2RitsuLib;
using Together.Core.Content;
using Together.Core.Patches.Deck;
using Together.Core.Settings;

namespace Together;

    /// <summary>Mod 入口。</summary>
    /// <remarks>
    /// 这里只做三件事：建 logger、把本程序集交给 RitsuLib 做自动注册、装 Harmony 补丁。
    /// 内容（角色 / 卡 / 遗物 / 能力 / 设置页）靠 <c>[RegisterXxx]</c> 特性自动注册，
    /// 不需要在 <see cref="Initialize" /> 里逐个登记——所以这个文件应该尽量不动。
    /// </remarks>
    [ModInitializer(nameof(Initialize))]
    public static class Main
    {
        /// <summary>必须与清单 id、DLL 名、PCK 名一致（直接取 <see cref="Const.ModId" />，
        /// 避免两处各写一份字面量后漂移）。</summary>
        public const string ModId = Const.ModId;

        public static Logger Logger { get; private set; } = null!;

        /// <summary>本 mod 的 Harmony 实例。后加的补丁（如扫描出来的第三方方法）也走它，
        /// 保证所有改动都在同一个 id 下可追踪。</summary>
        public static Harmony Patcher { get; private set; } = null!;

        public static void Initialize()
        {
            var assembly = Assembly.GetExecutingAssembly();
            Logger = RitsuLibFramework.CreateLogger(ModId);
            RitsuLibFramework.EnsureGodotScriptsRegistered(assembly, Logger);
            ModTypeDiscoveryHub.RegisterModAssembly(ModId, assembly);

            // 设置要在任何"读设置"的代码之前就位（共享角色目标就是从设置里读的）。
            TogetherSettingsStore.Initialize();
            TogetherSettingsSync.Initialize();
            TogetherModSettingsPage.Register();
            SymbiosisMembers.Initialize();

            ApplyPatches(assembly);

            // 通用兼容层：放行"任何自己重写了'同批 owner 必须一致'校验的 mod"。
            // 判据是方法 IL 里有没有那条错误信息，不看 mod 名字 —— 见 SameOwnerCheckCompat。
            SameOwnerCheckCompat.Apply(Patcher, "init");
        }

        /// <summary>逐个补丁类安装，失败只废掉那一个类。</summary>
        /// <remarks>
        /// 不能用 <c>Harmony.PatchAll</c>：它遇到第一个失败就抛异常，
        /// 而 <c>Initialize</c> 抛异常会让**整个 mod 初始化失败**——
        /// 更糟的是 PatchAll 不是事务性的，已经装上的补丁会留着，
        /// 于是变成"一半功能正常、一半功能消失"这种最难查的状态。
        /// （实测踩过：一个 `__2` 参数索引写错，整套补丁全废，界面表现成"卡组不共享了"。）
        /// </remarks>
        private static void ApplyPatches(Assembly assembly)
        {
            var harmony = new Harmony(ModId);
            Patcher = harmony;
            var applied = 0;
            var failed = 0;

            foreach (var type in assembly.GetTypes())
            {
                if (type.GetCustomAttributes(typeof(HarmonyPatch), true).Length == 0)
                {
                    continue;
                }

                try
                {
                    harmony.CreateClassProcessor(type).Patch();
                    applied++;
                }
                catch (Exception ex)
                {
                    failed++;
                    // Harmony 会把真正的原因包在内层异常里（Transpiler 抛出的匹配失败提示就在那儿），
                    // 只打 ex.Message 会看到一串无用的"Patching exception in method …"。
                    Log.Error($"[{ModId}] 补丁类 {type.Name} 应用失败：{Describe(ex)}");
                }
            }

            // 把数量打进日志：单个 PatchAll 成功不代表"打到了我们想打的方法"。
            Log.Info(
                $"[{ModId}] initialized v{Const.Version}; patch classes applied={applied} failed={failed}; "
                + $"Harmony patched {harmony.GetPatchedMethods().Count()} method(s).");
        }

        /// <summary>把异常链摊平成一行，便于在日志里直接看到补丁失败的真实原因。</summary>
        private static string Describe(Exception ex)
        {
            var parts = new List<string>();

            for (Exception? current = ex; current is not null; current = current.InnerException)
            {
                parts.Add($"{current.GetType().Name}: {current.Message}");
            }

            return string.Join("  ←  ", parts);
        }
    }
