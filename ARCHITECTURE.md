# Together 架构说明

> 面向两类读者：**接手维护的人**（看 §1–§4）与 **想依赖本 mod 开发的人**（看 §5–§7）。
> 代码位置：`E:\game_part2\mod_c\sts2\mod\remake\together\`（`Main.cs` 是入口，`Core/` 下分四层）。
> 最后更新：2026-09-24（能力镜像升级 + 对外接口）

---

## 0. 一句话说清这个 mod 在做什么

**2~4 人联机共控一个角色**：一个身体、一副卡组、一口球位、一只召唤物、一个钱包；手牌 / 能量仍是各人各一份。
引擎强制"每个玩家各有一个 `Creature`、各有一套 `PlayerCombatState`"，所以"一个"全部是靠**重定向 + 镜像**做出来的。

---

## 1. 分层总览

```
                    ┌──────────────────────────────────────────────┐
   公共面           │  Core/Api/TogetherApi.cs（唯一 public 依赖面） │
                    └──────────────────────────────────────────────┘
                                      ▲
   L3 兼容层          SameOwnerCheckCompat / 结构判据 / 牌堆顺序让位
                                      ▲
   L2 规则收口        归属 · 事件份数 · 顺序与随机 · 界面同步 · 事件流程
                                      ▲
   L1 共享域          身体 · 牌堆 · 能力(power) · 球位 · 召唤物 · 金币
                                      ▲
   L0 底座            谁是成员 · 总闸门 · 设置 · 日志/自检
