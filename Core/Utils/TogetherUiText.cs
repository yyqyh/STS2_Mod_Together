using STS2RitsuLib;
using STS2RitsuLib.Settings;
using STS2RitsuLib.Utils;

namespace Together.Core.Utils;

/// <summary>
/// 界面文本（设置页、选人界面的按钮）的唯一出口：按当前游戏语言取中/英文。
/// </summary>
/// <remarks>
/// <para>
/// 用 RitsuLib 的 <see cref="I18N" />（和酒狐的设置页同一套）：<b>一个语言一个 JSON</b>，
/// 放在 <c>res://together/localization/mod_settings/{eng|zhs}.json</c>，内容是扁平的 <c>key → 文本</c>。
/// </para>
/// <para>
/// <b>为什么代码里还留一份中文</b>：<see cref="I18N" /> 的回退链是"当前语言 → <c>eng.json</c> → 代码 fallback"，
/// 所以中文字符串在代码里当最后一道保险 —— 任何一边缺失都不会出现空白，也不会让中文玩家莫名其妙看到英文。
/// </para>
/// <para>
/// <b>两个来源都配</b>：PCK 里的 <c>res://</c>（正常打包路径）和 DLL 旁边的 <c>localization/mod_settings</c>
/// （构建时由 csproj 复制过去）。这样"只改了 JSON、懒得重新导出 PCK"也能立刻看到效果。
/// </para>
/// </remarks>
internal static class TogetherUiText
{
    private static readonly Lazy<I18N> Localization = new(() => RitsuLibFramework.CreateModLocalization(
        Const.ModId,
        "together-ui",
        fileSystemFolders: ModFolders(),
        pckFolders: [Const.Paths.ModSettingsLocalizationRoot]));

    /// <summary>取一条界面文本；缺失时返回 <paramref name="fallback" />（一般是中文原文）。</summary>
    public static string Get(string key, string fallback)
    {
        try
        {
            return Localization.Value.Get(key, fallback);
        }
        catch (Exception)
        {
            // 本地化是锦上添花：取不到就回落到中文，绝不让它影响界面。
            return fallback;
        }
    }

    /// <summary>给设置界面用：返回每次刷新都重新解析文本的对象（切语言后自动跟着变）。</summary>
    public static ModSettingsText Settings(string key, string fallback)
    {
        return ModSettingsText.I18N(Localization.Value, key, fallback);
    }

    /// <summary>DLL 旁边的 <c>localization/mod_settings</c>（拿不到路径就只靠 PCK 里那一份）。</summary>
    private static string[] ModFolders()
    {
        try
        {
            var dllDir = Path.GetDirectoryName(typeof(TogetherUiText).Assembly.Location);
            if (!string.IsNullOrWhiteSpace(dllDir))
            {
                return [Path.Combine(dllDir, "localization", "mod_settings")];
            }
        }
        catch (Exception)
        {
            // 反射拿不到路径（单文件发布之类）→ 交给 PCK。
        }

        return [];
    }
}
