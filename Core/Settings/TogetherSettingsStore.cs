using STS2RitsuLib.Utils.Persistence;
using STS2RitsuLib;
using Together.Core.Combat;
using Together;

namespace Together.Core.Settings;

/// <summary>
/// 设置的持久化入口（RitsuLib 数据存储）。
/// </summary>
/// <remarks>
/// <para>
/// 读设置的人不该关心文件在哪，统一走 <see cref="SharedCharacterKey" />——
/// 它每次都会去数据存储里取当前值，所以<b>设置界面一改就立刻生效</b>（不需要重启游戏）。
/// </para>
/// <para>
/// 存储注册必须在 <c>BeginModDataRegistration</c> 作用域里做（RitsuLib 用这个把它归到本 mod 名下），
/// 并且只做一次；<see cref="Initialize" /> 是幂等的。
/// </para>
/// </remarks>
internal static class TogetherSettingsStore
{
    internal const string DataKey = "settings";

    /// <summary>存档登记最多保留多少条（按插入顺序淘汰最旧的）。</summary>
    public const int SymbioticRunHistory = 10;

    private const string FileName = "together_settings.json";

    private static bool _initialized;

    /// <summary>本机设置里的"是否开启共生体"。</summary>
    /// <remarks>
    /// 功能上请一律读 <c>TogetherSettingsSync.EffectiveSymbiosisEnabled</c>（联机时以主机为准），
    /// 只有那个同步层才该直接读这里。
    /// </remarks>
    public static bool SymbiosisEnabled
    {
        get
        {
            Initialize();

            var settings = RitsuLibFramework.GetDataStore(Const.ModId).Get<TogetherSettings>(DataKey);
            return settings is not null && settings.SymbiosisEnabled;
        }
    }

    /// <summary>本机设置里的"开局是否把回声的初始卡组复制进共享卡组"。</summary>
    /// <remarks>同 <see cref="SymbiosisEnabled" />：功能上请读同步层。</remarks>
    public static bool MergeStarterDecks
    {
        get
        {
            Initialize();

            var settings = RitsuLibFramework.GetDataStore(Const.ModId).Get<TogetherSettings>(DataKey);
            return settings is null || settings.MergeStarterDecks;
        }
    }

    /// <summary>本机设置里的"血量上限提升百分比"（已夹到 0~100）。</summary>
    public static int HpBonusPercent
    {
        get
        {
            Initialize();

            var settings = RitsuLibFramework.GetDataStore(Const.ModId).Get<TogetherSettings>(DataKey);
            return Math.Clamp(settings?.HpBonusPercent ?? 0, 0, 100);
        }
    }

    /// <summary>
    /// "合作人数上限"。<b>历史字段：已不参与判定</b>，固定返回硬上限 <see cref="TogetherPair.MaxMembers" />。
    /// </summary>
    /// <remarks>
    /// 以前读的是设置里那一项（2~4）。现在没有名额了，<b>不再读存档里的旧值</b> ——
    /// 否则老配置里存着的 2 会被照搬出去（设置页已经改不掉它），对外看起来就是个"改不动的假设置"。
    /// 字段保留只是为了联机快照（<c>TogetherSettingsSync.Snapshot</c>）的结构不变。
    /// </remarks>
    public static int GroupSize => TogetherPair.MaxMembers;

    /// <summary>本机设置里的"是否共享金币"。</summary>
    public static bool ShareGold
    {
        get
        {
            Initialize();

            var settings = RitsuLibFramework.GetDataStore(Const.ModId).Get<TogetherSettings>(DataKey);
            return settings is null || settings.ShareGold;
        }
    }

    /// <summary>记住"这个种子是共生体局，成员是这些 netId"。</summary>
    public static void RememberSymbioticRun(string? seed, IEnumerable<ulong> memberIds)
    {
        if (string.IsNullOrWhiteSpace(seed))
        {
            return;
        }

        Initialize();

        var value = string.Join(",", memberIds);
        var store = RitsuLibFramework.GetDataStore(Const.ModId);

        store.Modify<TogetherSettings>(DataKey, settings =>
        {
            settings.SymbioticRuns ??= [];
            settings.SymbioticRuns[seed] = value;

            while (settings.SymbioticRuns.Count > SymbioticRunHistory)
            {
                settings.SymbioticRuns.Remove(settings.SymbioticRuns.Keys.First());
            }
        });

        store.Save(DataKey);
    }

    /// <summary>读档时按种子找回共生体成员；没登记过返回 <c>null</c>。</summary>
    public static ulong[]? FindSymbioticRun(string? seed)
    {
        if (string.IsNullOrWhiteSpace(seed))
        {
            return null;
        }

        Initialize();

        var settings = RitsuLibFramework.GetDataStore(Const.ModId).Get<TogetherSettings>(DataKey);
        if (settings?.SymbioticRuns is not { } map
            || !map.TryGetValue(seed, out var value)
            || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var ids = new List<ulong>();
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (ulong.TryParse(part.Trim(), out var id) && id != 0UL)
            {
                ids.Add(id);
            }
        }

        return ids.Count > 0 ? ids.ToArray() : null;
    }

    /// <summary>把"共生体开关"写成本值并落盘（解绑时用；设置页仍走原来的绑定通道）。</summary>
    /// <remarks>
    /// 给"代码里要关掉这个开关"的场景补一条最小写入路径（目前只有 <c>TogetherApi.Unbind</c> 用它）：
    /// 落盘之后由 <c>TogetherSettingsSync.PublishHostSettings</c> 把新值广播给客户端，
    /// 设置页读的也是同一份，所以界面上会跟着变成关。
    /// </remarks>
    public static void SetSymbiosisEnabled(bool value)
    {
        Initialize();

        var store = RitsuLibFramework.GetDataStore(Const.ModId);
        store.Modify<TogetherSettings>(DataKey, settings => settings.SymbiosisEnabled = value);
        store.Save(DataKey);
    }

    public static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        using (RitsuLibFramework.BeginModDataRegistration(Const.ModId, false))
        {
            RitsuLibFramework.GetDataStore(Const.ModId).Register(
                DataKey,
                FileName,
                SaveScope.Global,
                defaultFactory: () => new TogetherSettings(),
                autoCreateIfMissing: true);
        }

        RitsuLibFramework.GetDataStore(Const.ModId).InitializeGlobal();
        _initialized = true;
    }
}
