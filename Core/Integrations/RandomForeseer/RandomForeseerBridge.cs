using System.Reflection;
using System.Runtime.CompilerServices;

using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using Together.Core.Diagnostics;
using Together.Core.Foundation;
using Together.Core.Settings;

namespace Together.Core.Integrations.RandomForeseer;

/// <summary>
/// 「随机数预测」（RandomForeseer）联动：类型解析 + 每次预测的会话表。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要联动</b>：那个 mod 的预测内核是一台独立模拟器，它<b>按玩家</b>把真实战斗状态快照成预测副本
/// （<c>SimPlayerCombatState</c> / <c>SimCardPile</c> / <c>SimOrbQueue</c>）。
/// 而共享身体下同组成员的四口战斗牌堆是<b>同一个实例</b>、球位也是<b>同一口队列</b>：
/// 模拟器会把同一副牌快照成两份副本、把同一口球队列算两遍 ——
/// 表现就是"抽牌预测连续都是第一张牌""充能球伤害预测不准"。
/// </para>
/// <para>
/// <b>做法（不碰对方源码、不猜私有字段名）</b>：在<b>模拟状态构造的那一刻</b>，按"这口真实牌堆 / 球队列"
/// 取出<b>本次预测唯一的那份模拟副本</b>写回它的字段；同一台模拟器里所有成员因此共用同一份副本。
/// 字段名用"名字里含属性名"在运行时找（C# 的 <c>field</c> 关键字生成 <c>&lt;Prop&gt;k__BackingField</c>），
/// 找不到就只跳过那一项并记一条日志 —— 最坏情况退回"预测不准"，绝不影响对局。
/// </para>
/// <para>
/// 会话（"本次预测"）用对方 <c>CombatPredictionSimulator</c> 的实例标识：它的构造函数在这里登记
/// "当前线程正在跑的模拟器"，之后懒创建的模拟状态都落进同一张表。
/// </para>
/// </remarks>
internal static class RandomForeseerBridge
{
    private const string SimulationNamespace = "RandomForeseer.RandomForeseerCode.InCombat.Simulation";

    /// <summary>补丁类别：确认对方加载后才挂（见 <see cref="RandomForeseerInstaller" />）。</summary>
    public const string PatchCategory = "together.RandomForeseer";

    /// <summary>RandomForeseer 的清单 id。</summary>
    public const string ModId = "RandomForeseer";

    public static Type? SimulatorType { get; private set; }

    public static Type? PlayerStateType { get; private set; }

    public static Type? SimCardPileType { get; private set; }

    public static Type? SimOrbQueueType { get; private set; }

    public static Type? CardPileUtilsType { get; private set; }

    private static bool _resolved;

    private static bool _resolvedOk;

    /// <summary>联动开关（设置页「兼容性」节，默认开）+ 本局真的成组了。</summary>
    public static bool Enabled =>
        TogetherPair.IsActive && TogetherSettingsSync.EffectiveCompatRandomForeseerSync;

    /// <summary>按名字解析对方的类型；缺关键类型就整体不启用（没装 / 版本对不上）。</summary>
    public static bool TryResolve()
    {
        if (_resolved)
        {
            return _resolvedOk;
        }

        _resolved = true;

        try
        {
            SimulatorType = AccessTools.TypeByName($"{SimulationNamespace}.CombatPredictionSimulator");
            PlayerStateType = AccessTools.TypeByName($"{SimulationNamespace}.SimPlayerCombatState");
            SimOrbQueueType = AccessTools.TypeByName($"{SimulationNamespace}.SimOrbQueue");
            SimCardPileType = AccessTools.TypeByName("RandomForeseer.RandomForeseerCode.Common.SimCardPile");
            CardPileUtilsType = AccessTools.TypeByName("RandomForeseer.RandomForeseerCode.InCombat.CardPileUtils");

            _resolvedOk = SimulatorType is not null
                          && PlayerStateType is not null
                          && SimCardPileType is not null
                          && SimOrbQueueType is not null;

            CappedLog.Info(
                "rf.bridge",
                _resolvedOk
                    ? "找到 RandomForeseer 的模拟器类型 → 联动补丁可用"
                    : "找不到 RandomForeseer 的模拟器类型（联动补丁不启用，预测保持原样）");
        }
        catch (Exception ex)
        {
            _resolvedOk = false;
            CappedLog.Info("rf.bridge", $"解析 RandomForeseer 类型失败（联动不启用）：{ex.GetType().Name}: {ex.Message}");
        }

        return _resolvedOk;
    }

    // ======================================================================
    // 每次预测的会话表
    // ======================================================================

    /// <summary>一台模拟器（= 一次预测）里"真实实例 → 唯一模拟副本"的映射。</summary>
    internal sealed class Session
    {
        public Dictionary<object, object> Piles { get; } = new(ReferenceEqualityComparer.Instance);

        public Dictionary<object, object> OrbQueues { get; } = new(ReferenceEqualityComparer.Instance);

        /// <summary>这次预测里已经跑过"回合末球触发"的那口队列。</summary>
        public HashSet<object> OrbTriggers { get; } = new(ReferenceEqualityComparer.Instance);
    }

    private static readonly ConditionalWeakTable<object, Session> Sessions = new();

    [ThreadStatic]
    private static object? _currentSimulator;

