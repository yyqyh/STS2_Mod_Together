using Together.Core.Content;

namespace Together.Core.Settings;

/// <summary>
/// 本 mod 的持久化设置。
/// </summary>
/// <remarks>
/// <b>加字段的约定</b>：给新字段一个合理的默认值即可，不用写迁移——
/// 老配置反序列化出来缺字段时会取这个默认值（见 <see cref="TogetherSettingsStore" />）。
/// </remarks>
public sealed class TogetherSettings
{
    /// <summary>
    /// 是否开启"共生体"。
    /// </summary>
    /// <remarks>
    /// 关闭时本 mod 完全不介入任何对局；开启后，多人选人界面会出现"共生体"按钮，
    /// 由玩家自己确定谁和谁共用身体（见 <c>SymbiosisMembers</c>）。
    /// <b>以主机设置为准</b>：客户端跟随主机，避免两台机器判定不一致导致分叉。
    /// </remarks>
    public bool SymbiosisEnabled { get; set; }

    /// <summary>
    /// 开局时是否把"回声（p2）"的初始卡组<b>复制</b>进共享卡组。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 开启：共生体的卡组 = p1 + p2（两个人各自的初始卡都在里面）。
    /// 关闭：只用锚点（p1）那一副初始卡组，回声那副不参与。
    /// </para>
    /// <para>
    /// 注意：这是<b>复制</b>，所以两个人选同一个角色时开启它会得到两份初始卡
    /// （两个静默猎手 = 24 张），需要"同角色只要一份"时把它关掉。
    /// 与共生体开关一样，联机时以主机设置为准。
    /// </para>
    /// </remarks>
    public bool MergeStarterDecks { get; set; } = true;

    /// <summary>
    /// 共生体血量上限提升：把"回声（p2）的最大生命"的百分之几加进共享血池，范围 0~100。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 0 = 不加（共享血池 = 锚点自己的上限）；100 = 把 p2 那一整份最大生命也加进来
    /// （等价于两个人的血池合在一起）。
    /// </para>
    /// <para>
    /// 只在新开一局时生效一次——上限会写进存档，读档/重连时不再重复加。
    /// 联机时同样以主机设置为准。
    /// </para>
    /// </remarks>
    public int HpBonusPercent { get; set; }

    /// <summary>
    /// 共生体人数上限（2~4）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这个值是"最多几个人能确定参加共生体"。实际人数由选人界面里按按钮的人决定：
    /// 只要 ≥2 人就成组（2 人就是原来的双人共生体），没按按钮的玩家照常各玩各的。
    /// </para>
    /// <para>
    /// 锚点仍然是"组里 Players 顺序最靠前的那位"，其余成员都是回声：
    /// 身体、卡组、抽弃牌堆共用，手牌与能量各人各一份。
    /// </para>
    /// </remarks>
    public int GroupSize { get; set; } = 2;

    /// <summary>
    /// 是否共享金币：开启后组内只有一个钱包，谁捡到/花掉都直接改同一份余额。
    /// </summary>
    /// <remarks>
    /// 开局时取组内<b>最大值</b>作为共同余额（不会把两个人的起始金币叠加成双份，也不会弄丢谁的钱）。
    /// 与其它设置一样，联机时以主机为准。
    /// </remarks>
    public bool ShareGold { get; set; } = true;

    /// <summary>
    /// 是否把原版事件改成"共享事件"（两人投票，票高的选项对所有人执行）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么需要这个开关</b>：联机时每个玩家各有一份事件实例（本体 <c>EventModel.IsShared=false</c> 时
    /// "players may choose options independently"），而共生体<b>共用一副卡组</b>。于是两个人可能同时对同一张牌
    /// 做"附魔 / 移除 / 升级"：后手会撞上"这张牌已经附魔过"而抛异常（附魔不可叠加），
    /// 又因为异常抛在事件结束之前，事件会卡住、房间出不去。
    /// </para>
    /// <para>
    /// 开启后事件只有一个选择（投票），从根上没有这个冲突。
    /// <b>代价</b>：① 事件奖励由"每人一份"变成"整组一份"；
    /// ② <c>IsDeterministic =&gt; !IsShared</c>，所以共享事件结束时<b>不再发校验和</b>（少一次不同步检查）。
    /// </para>
    /// <para>
    /// 不开也能用：本 mod 会在共享卡组变化时刷新另一个人的选牌界面，并在应用前拦掉已经失效的选择
    /// （不崩、不卡房，见 <c>Core/Patches/Together/EventFlowPatches.cs</c>）。
    /// </para>
    /// </remarks>
    public bool ShareEvents { get; set; }

    /// <summary>
    /// 共生体存档登记：<c>种子 → 成员 netId（逗号分隔）</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// "谁是共生体成员"是<b>选人界面按按钮</b>定的，本来只活在内存里；而读档（尤其是重启游戏后
    /// 点"继续"）根本不经过选人界面 —— 结果是按共生体写出来的存档被当成<b>普通联机局</b>加载，
    /// 血量/卡组不再共享，存档里的共享痕迹还会让状态看起来很怪。
    /// </para>
    /// <para>
    /// 所以新开一局时把成员按<b>种子</b>记下来，读档时按同一个种子找回。
    /// 只保留最近若干条（见 <c>TogetherSettingsStore.SymbioticRunHistory</c>）。
    /// </para>
    /// </remarks>
    public Dictionary<string, string> SymbioticRuns { get; set; } = [];
}
