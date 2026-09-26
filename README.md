# Together · 合作模式（联机共享卡组）

语言 / Languages：中文 | [English summary](#english-summary)

《杀戮尖塔 2》的联机合作模组：**2~4 名玩家共用一个身体** —— 共享卡组、血量、格挡、状态、球位、召唤物与钱包，
各自保留手牌与能量；代价是要扛 n 倍的怪物伤害。

Steam 创意工坊：[合作模式（联机共享卡组）](https://steamcommunity.com/sharedfiles/filedetails/?id=3803029754)

> 这个 mod 也是"之后**可能**会出的联机专属角色 mod（画饼 ing）"主要机制的前置验证 mod，
> 所以日志 / 存档里可能还留着 `symbiosis` / 共生体之类的旧标识，先忽略吧，还可能有 bug（）。

## 特性

| 机制 | 说明 |
|---|---|
| **共享卡组** | 一副卡组大家一起用；抓牌进同一副；战斗中共享抽牌堆 / 弃牌堆（手牌各人各一份） |
| **状态共享** | 自己或队友拿到的 buff 会一并复制过去；`牌进卡组`这类"按牌属于谁"的遗物钩子按全组建账 |
| **共享身体** | 血量、格挡、血量上限、召唤物血量、充能球位、金币都是同一份；谁掉血/掉格挡，全组一起掉 |
| **怪物对策** | 以**原版联机（2 人局）**为难度基线：怪的血量 / 格挡 / AOE 都按原版多人规则，共享血池自然要扛 n 倍 |
| **不共享的** | 手牌、每回合能量、抽牌量 —— 和正常联机一样每人一份，所以不作修改 |

## 怎么玩

1. 在设置界面开启「**合作模式（共享卡组）**」（默认关；关着的时候本 mod 完全不介入对局）。
2. 进入联机大厅的**选人界面**，右下角会出现「**加入合作模式**」按钮 —— 按下去同时等于按了官方的"确认准备"。
3. **≥2 人**加入即成组（各自可以选不同角色）；再按一次（或按官方"取消准备"）即退出。
   只有 1 个人加入时不生效，这一局按普通联机打。
4. 全员准备后由本体照常开局。

想单人体验共享卡组？创意工坊搜索「**本地多角色**」（`LocalCoopClone`）。

## 设置项

| 分组 | 设置 | 说明 |
|---|---|---|
| 合作模式 | 开启合作模式（共享卡组） | 总开关 |
| | 合并双方初始卡组 | 开启后共享卡组 = 各人所选角色初始卡组之和 |
| | 血量上限提升（0~100%） | 把队友最大生命的百分之几并进共享血池 |
| | 共享金币 | 组内一个钱包 |
| | 共享球位上限 | 自动（10 × 有球位人数）/ 原版 10 格 / 10 × 全部人数 |
| 兼容性（高风险补丁） | 6+ 条 | 改的是"本体自己也会走的路径"，**只在和某个 mod 冲突时**逐条关掉回退 |
| 诊断 / 反馈 | 自检到异常时弹窗提醒 | **本机设置**（不跟主机同步）；关掉后反馈包照旧自动生成、log 照旧有 |
| | 一键导出 / 测试一下 | 见下面「反馈」一节 |

> 除「诊断 / 反馈」外，**联机时以主机设置为准**（否则一端关一端开会直接分叉）。

## 兼容性

- **依赖**：`STS2-RitsuLib`（自动注册与设置框架）。
- **可选联动**：装了「随机数预测（RandomForeseer）」时，本 mod 会让它的每次预测只建一份共享牌堆 / 球队列副本 ——
  修掉"抽牌预测连续都是第一张牌""充能球伤害预测不准"；没装它时这几条补丁完全不参与。
- **角色**：初始角色 + 绝大部分 mod 角色（本 mod 只操作卡组，对角色本身不挑）。
- **已知边界**：所有能力都会镜像（含带内部数据的），所以某些 buff 共享起来会偏强 —— 这是"共用一副牌 / 一个身体"的
  语义延伸，不按 bug 处理；觉得不合理可以直接反馈。
- 与别的 mod 冲突时，优先在设置里**逐条关掉兼容性开关**定位到具体一条，再反馈。

## 反馈

**要不要反馈、发到哪里，完全由你自己决定。** 这个 mod 不会自动上传任何东西。

出问题时 mod 会**自动把现场文件准备好**（不依赖你点任何按钮）：

| 文件 | 是什么 |
|---|---|
| `godot.log` | 游戏自己的运行日志，卡死 / 报错的现场就在最后几行 |
| `together-modlist.txt` | 启用的 mod 列表（按加载顺序）+ 本机生效设置 + 本局会话状态 |
| `ritsulib_state_divergence_*.zip` | 分歧诊断包（只有真的"不同步"时才会有） |

它们都在日志目录里（和 `godot.log` 同一个文件夹）：

```
%APPDATA%\SlayTheSpire2\logs\together-feedback\
```

三种拿法：

- **出问题时自动弹窗** → 点「打开文件夹」；
- **随时手动**：`模组设置 → Together · 合作模式 → 诊断 / 反馈 → 「一键导出」`
  （这是**本机设置**，战斗中的暂停界面也能点）；
- **只想验证一下**：同一节里的「**测试一下**」按钮会走完全一样的流程。

不想被打扰就在同一节里关掉「**自检到异常时弹窗提醒**」—— 文件照旧会准备好，只是不弹窗。

反馈渠道：**创意工坊页面评论区**，或者你加过的 mod 群（群里 @yyqy）。
附上上面那两个文件 + 一句"当时在做什么"（哪个事件 / 哪张牌 / 谁先按的）就够了。

## 从源码构建

首次构建前按本机路径改 `local.props`：

| 字段 | 说明 |
|---|---|
| `Sts2Dir` | Slay the Spire 2 安装目录 |
| `Sts2DataDir` | 游戏 dll 目录（默认 `$(Sts2Dir)/data_sts2_windows_x86_64`） |
| `GodotExe` | 用来导出 pck 的 Godot / MegaDot 可执行文件 |
| `RitsuLibDir` | RitsuLib 的本机目录（用于按 OS 自动发现游戏路径） |

```powershell
# 完整构建：编译 + 拷贝到 $(Sts2Dir)\mods\together + 导出 PCK
dotnet build .\together.csproj -c Debug

# 只验证 C# 编译（不拷 mod、不导出 PCK）：改了 JSON / 图片时必须单独导出 PCK 才会生效
dotnet build .\together.csproj -c Debug -p:RunPckExport=false
```

> 改 `.cs` 只需重新编译；改**本地化 JSON / 图片 / 场景必须重新导出 PCK**（`RunPckExport` 默认 `true`）。
> 游戏运行中会锁 DLL，构建的拷贝步骤会失败 —— 先关游戏再构建。

## 项目结构

```text
Main.cs                     入口：logger + 自动注册 + 逐个补丁类安装
Const.cs                    ModId / 版本 / 资源路径常量（ModId 三处必须一致）
together.json               Mod 清单（与 Const.Version 同步）
Core/Foundation/            配对（锚点/回声）、名单槽位、设置读写
Core/Settings/              设置页、设置模型、本机界面偏好
Core/Shared/{Deck,Body,Power,Orb,Pet,Gold}/   "一个身体"的五块共享域
Core/Alignment/             规则收口（归属放宽 / 钩子去重 / 怪招作用域 …）
Core/Integrations/          可选联动（RandomForeseer）
Core/Diagnostics/           诊断层（可整体删）：对账日志、取证、环境导出、异常弹窗
Core/Ui/                    界面文本（中英）与选人界面按钮
together/localization/      设置页 / 界面文案（zhs / eng）
ARCHITECTURE.md             架构说明（分层、补丁清单、不变量）
API使用说明.md               对外 API（依赖本 mod 开发时看这份）
```

## 文档

| 文档 | 给谁看 |
|---|---|
| `ARCHITECTURE.md` | 想改这份代码的人：分层、补丁清单、关键不变量 |
| `API使用说明.md` | 想依赖本 mod 开发的其它 mod 作者（接口与变更记录） |
| `共享角色设计方案.md` / `架构与实现说明.md` / `问题与解法清单.md` | 设计过程与历史问题记录 |

## 版本与依赖

| 项 | 值 |
|---|---|
| 当前版本 | `0.3.5` |
| 最低游戏版本 | `0.111.0` |
| RitsuLib 依赖 | `0.5.18` |

## 更新日志

- **0.3.5**：owner 归属槽位表（按 Deck 顺序随存档同步）+ 读档 / 配对 / 开战前三处还原 + 变牌前 owner 对齐，
  修掉"整副卡组在两端互为镜像"导致的变牌崩溃与不同步；「诊断 / 反馈」小节（一键导出环境、反馈包、
  自检异常弹窗及本机开关）；取证日志分级（`TOGETHER_DRIFT=1` 才出逐条明细）。
- **0.3.4**：兼容性补丁逐条开关化、随机数预测（RandomForeseer）联动、能力"只算一次"改成结构判据。
- **0.3.3**：球位共享与修复、镜子事件错误复制、夜魇 / 模仿学习崩溃、草蜢偷牌不同步。
- **0.3.2**：真实联机"开局就不同步"修复（名单改用 RitsuLib 的局内数据槽位，开局前对齐）。
- **0.3.1**：两端日志对账（`chk=`）、选人界面「加入合作模式」改版。
- **0.3.0**：首次发布对外 API 说明（配对规则 / 解绑 / 设置只读）。

更细的每轮改动与验收清单见 `项目文档/Together_现状与待办.md`（随源码仓库维护，不随 mod 分发）。

## English summary

A co-op mod for *Slay the Spire 2*: in multiplayer, **2–4 players share one body** — deck, draw/discard piles,
HP, Block, powers, orb slots, summons and gold — while hand and energy stay per player. The cost is taking
n× the monster damage.

- **Join**: enable "Co-op mode (shared deck)" in Settings, then press **Join Co-op Mode** on the multiplayer
  character-select screen (it also counts as the official "ready"). 2+ players = a group.
- **Compatibility**: depends on `STS2-RitsuLib`; optional integration with *Random Foreseer*.
  Host settings win in multiplayer.
- **Bug reports are optional and fully up to you** — nothing is uploaded automatically. When something goes
  wrong the mod writes `godot.log` + `together-modlist.txt` into `logs\together-feedback\` for you
  (also reachable from *Mod Settings → Together → Diagnostics*, where the alert popup can be turned off).
