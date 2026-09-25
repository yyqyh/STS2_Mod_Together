using Together.Core.Foundation;

namespace Together.Core.Settings;

/// <summary>共享球位上限的口径（见 <c>OrbSlotSharing.CapacityCap</c>）。</summary>
public enum OrbCapMode
{
    /// <summary>10 × 本局"有球位"的成员数（默认：两个故障机器人 = 20；一个人有球位就还是 10）。</summary>
    Auto,

    /// <summary>原版固定 10 格，不管几个人共享。</summary>
    Vanilla,

    /// <summary>10 × 本局全部成员数（不看角色有没有球位；3 人局就算只有 1 个机器人也给 30 格）。</summary>
    PerMember,
}

/// <summary>本 mod 的持久化设置。</summary>
/// <remarks>
/// <b>加字段的约定</b>：给新字段一个合理的默认值即可，不用写迁移——
/// 老配置反序列化出来缺字段时会取这个默认值（见 <see cref="TogetherSettingsStore" />）。
/// </remarks>
public sealed class TogetherSettings
{
    /// <summary>是否开启"合作模式（共享卡组）"。</summary>
    /// <remarks>
    /// 关闭时本 mod 完全不介入任何对局；开启后，多人选人界面会出现"加入合作模式"按钮，
    /// 由玩家自己决定谁和谁共用身体（见 <c>CoopLobbyData</c>）。
    /// <b>以主机设置为准</b>：客户端跟随主机，避免两台机器判定不一致导致分叉。
    /// </remarks>
    public bool SymbiosisEnabled { get; set; }

    /// <summary>开局时是否把"回声（p2）"的初始卡组<b>复制</b>进共享卡组。</summary>
    /// <remarks>
    /// 开启：合作模式的卡组 = p1 + p2（两个人各自的初始卡都在里面）。
    /// 关闭：只用锚点（p1）那一副初始卡组，回声那副不参与。
    /// 注意：这是<b>复制</b>，所以两个人选同一个角色时开启它会得到两份初始卡
    /// （两个静默猎手 = 24 张），需要"同角色只要一份"时把它关掉。
    /// 与合作模式开关一样，联机时以主机设置为准。
    /// </remarks>
    public bool MergeStarterDecks { get; set; } = true;

    /// <summary>共享血池的血量上限提升：把"回声（p2）的最大生命"的百分之几加进共享血池，范围 0~100。</summary>
    /// <remarks>
    /// 0 = 不加（共享血池 = 锚点自己的上限）；100 = 把 p2 那一整份最大生命也加进来
    /// （等价于两个人的血池合在一起）。
    /// <b>默认 100</b>：本项目的难度基线是"<b>原版联机（2 人局）</b>"——
    /// 那边的怪血 / 怪格挡都是 ×2、AOE 也是打两个身体（共享池受 2× 伤害），
    /// 所以共享血池取"两份血池之和"才等价。
    /// 只在新开一局时生效一次——上限会写进存档，读档/重连时不再重复加。
    /// 联机时同样以主机设置为准。
    /// </remarks>
    public int HpBonusPercent { get; set; } = 100;

    /// <summary>【历史字段，已没有任何读取方】合作人数上限。</summary>
    /// <remarks>
    /// 曾经是"最多几个人能加入合作"。现在改成<b>无名额</b>：谁都能按「加入合作模式」，
    /// 加入的人自成一组，上限由 <c>TogetherPair.MaxMembers</c>（4）硬性截断。
    /// 现在连设置页和 <c>TogetherSettingsStore</c> 都不再读它（后者固定返回 4）。
    /// 字段本身保留不删，是为了不改变设置文件与联机快照（<c>TogetherSettingsSync.Snapshot</c>）的结构 ——
    /// 快照字段数一变，和旧版本 / wa2 那边的解析就对不上了。
    /// </remarks>
    public int GroupSize { get; set; } = 4;

    /// <summary>是否共享金币：开启后组内只有一个钱包，谁捡到/花掉都直接改同一份余额。</summary>
    /// <remarks>
    /// 新开一局取组内<b>起始金币之和</b>作为共同余额（读档/重连只做"取最大值对齐"，避免每次重连翻倍）。
    /// 与其它设置一样，联机时以主机为准。
    /// </remarks>
    public bool ShareGold { get; set; } = true;

