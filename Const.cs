namespace Together;

/// <summary>
/// 全局常量：ModId 与全部资源路径。
/// </summary>
/// <remarks>
/// <b>ModId 规则（最容易踩的一条）</b>：本体的 <c>ModManager</c> 是按
/// <c>{manifest.id}.dll</c> / <c>{manifest.id}.pck</c> 去 mod 目录里找文件的，
/// 所以下面三者必须完全一致，否则会出现「清单读到了、程序集没加载」的静默失败：
/// <list type="number">
/// <item><description>清单 <c>together.json</c> 的 <c>id</c></description></item>
/// <item><description>程序集文件名（由 <c>together.csproj</c> 的 <c>AssemblyName</c> / 工程名决定）</description></item>
/// <item><description>PCK 文件名（由 csproj 的 <c>ModPckPath</c> 生成）</description></item>
/// </list>
/// <see cref="ModId" /> 同时是 RitsuLib 的内容 ID 前缀来源：规范化后
/// <c>"together"</c> → <c>TOGETHER_</c>，内容 ID 形如 <c>TOGETHER_CARD_XXX</c>。
/// </remarks>
public static class Const
{
    /// <summary>Mod 唯一标识。必须与清单 id、DLL 名、PCK 名一致——见类型注释。</summary>
    public const string ModId = "together";

    /// <summary>显示用名称（清单里另有展示名，这里给代码侧用）。</summary>
    public const string Name = "Together";

    /// <summary>版本号，与清单 <c>version</c> 保持一致。</summary>
    public const string Version = "0.1.0";

    /// <summary>
    /// 能量颜色标识。卡池 / 遗物池 / 药水池的 <c>EnergyColorName</c> 都返回它，
    /// 用来索引该池的能量图标（<c>res://images/packed/sprite_fonts/{name}_energy_icon.png</c>）。
    /// </summary>
    public const string EnergyColorName = "Together";

    /// <summary>
    /// 资源路径。全部指向 PCK 内部的 <c>res://</c> 路径，
    /// 对应 Godot 工程目录下的 <c>together/</c> 文件夹。
    /// </summary>
    /// <remarks>
    /// <b>路径命名规则</b>（新增美术时照着写，别再引入临时名）：
    /// <list type="bullet">
    /// <item><description>卡面：<c>{CardsRoot}/card_{snake}.png</c>，常量名 <c>Card{ClassName}</c>。</description></item>
    /// <item><description>能力图标：<c>{PowersRoot}/power_{snake}.png</c>，在能力类里用 <c>PowerAssetProfile</c> 引用。</description></item>
    /// <item><description>遗物：<c>{RelicsRoot}/relic_{snake}.png</c>，常量名 <c>Relic{ClassName}</c>。</description></item>
    /// <item><description>UI：<c>{UiRoot}/{name}.png</c>，场景 <c>{ScenesRoot}/ui/**</c>。</description></item>
    /// <item><description>角色：<c>{CharacterRoot}/{name}.png</c>，战斗场景 <c>{ScenesRoot}/**</c>。</description></item>
    /// <item><description>本地化：<c>{LocalizationRoot}/zhs/{cards|characters|relics|powers|card_keywords|static_hover_tips}.json</c>（表名用复数）。</description></item>
    /// </list>
    /// 美术未就绪时统一用 <see cref="xxx" /> 占位：它指向目录本身，加载必然失败并回退到本体贴图，
    /// 只会刷一条 "Missing resource path" 警告，不会崩。画好之后把常量填上、把引用换掉即可。
    /// </remarks>
    public static class Paths
    {
        // ======================================================================
        // 根
        // ======================================================================

        /// <summary>内容根目录：Godot 工程下的 <c>together/</c>。</summary>
        public const string Root = "res://together";

        public const string ImagesRoot = Root + "/images";
        public const string ScenesRoot = Root + "/scenes";
        public const string LocalizationRoot = Root + "/localization";

        /// <summary>
        /// 美术未就绪时的占位路径（指向目录本身，加载必然失败 → 回退到本体贴图）。
        /// 代码里出现 <c>Art(Const.Paths.xxx)</c> 说明这张卡的卡面还没接。
        /// </summary>
        public const string xxx = Root;

        // ======================================================================
        // 分类目录（新增具体资源时在对应分类下面加常量）
        // ======================================================================

        /// <summary>卡面：<c>res://together/images/cards</c>。</summary>
        public const string CardsRoot = ImagesRoot + "/cards";

        /// <summary>能力图标：<c>res://together/images/powers</c>。</summary>
        public const string PowersRoot = ImagesRoot + "/powers";

        /// <summary>遗物图标：<c>res://together/images/relics</c>。</summary>
        public const string RelicsRoot = ImagesRoot + "/relics";

        /// <summary>UI 图标：<c>res://together/images/ui</c>。</summary>
        public const string UiRoot = ImagesRoot + "/ui";

        /// <summary>角色立绘 / 头像 / 地图箭头：<c>res://together/images/character</c>。</summary>
        public const string CharacterRoot = ImagesRoot + "/character";
    }

    /// <summary>音频路径（暂时为空，接入 FMOD 事件时在这里加常量）。</summary>
    public static class Audio
    {
    }
}
