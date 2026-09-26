using MegaCrit.Sts2.Core.Logging;
using Together.Core.Common;
using Together.Core.Foundation;
using Together.Core.Ui;

namespace Together.Core.Diagnostics;

/// <summary>逐条取证日志的开关（<b>默认静默</b>）：只有排查归属漂移时才需要打开。</summary>
/// <remarks>
/// <para>
/// <b>为什么单独一档</b>：<c>drift.addcard</c> / <c>drift.deck</c> / <c>own.assign</c> 是"逐张牌"级别的取证 ——
/// 一次房间切换就是"34 张删 + 34 张加 + 复原 32 条"，一局下来上千行。它们的价值只在"复现某个 bug 时取证"，
/// 不适合常驻玩家日志（真出问题时，让玩家带上环境变量重跑一次即可）。
/// </para>
/// <para>
/// <b>开关</b>：环境变量 <c>TOGETHER_DRIFT=1</c> 打开（<see cref="Enabled"/> 在首次访问时读一次，之后走缓存 ——
/// 这些探针挂在"每次牌堆增删"上，不能每次去读环境变量）。
/// 静默不等于不装补丁：补丁照常挂着，只是 <see cref="Info"/> 直接返回，代价是一次布尔判断。
/// </para>
/// <para>
/// <b>默认仍然可见</b>的是"聚合/罕见"那几行（它们本身就是信号，不是流水账）：
/// <c>drift.watch</c> 的<b>分布变化</b>行、<c>own.repair</c> 的"还原 N 张 / 钉锚点"行、
/// <c>own.transform</c> 的变牌对齐行、<c>own.deck</c> 的重建后分布行。
/// </para>
/// </remarks>
internal static class DriftLog
{
    /// <summary>环境变量名（<c>1</c> = 打开逐条取证）。</summary>
    private const string EnvName = "TOGETHER_DRIFT";

    private static bool? _enabled;

    /// <summary>逐条取证是否输出（默认关）。</summary>
    public static bool Enabled
    {
        get
        {
            if (_enabled is { } cached)
            {
                return cached;
            }

            var value = string.Equals(
                Environment.GetEnvironmentVariable(EnvName),
                "1",
                StringComparison.Ordinal);

            _enabled = value;
            return value;
        }
    }

    /// <summary>逐条取证（默认不输出；开了 <c>TOGETHER_DRIFT=1</c> 才走 <see cref="CappedLog"/>，同样有上限）。</summary>
    public static void Info(string key, string message)
    {
        if (!Enabled)
        {
            return;
        }

        CappedLog.Info(key, message);
    }
}