    /// <summary>模拟器构造时登记"当前线程正在跑的这次预测"。</summary>
    public static void BeginSession(object simulator)
    {
        _currentSimulator = simulator;
    }

    /// <summary>当前这次预测的会话（拿不到就返回 null，调用方直接跳过）。</summary>
    public static Session? CurrentSession =>
        _currentSimulator is null ? null : Sessions.GetOrCreateValue(_currentSimulator);

    /// <summary>指定模拟器所属的会话。</summary>
    public static Session? SessionOf(object? simulator) =>
        simulator is null ? null : Sessions.GetOrCreateValue(simulator);

    // ======================================================================
    // 字段查找 + 唯一副本
    // ======================================================================

    private static readonly Dictionary<string, FieldInfo?> FieldCache = [];

    /// <summary>
    /// 找属性对应的字段：优先 <c>&lt;属性名&gt;k__BackingField</c>，其次"名字里含属性名"（忽略大小写），
    /// 并且要求字段类型能和 <paramref name="expectedType" /> 对上 —— 对方换命名风格也能认出来，认不出就跳过这一项。
    /// </summary>
    public static FieldInfo? BackingField(Type type, string property, Type? expectedType)
    {
        var key = $"{type.FullName}|{property}|{expectedType?.Name}";

        lock (FieldCache)
        {
            if (FieldCache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        FieldInfo? found = null;
        try
        {
            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            foreach (var field in fields)
            {
                if (!field.Name.Equals($"<{property}>k__BackingField", StringComparison.Ordinal))
                {
                    continue;
                }

                if (expectedType is null || expectedType.IsAssignableFrom(field.FieldType))
                {
                    found = field;
                    break;
                }
            }

            if (found is null)
            {
                foreach (var field in fields)
                {
                    if (!field.Name.Contains(property, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (expectedType is not null && !expectedType.IsAssignableFrom(field.FieldType))
                    {
                        continue;
                    }

                    found = field;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            CappedLog.Info("rf.bridge", $"读 {type.Name} 的字段失败：{ex.GetType().Name}: {ex.Message}");
        }

        if (found is null)
        {
            // 版本对不上就只跳过这一项：预测退回原样，不影响任何对局逻辑。
            CappedLog.Info("rf.bridge", $"{type.Name}.{property} 的字段找不到（这一项跳过）");
        }

        lock (FieldCache)
        {
            FieldCache[key] = found;
        }

        return found;
    }

    /// <summary>取"这口真实牌堆在本次预测里的唯一模拟副本"（没有就调对方的构造函数造一份）。</summary>
    public static object? CanonicalPile(Session session, object livePile)
    {
        if (session.Piles.TryGetValue(livePile, out var existing))
        {
            return existing;
        }

        if (SimCardPileType is null)
        {
            return null;
        }

        try
        {
            var created = Activator.CreateInstance(SimCardPileType, [livePile]);
            if (created is not null)
            {
                session.Piles[livePile] = created;
            }

            return created;
        }
        catch (Exception ex)
        {
            CappedLog.Info("rf.bridge", $"建模拟牌堆失败（这一项跳过）：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>取"这口真实球队列在本次预测里的唯一模拟副本"。</summary>
    public static object? CanonicalOrbQueue(Session session, object liveQueue)
    {
        if (session.OrbQueues.TryGetValue(liveQueue, out var existing))
        {
            return existing;
        }

        if (SimOrbQueueType is null)
        {
            return null;
        }

        try
        {
            var created = Activator.CreateInstance(SimOrbQueueType, [liveQueue]);
            if (created is not null)
            {
                session.OrbQueues[liveQueue] = created;
            }

            return created;
        }
        catch (Exception ex)
        {
            CappedLog.Info("rf.bridge", $"建模拟球队列失败（这一项跳过）：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>把某个模拟战斗状态的四口共享战斗堆 + 球队列，换成本次预测里的唯一副本。</summary>
    /// <remarks>
    /// 手牌<b>不换</b>（手牌没共享，和游戏里一致）；只换"真实实例是同一份"的那些 ——
    /// 键就是真实牌堆实例本身，所以"这个成员的这口堆本来就没共享"时自然各用各的副本。
    /// </remarks>
    public static void AliasSharedPiles(object playerState, PlayerCombatState live)
    {
        if (CurrentSession is not { } session)
        {
            return;
        }

        var type = playerState.GetType();

        Alias(type, playerState, session, "DrawPile", live.DrawPile);
        Alias(type, playerState, session, "DiscardPile", live.DiscardPile);
        Alias(type, playerState, session, "ExhaustPile", live.ExhaustPile);
        Alias(type, playerState, session, "PlayPile", live.PlayPile);
        AliasOrbQueue(type, playerState, session, live.OrbQueue);
    }

    private static void Alias(Type type, object instance, Session session, string property, object livePile)
    {
        if (BackingField(type, property, SimCardPileType) is not { } field)
        {
            return;
        }

        if (CanonicalPile(session, livePile) is not { } canonical)
        {
            return;
        }

        field.SetValue(instance, canonical);
    }

    private static void AliasOrbQueue(Type type, object instance, Session session, object liveQueue)
    {
        if (BackingField(type, "OrbQueue", SimOrbQueueType) is not { } field)
        {
            return;
        }

        if (CanonicalOrbQueue(session, liveQueue) is not { } canonical)
        {
            return;
        }

        field.SetValue(instance, canonical);
    }
}
