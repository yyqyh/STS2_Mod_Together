using Together.Core.Content;

namespace Together.Core.Settings;

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
    /// 由玩家自己决定谁和谁共用身体（见 <c>SymbiosisMembers</c>）。
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

    /// <summary>共生体存档登记：<c>种子 → 成员 netId（逗号分隔）</c>。</summary>
    /// <remarks>
    /// "谁是共生体成员"是<b>选人界面按按钮</b>定的，本来只活在内存里；而读档（尤其是重启游戏后
    /// 点"继续"）根本不经过选人界面 —— 结果是按共生体写出来的存档被当成<b>普通联机局</b>加载，
    /// 血量/卡组不再共享，存档里的共享痕迹还会让状态看起来很怪。
    /// 所以新开一局时把成员按<b>种子</b>记下来，读档时按同一个种子找回。
    /// 只保留最近若干条（见 <c>TogetherSettingsStore.SymbioticRunHistory</c>）。
    /// </remarks>
    public Dictionary<string, string> SymbioticRuns { get; set; } = [];
}