    /// <summary>共享球位（故障机器人的 orb）上限口径。</summary>
    /// <remarks>
    /// 共享局里几个人的球位队列是同一口，容量上限得按"参与共享的人数"放大 ——
    /// 见 <see cref="OrbCapMode" />。与其它设置一样，联机时以主机为准。
    /// </remarks>
    public OrbCapMode OrbCap { get; set; } = OrbCapMode.Auto;

    /// <summary>共生体存档登记：<c>种子 → 成员 netId（逗号分隔）</c>。</summary>
    /// <remarks>
    /// "谁是共生体成员"是<b>选人界面按按钮</b>定的，本来只活在内存里；而读档（尤其是重启游戏后
    /// 点"继续"）根本不经过选人界面 —— 结果是按共生体写出来的存档被当成<b>普通联机局</b>加载，
    /// 血量/卡组不再共享，存档里的共享痕迹还会让状态看起来很怪。
    /// 所以新开一局时把成员按<b>种子</b>记下来，读档时按同一个种子找回。
    /// 只保留最近若干条（见 <c>TogetherSettingsStore.SymbioticRunHistory</c>）。
    /// </remarks>
    public Dictionary<string, string> SymbioticRuns { get; set; } = [];

    // ======================================================================
    // 兼容性开关（B 类"改本体行为"的补丁，各自可单独关掉 → 回到原版行为）
    // 默认全开 = 保持既有体验；关掉只在"与别的 mod 冲突"时使用。
    // 注意：联机时以主机为准（否则一端关一端开会直接分叉）。
    // ======================================================================

    /// <summary>是否启用「牌序全序化」（<c>CardModel.CompareTo</c>）。关掉 = 用本体比较器。</summary>
    public bool CompatDeterministicOrder { get; set; } = true;

    /// <summary>是否启用「钩子监听表去重」。关掉 = 保留本体可能出现的重复监听者。</summary>
    public bool CompatHookDedupe { get; set; } = true;

    /// <summary>是否启用「钩子派发组内放宽」（共享局里让每个成员的遗物都收到"牌进卡组"通知）。</summary>
    public bool CompatHookWiden { get; set; } = true;

    /// <summary>是否启用「回声共享卡牌视图置空」（怪招期间共享牌只算一次）。</summary>
    public bool CompatSharedCardView { get; set; } = true;

    /// <summary>是否启用「回声不重复填充战斗牌堆」。</summary>
    /// <remarks>
    /// 开启（默认）：进战斗时只让锚点把主卡组复制进抽牌堆 —— 主卡组只有一份，回声再填一次就是双倍卡组。
    /// 关闭：回声也照常填充（等于回到"两个人各填一遍"），<b>共享卡组会变成两份</b>。
    /// 只在"另一个 mod 自己接管了回声的战斗牌堆"时才关掉。
    /// </remarks>
    public bool CompatEchoPopulateSkip { get; set; } = true;

    /// <summary>是否启用「镜像副本的回合末能力只由原件结算一次」。</summary>
    /// <remarks>
    /// 开启（默认）：临时力量/敏捷/集中、虚弱/易伤/脆弱这几个"会改身体数值"的回合末能力，
    /// 只让原件扣一次（镜像副本不重复扣）。
    /// 关闭：完全按本体行为（每份镜像各扣一次，数值会翻倍/变负）。
    /// </remarks>
    public bool CompatMirroredPowerSingleFire { get; set; } = true;

    /// <summary>是否启用「注能每场战斗只自动打出一次」。</summary>
    /// <remarks>
    /// 开启（默认）：同一张注能牌在本场战斗里只自动打出一次（按对象引用去重）。
    /// 关闭：本体照常派发（配合上面的「只跑锚点那次」一般不会再重复）。
    /// </remarks>
    public bool CompatImbuedOnce { get; set; } = true;

    /// <summary>是否启用「随机数预测（RandomForeseer）联动」。</summary>
    /// <remarks>
    /// 开启（默认）：装了那个 mod 时，让它每次预测只建<b>一份</b>共享牌堆 / 球队列副本 ——
    /// 否则会出现"抽牌预测连续都是第一张牌""充能球伤害预测不准"（它按玩家分别快照，而共享身体下这些是同一份）。
    /// 关闭：完全不碰对方的预测内核（预测退回对方原本的算法）。
    /// </remarks>
    public bool CompatRandomForeseerSync { get; set; } = true;
}