```

| 层 | 职责（一句话） | 代表类型 | 目录 |
|---|---|---|---|
| **L0 底座** | 决定"这局是不是共享局、谁是成员"，并提供设置与日志 | `TogetherPair`、`SymbiosisMembers`、`TogetherCoopGate`、`TogetherSettings*`、`CappedLog`/`SelfCheck` | `Core/Combat/Together/TogetherPair.cs`、`Core/Content/`、`Core/Settings/`、`Core/Utils/` |
| **L1 共享域** | 把"每人一份"的东西变成"共用一份" | `SharedPileImpl`、`BodyMirror`、`PowerMirror`、`PowerPayload`、`OrbSlotSharing`、`SummonMirror`、`GoldMirror` | `Core/Combat/Together/` |
| **L2 规则收口** | 让两端**算出同一个答案**（归属 / 份数 / 顺序 / 界面） | `CardOwnershipImpl`、`HandReturnOwnership`、`HookListenerDedupe`、`MonsterMoveScope`、`DeterministicCardOrder`、`PileCountSync`、`EventFlow` | `Core/Patches/Together/`（+ 少量同目录） |
| **L3 兼容层** | 与别的 mod 共存 | `SameOwnerCheckCompat`、`PowerMirror.PolicyOf` 的结构判据、`PileOrderProbe` | `Core/Patches/Together/Deck/SameOwnerCheckCompat.cs` 等 |
| **L4 诊断层** | 取证与自愈（可整体删） | `RoomFlowDiag`+`HangWatchdog`、`SharedStateSelfCheck`、`CappedLog` 键 | `Core/Utils/` |

**依赖方向**：L1→L0，L2→L1/L0，L3→L2，公共面→任意层。**反向依赖不允许**（补丁层不定义内核类型）。

---

## 2. 各层详解

### L0 底座

| 机制 | 关键点 |
|---|---|
| **锚点 / 回声**（`TogetherPair`） | 锚点 = `RunState.Players` 顺序里最靠前的成员，权威实例（主卡组 + 四口战斗牌堆）挂在它身上；其余是回声，访问入口重定向到锚点，但**手牌与能量独立**。锚点由 `Players` 顺序决定 —— 两端天然一致、且写进存档，**不能用本机视角（`LocalContext`）判**，否则第一次抽牌就分叉 |
| **总闸门** `TogetherPair.IsActive` | = 已配对 **且** 联机。镜像 / 球位 / 召唤物 / 金币 / 事件保护……全部只看它。带"联机"这一条是必须的：配对按设计"非共生体局不清空"，单人局里可能残留上一局的配对 |
| **激活时机** `Arm` | 必须**晚于** `RunState` 构造（`CreateShared` 会遍历牌组给每张卡设 owner，提前激活会让 p2 读到被重定向的卡组 → "同一张牌设两次 owner" → 开局黑屏）。入口：`RunStateReadyPatch`（新局 / 读档两条） |
| **成员名单** | 选人界面按「加入合作模式」→ `SymbiosisMembers`（主机权威 + sidecar 同步，**无名额限制**）；进局后由 `RunMembersSync` 广播"本局成员"，供草蜢等判据用（它不能依赖选人界面那份，读档/重连时是空的） |
| **成员来源（可外部接管）** | `Arm()` 里先问外部注册的**配对规则**（`TogetherApi.RegisterPairRule`），都没有结果才回落"选人界面按了「加入合作模式」的名单"；规则算出的名单同样会被 `RunMembersSync` 广播，所以规则只要"两端各自算得一样"即可 |
| **解绑（外部 mod 入口）** | `TogetherApi.Unbind(reason)`：关开关 + 清成员名单并广播 + 置"本局不再自动配对"标记 + 把共享主卡组按 **1-based 奇偶**（奇数→锚点、偶数→回声）拆回两人；新开一局复位。超过 2 人只还原各自卡组 + warning |
| **设置** | `TogetherSettings`（持久化）/ `TogetherSettingsStore`（读写）/ `TogetherSettingsSync`（**联机以主机为准**，sidecar 发布订阅）/ `TogetherModSettingsPage`（设置页）。所有读取都走 `Effective*` 属性，不直接读 Store |

### L1 共享域（"一个"是怎么实现的）

| 共享对象 | 做法 | 关键踩坑 |
|---|---|---|
| **牌堆**（`SharedPileImpl`） | 把回声的 `DrawPile/DiscardPile/ExhaustPile/PlayPile` 四个 backing field 换成锚点那一份；`Hand` **不换**（各人各一份）；`PopulateCombatState` 只让锚点跑（否则同一套卡组被克隆两遍 = 双倍卡组） | 换字段必须**清 `_piles` 缓存**；`Deck`（主卡组）也要换字段（只改 getter 不够：界面会把旧引用存进自己的字段里） |
| **身体**（`BodyMirror`） | 盯 `set_Block` / `set_CurrentHp` / `set_MaxHp` 三个 setter，谁变就推给组里其他人；值相等直接返回 → 天然收敛 | 召唤物（pet）的 `Creature.Player` 是 **null**，不在成员身体集合里，必须单独接一条腿（`SummonMirror.PartnerOf`） |
| **能力**（`PowerMirror` + `PowerPayload` + `PowerApplySource`） | 见 §2.1 | 见 §2.1 |
| **球位**（`OrbSlotSharing`） | 换 `PlayerCombatState.OrbQueue` 字段 → 两人操作/显示同一口队列；容量补成"各成员基础球位之和"（上限 10×n） | 回合钩子 `AfterTurnStart`/`BeforeTurnEnd` 是**逐玩家**调的，共享后必须只让队列主人跑；界面是按节点增量画的，要镜像；`PlayerCombatState` 会被下一场战斗**沿用** → 每回合重挂一次 |
| **召唤物**（`SummonMirror` + 扇形召唤） | 本体召唤逐玩家 → 任一成员召唤时**扇形**对队友各调一次本体入口；血量用镜像维持（只推 hp/maxHp，不推格挡） | 召唤期间**不推镜像**（否则"5→5→再5→10 翻倍"）；`DieForYouPower` 的本体判定是"只替召出它的那个人挡"→ 共享身体下要放开 |
| **金币**（`GoldMirror`） | `Player.Gold` 的 setter 是唯一收口 → 谁变推给组里其他人；<br>重建窗口（保存 / 读档 canonicalize）里**旧 `Player` 实例**被写时，按 netId 认领回当前实例、用**当前实例的值**继续镜像（不推旧身那份存档值，否则等于把钱包打回去） | 新局**求和**（99×人数）、读档只取最大值对齐（否则每次重连翻倍） |

#### 2.1 能力（power）镜像 —— 本 mod 最复杂的一块

**目标**：所有能力都镜像，而且副本要**真正生效**。

| 环节 | 做法 | 为什么 |
|---|---|---|
| **选哪些镜像** | 判据顺序：① `Overrides` 手工出口（默认空，对外接口可写）→ ② `Applier.IsMonster` **不镜像**（本体怪招自己会逐个玩家施加）→ ③ 其余**镜像**；另外"**能力自己衍生的跨成员施加**"（`cardSource == null`，见 `PowerApplySource`）不镜像 | 怪的能力镜像会翻倍；衍生的关系型能力镜像会变成"自己掩护自己" |
| **造副本** | `power.MutableClone()` → `PowerPayload.TryCopy`（内部数据） → **身份对齐**（`Applier`/`Target`；关系型还要**互换视角**：副本的 `Applier` = 原件宿主） → `ApplyInternal(新宿主, 同层数)` → `SyncFields`（自身字段） → 打"镜像副本"标记 → 记"原件" → 补 `SkipNextDurationTick` | 克隆契约只重置 `_internalData`，其余字段是浅拷贝；关系型能力（我施加给你）副本要从另一方视角复述 |
| **非 Instanced 防重** | 目标身上已有同一份 → **跳过添加**（层数由 `OnPowerAmountChanged` 收口同步） | 本体在 `PowerCmd.Apply` 里靠 `FindExistingInstanceForStacking` 处理"第二份走改层数"，我们绕过那条路，不自己判就会撞 `Trying to add multiple instances of a non-instanced power`（实测拦截崩在这） |
| **补状态（关键）** | `PowerPayload.Sync` 在**每次派发前**"**只补空**"：① 合并内部数据 ② 同步自身字段。能搬的只有**值类型 + 身份对象**（模型 / Creature / Player / 战斗状态）；Godot 节点、委托、指令类、集合一律跳过（`power.payload.skip`） | 很多状态在 `Apply` **之后**才填（模仿学习的 `PlayerTarget`），克隆那刻拿不到；"只补空"是为了不冲掉副本自己写的状态（余像/潘塔奇是"成对记录"型，整份覆盖会让副本完全不生效） |
| **重放** | **关系型**副本补跑一次 `AfterApplied`（异步、失败只记日志、期间屏蔽镜像）；`BeforeApplied` **不重放** | 关系型的衍生施加被"无来源"判据挡住，只能副本自己建（拦截的 `CoveredPower→InterceptPower`）；非关系型的衍生本来就会被镜像机制送到两边，重放就是一边双份 |
| **移除 / 层数** | 副本被移除时不联删另一份（资源类）；层数变化同步给"对应的第 N 份副本"（按类型 + 同类型内序号配对） | 资源类（下回合加费…）是一次的：一份被消耗不该把另一份删掉，否则"先开始回合的人把收益拿走" |

**配套的"第二命中"机制**：本体对多目标效果是"逐个目标 Apply / 逐个加层"，共享身体下会算成两遍 → `PowerSecondHitModifyAmountPatch` + `PowerSecondHitApplyPatch` 用"同类型 + 同施加者 + 同层数 + 时间窗 + 目标没被这次效果命中过"来识别并忽略第二次。

### L2 规则收口

| 收口对象 | 机制 | 关键决策 |
|---|---|---|
| **卡牌归属** | 共享堆里的牌保留**自然归属**（"这张牌属于谁"）；只有"进手牌"那一刻把 owner 对齐到手牌主人；本体的"同批 owner 必须一致"校验由 transpiler 打掉 | 归一（把共享堆里的牌统一改成锚点）会连带毁掉"我的牌"判据（金纸/卡戎之灰/探戈…），所以撤掉了 |
| **从共享堆回手** | 搬运之前先把这批牌改成"当前正在结算效果的那名玩家"，本体随后用 owner 推出的目标手牌就是他自己那口 | 目标手牌是本体用 `cards[0].Owner` 推的，共享堆里 owner 不等于"正在操作的人" |
| **监听表去重** | `CombatState` / `RunState` 的 `IterateHookListeners` 两个源头都按**引用**去重 | 共享牌堆/共享主卡组会让同一张牌被收集两次（注能被自动打出两次、第二次还会卡住战斗循环）；按 Id 去重会误伤同名牌 |
| **怪招作用域** | 怪物执行招式期间开作用域，让**回声那一侧**的"共享卡牌视图"返回空 | 共享卡组下两位成员的 `AllCards` 指向同一批牌，而怪招按 target 逐个跑 → 同一批牌被处理 N 次（沙漏凋萎升级就是这么坏的）；**不折叠目标** |
| **顺序与随机** | 只保留两条**不碰牌堆**的：`DeterministicCardComparePatch`（把 `CardModel.CompareTo` 变全序，本体自己的 `List.Sort` 排的是副本）与 `CardFactoryDistinctOrderPatch`（排序候选池**副本**）。破灭/受膏/初始洗牌三处只打**顺序指纹**（`order.probe`） | 用户需要别的 mod 能控制牌堆顺序 → 我们**不重排任何牌堆**；顺序一致改由"两端同逻辑 + 指纹取证"保证 |
| **界面同步** | 牌堆计数（`PileCountSync`）、球位（`OrbSlotSharing.MirrorAnim`）、选牌界面（`DeckSelectionWatch`）、选人界面（`CharacterSelectPatches`） | 本体界面多为"事件累加 / 按节点增量画"，共享后漏事件 → 改成"按真实数据写死 / 在帧末重建" |
| **事件流程** | 事件本身照原版"每人一份、各自选"；只在①共享卡组一变就重建本机选牌界面候选 ②附魔/移除/变牌入口把失效选择跳过而不是抛异常 ③选牌界面"建到玩家眼前才算数" | 两份事件实例动同一张牌会撞 `Cannot enchant`/`You cannot remove a card that is not in the deck.`，异常抛在 `SetEventFinished` 之前 → 事件不结束、房间出不去 |

### L3 兼容层

| 机制 | 做法 |
|---|---|
| **别的 mod 自己复制的 owner 校验** | `SameOwnerCheckCompat`：扫方法 IL 里有没有字符串常量 `different owners`（**不看 mod 名字**），命中就把 `newobj/throw` 摘掉；结论按程序集 **MVID** 跨启动缓存（mod 更新即失效重扫） |
| **能力镜像的结构判据** | 不写牌名：靠 `Applier.IsMonster` / `InstanceType` / `cardSource` / 字段类型特征，对 mod 能力同样成立；mod 也可用 `TogetherApi.RegisterPowerMirrorOverride` 自己声明 |
| **牌堆顺序让位** | 本 mod **不重排任何牌堆**（弃牌堆 / 主卡组 / 抽牌堆都保持原样），想控制牌堆的 mod 不需要额外协调 |

### L4 诊断层（可整体删）

| 组件 | 作用 |
|---|---|
| `RoomFlowDiag` + `HangWatchdog` | 房间流程里程碑日志；黑屏探针 + 自愈（转场遮罩卡在黑色时强制淡回） |
| `SharedStateSelfCheck` | 校验和生成点把两端状态打出来（共享局默认开，`TOGETHER_SELFCHECK=0` 可静音） |
| `CappedLog` | 每个键最多 N 条的普通日志（功能取证默认可见，不刷屏） |
| `TogetherUiText` | 界面文本（设置页 / 选人界面按钮）唯一出口：按游戏语言取中/英。文本在 `together/localization/mod_settings/{eng,zhs}.json`（RitsuLib `I18N`），代码里那份中文是最后回退 |
| 取证键一览 | `hook.dedupe` / `move.shared_cards` / `power.payload(.field/.skip)` / `power.replay` / `power.mirror(.skip)` / `event.*` / `order.probe` / `gold.*` / `orb.*` / `summon.*` / `steal.*` |

---

## 3. 补丁清单（59 个 `[HarmonyPatch]` 类）

> 安装方式：`Main.ApplyPatches` **逐类安装**（不用 `Harmony.PatchAll`）—— 单类失败只废它自己，
> 否则 `Initialize` 抛异常会让整个 mod 初始化失败（而且 PatchAll 不是事务性的，会留下"一半功能正常"的状态）。

### L0 底座（`Core/Patches/Together/`、`Core/Settings/`、`Core/Content/`）

| 类 | 挂点 | 作用 |
|---|---|---|
| `RunStateReadyPatch` | `RunState.CreateForNewRun` / `FromSerializable` | 跑局就绪后激活配对（只有新局才加血量上限 / 合并初始卡组） |
| `CharacterSelectPatches` / `CharacterSelectUnreadyPatches` | 选人界面 4 个方法 + 本体"取消准备" | 装「加入合作模式」按钮、跟随换人刷新；按下 = 登记 + 反射调本体 `OnEmbarkPressed`（= 确认准备），再按 = 取消登记 + `OnUnreadyPressed`。**没有起程门控**（开局交给本体 ready 流程） |
| `HostStartSettingsSyncPatch` / `HostPeerReadySettingsSyncPatch` / `ClientResetSettingsSyncPatch` | 主机开服 / 对端就绪 / 客户端连接与断开 | 设置与成员名单的 sidecar 广播与清理 |
| `RoomFlowDiagPatches` | 房间里程碑 8 个方法 | 只看不改的日志 + 两处拉起看门狗 |
| `ChecksumSelfCheckPatch` | `ChecksumTracker.GenerateChecksum` | 校验和前的自检输出 |

### L1 共享域（`Core/Combat/Together/`）

| 类 | 挂点 | 作用 |
|---|---|---|
| `CombatPileRedirectPatch` / `DeckRedirectPatch` | 四口堆 getter / `Player.get_Deck` | 回声重定向到锚点 |
| `PopulateCombatStateAnchorOnlyPatch` / `CombatStateCreatedPatch` | 进战斗填充 / `PlayerCombatState` 构造 | 只让锚点填充；建完状态后链接球位、拉平身体数值 |
| `BodyStatMirrorPatch` | `set_Block`/`set_CurrentHp`/`set_MaxHp` | 共享身体 + 召唤物血量（召唤期间不推） |
| `PowerMirrorPatches` | `ApplyPowerInternal`/`RemovePowerInternal`/`InvokePowerModified` | 能力镜像三个入口 |
| `PowerSecondHitModifyAmountPatch` / `PowerSecondHitApplyPatch` | `PowerCmd.ModifyAmount` / `PowerCmd.Apply` | 同一效果的第二命中整个忽略 |
| `PowerApplySourceRecordPatch` | `PowerCmd.Apply` | 记下 `cardSource`，供"衍生施加不镜像"判据 |
| `OrbRelinkOnTurnStartPatch` / `OrbVisualMirrorPatch` / `OrbAddSlotsCapPatch` / `OrbTurnHookDedupePatch` | 回合开始 / 球位界面 5 个方法 / `OrbCmd.AddSlots` / 球位两个回合钩子 | 球位共享的四个收口 + 激发动画保险 |
| `PetSummonFanoutPatch` / `DieForYouSharedBodyPatch` | `OstyCmd.Summon` / `DieForYouPower.ModifyUnblockedDamageTarget` | 扇形召唤；召唤物替队友挡伤害 |
| `GoldMirrorPatch` / `LoseGoldMirrorPatch` | `Player.Gold` setter / `PlayerCmd.LoseGold` | 钱包共享 |

### L2 规则收口（`Core/Patches/Together/Deck/`、`.../Combat/`、`EventFlowPatches.cs`）

| 类 | 挂点 | 作用 |
|---|---|---|
| `CardOwnerSinglePatch` | `CardPileCmd.Add(card, pile, …)` | 进堆时归手牌主人 |
| `HandOwnershipInvariantPatch` | `CardPile.AddInternal` | 进手牌原地对齐 owner |
| `DifferentOwnersCheckPatch` | `CardPileCmd+<Add>d__N.MoveNext`（transpiler） | 打掉本体"同批 owner 必须一致"校验 |
| `HandReturnPatches` | `CardPileCmd.Add` 三个重载 | "从共享堆回手"的落点/归属修正 |
| `SelectedFromPilePatch` | `CardSelectCmd.FromCombatPile` | 记"谁从哪口堆选牌" |
| `CardPileLookupPatch` | `CardModel.get_Pile` | 找不到时去另一半的堆里再找（安全网） |
| `PileCountSyncOnChangePatch` / `PileCountBindPatch` / `PileCountUnbindPatch` | 入/出堆 + 牌堆按钮 Initialize/销毁 | 牌堆计数按真实张数写死 |
| `AutoPlayFromDrawPileProbePatch` / `AnointedOrderProbePatch` / `AutoPlayDiagnosticPatch` | 自动打牌 / 受膏 / `CardCmd.AutoPlay` | 顺序指纹 + 诊断（不改顺序） |
| `CardFactoryDistinctOrderPatch` | `CardFactory.GetDistinctForCombat` | 候选池副本排序（不碰牌堆） |
| `DeterministicCardComparePatch` | `CardModel.CompareTo` | 全序（修 `StableShuffle` 族两端不一致） |
| `InitialShuffleProbePatch` | `CardPile.RandomizeOrderInternal` | 初始洗牌后打指纹（不排序） |
| `AscensionBaneDedupePatch` | `AscensionManager.ApplyEffectsTo` | 共享卡组下"进阶之灾"去重 |
| `HookListenerDedupePatches` | `CombatState`/`RunState.IterateHookListeners` | 监听表按引用去重 |
| `MonsterMoveScopePatch` / `SharedCardViewScopePatch` | `MonsterModel.PerformMove` / `PlayerCombatState.get_AllCards` | 怪招期间共享卡牌只算一次 |
| `ImbuedOncePerCombatPatch` | `Imbued.AfterAutoPrePlayPhaseEntered` | 注能每场战斗只自动打出一次 |
| `MirroredPowerSingleFirePatch` | 6 个回合末能力 | 会改身体数值的回合末能力只由原件结算一次 |
| `GeneratedCardHandTargetPatch` | `CardPileCmd.AddGeneratedCardsToCombat` | 镜像副本产出的牌落到宿主手里 |
| `ExtraTurnPolicyPatches` | `Creature.AfterTurnStart` / `ClearBlock` | 额外回合不清共享格挡（并避开本体 NRE） |
| `DeckSelectionLifecyclePatch` / `DeckChangeRefreshPatch` / `EnchantApplyGuardPatch` / `RemoveFromDeckGuardPatch` / `TransformGuardPatch` / `EnchantSelectionPatches` / `PendingSelectorRegisterPatch` / `ChoiceSyncDiagPatch` | 事件与选牌流程 | 见 §2「事件流程」 |
| `StealRecordPatch` / `StealReturnAllPatch` / `ThieveryMoveTargetsPatch` / `StealVictimApplyPatch` | 草蜢偷牌 4 处 | 多人各偷一张、被偷方显式指定 |

### L3 兼容层

| 类 | 说明 |
|---|---|
| `SameOwnerCheckCompat`（无 `[HarmonyPatch]`，运行时动态装） | 扫 IL 放行别的 mod 的 owner 校验 + MVID 跨启动缓存 |

---

## 4. 关键不变量（改代码前先确认不破坏这些）

1. **只在 `TogetherPair.IsActive` 时介入**；单人局/原版联机必须保持原版行为。
2. **两端必须算出同一个答案**：锚点来自 `RunState.Players` 顺序；任何 `LocalContext`/本机视角都不能参与"谁是锚点/回声"。
3. **不重排任何牌堆**（顺序归玩家 / 别的 mod）；顺序一致性用"同逻辑 + `order.probe` 指纹"取证。
4. **能力镜像不写名单**：判据是结构特征（怪 / Instanced / cardSource / 字段类型），mod 可用 `TogetherApi` 覆写。
5. **非 Instanced 能力不重复添加**；副本的层数由 `OnPowerAmountChanged` 收口同步。
6. **"只补空"**：同步副本状态时绝不覆盖副本自己写的值。
7. **补丁逐类安装**；新增补丁用 `[HarmonyPatch]` + `TargetMethods()` 的合并风格（同一关注点一个类）。
8. **对外只暴露 `TogetherApi`**；内部类型保持 `internal`。

---

## 5. 对外接口（依赖开发用）

> **接口的正式说明与变更记录在仓库的 [`API使用说明.md`](../together/API使用说明.md)**
> （同时随构建复制到 `…\mods\together\`）。本节只列"有哪些面"，用法、示例与版本承诺看那份。

### 5.1 依赖方式

- 引用 `together.dll`（`mods/together/together.dll`），或直接反射调用 `Together.Core.Api.TogetherApi`。
- 命名空间 `Together.Core.Api`；类型 `TogetherApi`（静态类）+ `PowerMirrorPolicy`（枚举）。
- **不保证兼容**的：其他 `Together.*` 内部类型（可能随时改名/合并）。

### 5.2 接口一览

| 成员 | 说明 |
|---|---|
| `ModId` / `Version` | 本 mod 标识与版本 |
| `IsActive` | **总闸门**：本局是不是共享局（联机 + 已配对） |
| `IsMember(Player?)` | 某玩家是不是合作组成员 |
| `Anchor` | 锚点（权威实例持有者） |
| `Members` / `OthersOf(Player?)` / `Counterpart(Player?)` | 组成员 / 除某人外的成员 / 某人的队友（两人局就是另一个人） |
| `OtherBody(Creature?)` | 共享身体里"另一位成员的身体" |
| `PetCounterpart(Creature?)` | 队友身上"同一只召唤物" |
| `IsSharedOrbQueue(OrbQueue?)` / `SharedOrbQueueOwner(OrbQueue?)` | 这口球位队列是否共享 / 它的主人（该由谁跑回合钩子） |
| `IsMirroredPower(PowerModel)` | 这份能力是不是本 mod 的镜像副本 |
| `RegisterPowerMirrorOverride(Type, PowerMirrorPolicy)` | 声明某个能力的镜像策略（`Mirror` / `SingleInstance`） |
| `RegisterPetKeyRule(...)` / `RegisterPetPairing(...)` | 召唤物配对的扩展点 |
| `RegisterPairRule(name, select)` | **配对规则**：注册"谁该成组"的判定（返回 `null` = 本条不管，全部 `null` 回落"按按钮的名单"） |
| `IsBound` / `Unbind(reason)` | 是否已配对（**不看联机**）/ **本局解绑**：断开共享 + 按奇偶拆卡组 + 本局不再自动配对 |
| `SymbiosisEnabled` / `GroupSize` / `MergeStarterDecks` / `HpBonusPercent` / `ShareGold` | 设置的**生效值**（联机时客户端跟随主机） |
| `PileFingerprint(IEnumerable<CardModel>?)` | 顺序指纹（自检 / 对 log 用） |

### 5.3 用法示例

```csharp
// ① 我的卡要"指向队友"：拿队友的 player
var mate = TogetherApi.Counterpart(myPlayer);
if (mate is not null && TogetherApi.IsActive) { /* 走多人分支 */ }

