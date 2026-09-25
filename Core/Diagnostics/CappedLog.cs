using MegaCrit.Sts2.Core.Logging;
using Together.Core.Common;
using Together.Core.Foundation;
using Together.Core.Ui;

namespace Together.Core.Diagnostics;
/// <summary>有上限的普通日志。</summary>
/// <remarks>
/// 和 <see cref="SelfCheck" />（要开环境变量才出声）不同：这些是"功能是否真的在跑"的关键证据，
/// 需要默认就能在 log 里看到；但每个键最多打 <see cref="Limit" /> 条，避免刷屏。
/// 排查完可以整体降级成 <see cref="SelfCheck.Write" />，或者把 <see cref="Limit" /> 调小/清空。
/// </remarks>
internal static class CappedLog
{
    /// <summary>每个键最多打多少条。</summary>
    /// <remarks>
    /// 排查房间流程时 30 条很容易在长局里被打满（实测"奖励屏：取走了一项奖励"很早就到上限，
    /// 导致后半段没有证据可看），所以放到 120。真正高频的键（比如逐张牌的事件）不走这里。
    /// </remarks>
    private const int Limit = 120;

    private static readonly Dictionary<string, int> Counts = [];

    public static void Info(string key, string message)
    {
        lock (Counts)
        {
            var count = Counts.TryGetValue(key, out var current) ? current : 0;
            if (count >= Limit)
            {
                return;
            }

            Counts[key] = count + 1;

            // 带上"当前是第几个校验和"：两端自动对账时，同一 chk 下的这些诊断就能一一对上。
            Log.Info(
                $"[together] {SelfCheck.Tag(message)}"
                + $"{(count + 1 == Limit ? "（后续同类日志已省略）" : string.Empty)}");
        }
    }
}
