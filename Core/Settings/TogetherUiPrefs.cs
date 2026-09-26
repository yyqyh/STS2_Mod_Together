using STS2RitsuLib.Utils.Persistence;
using STS2RitsuLib;

using Together;

namespace Together.Core.Settings;

/// <summary>本机的界面偏好（<b>不进联机同步</b>：每台机器自己决定）。</summary>
/// <remarks>
/// <para>
/// <b>为什么不放进 <see cref="TogetherSettings" /></b>：那个模型是"本局规则"，联机时<b>以主机为准</b>
/// （客户端会跟随主机广播的值）。而这里放的是"我这台机器想不想被打扰"这种纯本机选择 ——
/// 主机不该替客户端决定要不要弹窗，客户端也不该因为主机改了就跟着变。
/// 顺带也就不会动到设置文件和联机快照的结构（跨版本解析那一层的约定）。
/// </para>
/// </remarks>
public sealed class TogetherUiPrefs
{
    /// <summary>自检到异常（不同步 / 我们的断言失败 / 补丁没装全）时是否弹窗提醒。</summary>
    /// <remarks>
    /// 关掉之后行为不变的部分：环境快照<b>照旧自动导出</b>、log 里<b>照旧</b>有
    /// <c>⚠ 自检异常（…）｜环境快照：…</c> 那一行 —— 只是不弹窗。
    /// </remarks>
    public bool AlertPopup { get; set; } = true;
}

/// <summary>本机界面偏好的持久化（和设置同一套 RitsuLib 数据存储，只是另一个文件 / key）。</summary>
internal static class TogetherUiPrefsStore
{
    /// <summary>数据键（发布后不可改）。</summary>
    public const string DataKey = "ui_prefs";

    private const string FileName = "together_ui_prefs.json";

    private static bool _initialized;

    /// <summary>注册存储（幂等；必须在设置界面绑定之前调用）。</summary>
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
                defaultFactory: () => new TogetherUiPrefs(),
                autoCreateIfMissing: true);
        }

        RitsuLibFramework.GetDataStore(Const.ModId).InitializeGlobal();
        _initialized = true;
    }

    /// <summary>自检异常要不要弹窗（默认开）。</summary>
    public static bool AlertPopup
    {
        get
        {
            Initialize();

            var prefs = RitsuLibFramework.GetDataStore(Const.ModId).Get<TogetherUiPrefs>(DataKey);
            return prefs is null || prefs.AlertPopup;
        }
        set
        {
            Initialize();

            var store = RitsuLibFramework.GetDataStore(Const.ModId);
            store.Modify<TogetherUiPrefs>(DataKey, prefs => prefs.AlertPopup = value);
            store.Save(DataKey);
        }
    }
}