// ② 我的能力语义上不能复制 → 声明只留一份（在 mod 初始化时调一次）
TogetherApi.RegisterPowerMirrorOverride(typeof(MyFragilePower), PowerMirrorPolicy.SingleInstance);

// ③ 我的 mod 有自定义召唤物（非本体 pet）→ 注册配对规则
TogetherApi.RegisterPetKeyRule("MyMod", c => c.Monster is MySummon ? "MySummon" : null,
                               c => c.PetOwner ?? MyOwnerOf(c));

// ④ 想确认"共享局里牌堆顺序两端一致"（不重排，只取证）
CappedLog / Logger.Info(TogetherApi.PileFingerprint(pile.Cards));
```

### 5.4 约定

- 注册类接口**在 mod 初始化时调用一次**；不要放在战斗中途。
- 所有查询在非共享局返回安全默认值，不会抛；`RegisterPowerMirrorOverride` 传非 `PowerModel` 类型会抛 `ArgumentException`。
- 本 mod **不改别人牌堆顺序**，也不需要别人为我让路；唯一需要协调的是"能力镜像策略"（走 ②）。

---

## 6. 目录结构（32 个 `.cs`）

```
together/
├── Main.cs                       入口：注册 → 设置初始化 → 逐类装补丁
├── Const.cs                      ModId / 名称 / 版本 / 资源路径常量
└── Core/
    ├── Api/TogetherApi.cs        ★ 对外接口（唯一 public 面）
    ├── Combat/Together/
    │   ├── TogetherPair.cs       L0 锚点/回声 + 总闸门 + Arm
    │   ├── TogetherCoopGate.cs   L0 选人阶段查询（Applies / 谁已加入；无名额、无门控）
    │   ├── TogetherMirrors.cs    L1 身体镜像 + 能力镜像内核（BodyMirror / PowerMirror）
    │   ├── PowerPayload.cs       L1 能力内部数据 + 自身字段搬运
    │   ├── PowerApplySource.cs   L1 "这次施加是卡还是能力衍生"
    │   ├── OrbSlotSharing.cs     L1 球位共享 + 界面镜像
    │   ├── Pets.cs               L1 召唤物配对 + 扇形召唤
    │   ├── MonsterMoveScope.cs   L2 怪招作用域
    │   └── ExtraTurnPolicy.cs    L2 额外回合策略
    ├── Content/SymbiosisMembers.cs           L0 成员名单（主机权威 + sidecar）
    ├── Patches/Together/
    │   ├── CharacterSelectPatches.cs         L0 选人界面
    │   ├── EventFlowPatches.cs               L2 事件与选牌流程
    │   ├── Combat/{HookPatches,PowerPatches}.cs   L2 监听去重 / 注能 / 回合末 / 生成牌落点
    │   └── Deck/{SharedPile,CardOwnership,PileView,RandomPick,SameOwnerCheckCompat,Steal}Patches.cs
    ├── Settings/{TogetherSettings,TogetherSettingsStore,TogetherSettingsSync,TogetherModSettingsPage}.cs
    └── Utils/{CappedLog,ModelAccess,DeterministicCardOrder,CreaturePartnerExtensions,RoomFlowDiag,SharedStateSelfCheck,TogetherUiText}.cs
