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
    /// <summary>普通键的上限。</summary>
    /// <remarks>高频键（逐张牌的事件之类）不走这里。</remarks>
    private const int DefaultLimit = 120;

    /// <summary>"关键证据类"键的上限。</summary>
    /// <remarks>
    /// 排查 <c>checksum</c> 分歧时，这些行就是唯一证据 —— 120 条在长局里很容易被打满
    /// （实测 <c>owner.widen</c> 在分歧发生前就满了，导致 chk=62 前后一条证据都没有）。
    /// 这些键每秒最多也就几条，放到 2000 既够用又不会淹没日志。
    /// </remarks>
    private const int EvidenceLimit = 2000;

    /// <summary>关键证据类键的前缀。</summary>
    private static readonly string[] EvidenceKeyPrefixes =
    [
        "steal.",   // 草蜢偷牌（改判归属 / 显示）
        "power.",   // 能力镜像（instance/mirror/relational/once/payload/second_hit）
        "owner.",   // 归属放宽（owner.widen）
        "own.",     // owner 归一 / 进手牌对齐 / owner.skip / widen
        "order.",   // 顺序指纹
        "event.",   // 事件并发守卫 / 注册
        "compat.",  // 兼容层结论
        "coop.",    // 名单 / 大厅
        "arm.",     // 配对激活
        "rf.",      // 随机数预测联动
        "drift.",   // 归属写入取证（AddCard / 卡组堆增删 / 分布变化）
    ];

    private static int LimitOf(string key)
    {
        foreach (var prefix in EvidenceKeyPrefixes)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                return EvidenceLimit;
            }
        }

        return DefaultLimit;
    }

    private static readonly Dictionary<string, int> Counts = [];

    public static void Info(string key, string message)
    {
        var limit = LimitOf(key);

        lock (Counts)
        {
            var count = Counts.TryGetValue(key, out var current) ? current : 0;
            if (count >= limit)
            {
                return;
            }

            Counts[key] = count + 1;

            // 带上"当前是第几个校验和"：两端自动对账时，同一 chk 下的这些诊断就能一一对上。
            Log.Info(
                $"[together] {SelfCheck.Tag(message)}"
                + $"{(count + 1 == limit ? "（后续同类日志已省略）" : string.Empty)}");
        }
    }
}