```

界面文本（不进程序集）：`together/localization/mod_settings/{eng,zhs}.json` —— RitsuLib `I18N` 的扁平 `key → 文本`，
一个语言一个文件；构建时另复制一份到 `mods/together/localization/mod_settings/`，改 JSON 不必重导 PCK。

---

## 7. 扩展点汇总

| 你想做的事 | 接口 |
|---|---|
| 判断"这局是不是共享局 / 队友是谁" | `TogetherApi.IsActive`、`Counterpart`、`Members` |
| 我的能力不想被复制（或想强制复制） | `TogetherApi.RegisterPowerMirrorOverride` |
| 我的召唤物怎么配对 | `TogetherApi.RegisterPetKeyRule` / `RegisterPetPairing` |
| 我拿到一份能力，想知道它是不是镜像副本 | `TogetherApi.IsMirroredPower` |
| 我的界面要显示"另一个人的身体" | `TogetherApi.OtherBody` |
| 我要处理共享球位 | `TogetherApi.IsSharedOrbQueue` / `SharedOrbQueueOwner` |

---

## 8. 已知边界（详见 `Together_现状与待办.md`）

- **关系型能力**的镜像语义（"我施加给你"）是我们定的"互复述"规则，不是本体语义；数值是否可接受要实测。
- 牌堆顺序一致性**不再强制归一**：靠"两端同逻辑 + `order.probe` 指纹"取证；已知风险是"卡牌奖励两端落地先后不同"。
- 召唤物的"只有一边召出来"不会替另一边补建（只留 `summon.missing` 诊断）。
- 草蜢多张归还仍交回本体（只还本体记住的那一张）。
