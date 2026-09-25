# together 对外 API 使用说明

> 这份文档是 **together 对外接口的唯一说明**，也是 **API 变更的发布处**（见文末 §8 变更记录）。
> 文件位置：`together/API使用说明.md`；每次构建会自动复制一份到
> `…\steamapps\common\Slay the Spire 2\mods\together\`。
>
> 当前版本：**0.3.1**　｜　接口类型：`Together.Core.Api.TogetherApi`

---

## 0. 这个 mod 是什么

**2~4 人联机共控一个角色**：一个身体、一副卡组、一口球位、一只召唤物、一个钱包；手牌与能量仍各人各一份。

别的 mod 想跟它协作（判断"现在是不是共享局""队友是谁""这份能力会不会被复制"），或者想接管
其中某些决策（**谁该成组**、**什么时候解除绑定**），只依赖 `TogetherApi` 这一个类型。

---

## 1. 怎么依赖

### 1.1 清单声明依赖（必须）

在你自己 mod 的清单里加：

```json
"dependencies": [{ "id": "together", "min_version": "0.3.0" }]
```

依赖会被**拓扑排序**（together 先加载），缺少依赖时你的 mod 直接 `Failed`。
`min_version` 请写"真正带上你要用的那些接口的版本"（见 §8 变更记录）。

### 1.2 调用方式：RitsuLib 的 `[AssemblyInterop]` / `[ModInterop]`（不需要编译期引用）

不要直接引用 `together.dll`：本体只对 `sts2` / `0Harmony` 做程序集解析兜底，
直接引用会让"版本号对不上"变成加载失败。

用 RitsuLib 的互操作存根 —— 运行时把存根的调用转到 `TogetherApi`，**不产生编译期依赖**。
官方教程（<https://tutorials.sts2modding.com/docs/04-ritsulib/04-30-mod-integration/>）里**更推荐 `[AssemblyInterop]`**：
它按"程序集限定类型名"解析，不要求对方在 RitsuLib 里登记过 mod id。本 mod 的两个坐标是：

```text
程序集：together            类型：Together.Core.Api.TogetherApi
```

```csharp
using STS2RitsuLib.Interop;

// 推荐写法：类型名带 ", 程序集名"
[AssemblyInterop("Together.Core.Api.TogetherApi, together")]
internal static class TogetherInterop
{
    // 只声明你真正要用的成员；签名照抄 TogetherApi。
    // 方法体写 `=> default;` 就行 —— 运行时会把转发 IL 插在唯一的 ret 之前。
    public static bool IsActive => default;

    public static Player? Counterpart(Player? player) => default;

    public static void RegisterPairRule(string name, Func<IRunState, IReadOnlyList<Player>?> select) { }
}
```

想只用 mod id 也可以（两种写法等价，**含逗号 → 走 AssemblyInterop 路径，不含 → 走 ModInterop**，
所以同一个项目里可以混着写）：

```csharp
[ModInterop("together", "Together.Core.Api.TogetherApi")]
internal static class TogetherInterop { /* 同上 */ }
```

**名称/类型不一致时**用 `[InteropTarget("远端类型", "远端成员名")]` 手动指；要包装**实例类型**
（比如把对方的对象包成自己这边的引用类）就用嵌套类继承 `InteropClassWrapper` —— 两种写法都支持，
细节见教程。委托参数（`Func<…>`）正常可用，只要**参数/返回类型是两边都能命名的类型**。

约定（来自 RitsuLib 的实现）：

* 存根成员必须有**方法体**，且**只有一个 `ret`**（`=> default;` / `{ return default; }` 都行）；
* 名称、参数类型、返回类型要与 `TogetherApi` 上的成员**逐字一致**（不一致时 RitsuLib 会在日志里警告；
  需要改名/换类型时用 `[InteropTarget("类型", "成员名")]`）；
* 解析成功时日志里有 `[ModInterop] Generated interop method xxx`；
  解析失败时该存根**返回默认值而不抛异常** —— 所以请用 `IsActive` 之类的语义开关判断，别直接依赖返回值非 null。

**本 mod 接口的 interop 友好度**（照 RitsuLib 的匹配规则：存根参数类型可**更宽**，`object` 视为通配符；
但**值类型之间不做转换**）：

| 接口 | 能否零引用走 interop | 说明 |
|---|---|---|
| `IsActive` / `IsBound` / `Members` / `Counterpart` / `OtherBody` / `PileFingerprint` / `Checkpoint` … | ✅ | 参数/返回都是游戏类型、`System.*` 或我们之外的 pubic 类型 |
| `RegisterPairRule(name, Func<…>)` / `RegisterPetKeyRule` / `RegisterPetPairing` | ✅ | 委托的参数类型都是游戏类型（`IRunState` / `Player` / `Creature`） |
| `RegisterPowerMirrorOverride(Type, PowerMirrorPolicy)` | ❌ | 枚举 `PowerMirrorPolicy` 在我们程序集里，存根命名不了它（值类型不转换） |
| `RegisterPowerMirrorOverride(Type, bool singleInstance)` | ✅ | **为此新增的跨 mod 重载**：`false` = 每人一份（默认），`true` = 只留一份 |

### 1.3 可以直接抄的存根模板

把下面整个文件抄进你自己的工程（改一下命名空间即可）——**不需要引用 `together.dll`**，
参数与返回类型都是游戏类型 / `System.*`，两边都能命名：

```csharp
// TogetherInterop.cs
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib.Interop;

namespace 你的命名空间;

[AssemblyInterop("Together.Core.Api.TogetherApi, together")]
internal static class TogetherInterop
{
    // 目标没装时，下面这些方法体生效 —— 也就是"正常分支"：判据为假、注册静默忽略、解绑返回 false。
    public static bool IsReady => false;   // together 装了 → 被转发到我们那份（恒 true）
    public static bool IsActive => false;
    public static bool IsBound => false;
    public static bool IsMember(Player? player) => false;
    public static IReadOnlyList<Player> Members => [];
    public static Player? Counterpart(Player? player) => null;

    public static void RegisterPairRule(string name, Func<IRunState, IReadOnlyList<Player>?> select) { }
    public static void RegisterPowerMirrorOverride(Type powerType, bool singleInstance) { }
    public static void RegisterPetKeyRule(string name, Func<Creature, string?> keyOf,
        Func<Creature, Player?>? ownerOf = null) { }
    public static void RegisterPetPairing(string name, Func<Creature, Creature?> pairOf) { }
    public static bool Unbind(string reason = "external") => false;
}
```

用法就一句判据 + 正常调用（**注册类接口在 mod 初始化时调一次**，查询类接口随时可调）：

```csharp
if (TogetherInterop.IsReady)                          // 装了 together 才会是真的
{
    TogetherInterop.RegisterPairRule("my-mod:duo", runState => /* 选谁成组，两端算得一样 */ null);
}

// 战斗 / 事件里判断"这局是不是共享局"
if (TogetherInterop.IsActive)
{
    var mate = TogetherInterop.Counterpart(myPlayer);  // 共享局里 = 另一个人的 Player
}
```

想再加别的成员，照 §3 的表把签名抄进来、方法体写 `=> default;` 就行（记住：**只有一个 `ret`**）。

---

## 2. 三条硬约定（违反会分叉或崩）

1. **注册类接口只在 mod 初始化（注册内容）时调用一次**，不要放在战斗中途。
2. **任何"参与判定"的规则必须两端算出同一个答案**：只读、不写状态、不看 `LocalContext` 这类本机视角，
   顺序按 `IRunState.Players` 来（两端天然一致）。
3. **查询类接口在"本局不是共享局"时返回安全默认值**（`false` / `null` / 空集合），不要靠它反推别的 mod 状态。

---

## 3. 接口清单

### 3.1 对局事实

| 成员 | 说明 |
|---|---|
| `IsReady` | 恒为 `true`（`ModId` / `Version` 同理是常量）—— **只给 interop 存根当"对方在不在"的判据**（见 §1.3）。判断"本局在不在共享"用 `IsActive`，别用它 |
| `IsActive` | **总闸门**：本局真的在共享（联机 + 已配对）。判断"要不要为共享让路"就看它 |
| `IsBound` | 是否已配对 —— **不看是否联机**。判断"有没有配对过"用它；判断战场行为用 `IsActive` |
| `IsMember(Player?)` | 某玩家是不是合作组成员（含锚点） |
| `Anchor` | 锚点（权威实例持有者：主卡组 + 四口战斗牌堆都挂在它身上） |
| `Members` / `OthersOf(Player?)` / `Counterpart(Player?)` | 全部成员 / 除某人的成员 / 某人的队友（两人局就是另一个人） |
| `SymbiosisEnabled` / `GroupSize` / `MergeStarterDecks` / `HpBonusPercent` / `ShareGold` | 设置的**生效值**（联机时客户端跟随主机，与设置页同一份）。注：`GroupSize` 为**历史字段，已不参与判定**（现在无名额限制），保留只为兼容既有调用方与联机快照字段数 |

### 3.2 接管决策（注册类）

| 成员 | 说明 |
|---|---|
| `RegisterPairRule(name, select)` | **谁该成组**。`Arm()` 里按注册顺序问：返回非空列表即生效；返回 `null`/空表示"本条不管"，全部不管则回落"本局名单槽位"（玩家在选人界面按「加入合作模式」投的票，主机汇总后随 run snapshot 下发）。单条规则抛异常只跳过它自己 |
| `Unbind(reason = "external")` | **本局解除绑定**：立刻停掉共享、把共享主卡组按 1-based 奇偶拆给两人（奇数→锚点、偶数→回声）、把合作模式开关置 false 并广播；**本局内不再自动重新配对**（新开一局复位）。本来就未绑定返回 `false` |
| `RegisterPowerMirrorOverride(Type, PowerMirrorPolicy)` | **能力镜像策略**：默认所有能力都镜像；你的能力语义上不能复制时声明 `SingleInstance` |
| `RegisterPowerMirrorOverride(Type, bool singleInstance)` | 同上，**跨 mod 联动专用重载**（存根不用认识我们的枚举）：`false` = 每人一份（默认），`true` = 只留一份 |
| `RegisterPetKeyRule(name, keyOf, ownerOf?)` | **召唤物配对**：怎么认出"这是同一只"（默认按 Monster ID + 同种序号，本体 pet 不用注册） |
| `RegisterPetPairing(name, pairOf)` | 完全自己决定召唤物配对（优先级最高） |

### 3.3 共享域查询

| 成员 | 说明 |
|---|---|
| `OtherBody(Creature?)` | 共享身体里"另一位成员的身体"（非共享局返回 `null`） |
| `PetCounterpart(Creature?)` | 队友身上"同一只召唤物"（配对不上返回 `null`） |
| `IsSharedOrbQueue(OrbQueue?)` / `SharedOrbQueueOwner(OrbQueue?)` | 这口球位队列是否共享 / 它的主人（也就是"该由谁跑球位回合钩子"的人） |
| `IsMirroredPower(PowerModel)` | 这份能力是不是本 mod 造出来的**镜像副本** |
| `PileFingerprint(IEnumerable<CardModel>?)` | 一串牌"按当前顺序"的指纹（`3F2A…/31`），用于两端对 log 自检 |
| `Checkpoint(tag, data = "")` | 打一个**对账点**：生成一次校验和 → 输出一行 `[sync] chk=<id> ctx=… ` → 返回这个号（详见 §4.8） |

---

## 4. 常用配方

### 4.1 判断"是不是共享局"并拿队友

```csharp
if (TogetherInterop.IsActive)
{
    var mate = TogetherInterop.Counterpart(myPlayer);
    // mate 可能为 null（理论只在多人局且配对异常时），所以要判空
}
```

### 4.2 注册配对规则（例：正好 2 人 + 两个不同角色才成组）

```csharp
// 在 mod 初始化（注册内容）时调一次
TogetherInterop.RegisterPairRule("MyMod:two-distinct", runState =>
{
    var players = runState.Players;
    if (players.Count != 2) return null;
    if (!players.All(p => IsMyCharacter(p.Character))) return null;
    if (players[0].Character.Id == players[1].Character.Id) return null;

    return players;   // 顺序保持 runState.Players 的顺序（两端一致的关键）
});
```

要点：**只读**、**不抛**（抛了只会废掉你这条规则，会记 warning）、**顺序照抄 `Players`**。
规则算出的名单由 together 自己负责两端一致（写入本局名单槽位 / 由 `Arm` 两端各自算出同一答案），你不需要实现同步。

### 4.3 进 PVP 决斗时解绑

```csharp
if (TogetherInterop.IsBound && TogetherInterop.Unbind("duel_enter"))
{
    // 共享立刻停（IsActive == false），卡组已按奇偶拆给两人，合作模式开关已置 false 并广播
}
```

解绑是**本局一次性**的：读档 / 重连都不会重新配对；新开一局自动复位。
超过 2 人时不做奇偶分牌，只把各自卡组还原并打一条 warning。

### 4.4 我的能力不能复制

```csharp
TogetherInterop.RegisterPowerMirrorOverride(typeof(MyFragilePower), PowerMirrorPolicy.SingleInstance);
```

`PowerMirrorPolicy` 是 `Together.Core.Api` 下的枚举（`Mirror` / `SingleInstance`）。
默认全镜像；只有在"镜像后语义确实不对"时才覆写。

### 4.5 我的召唤物怎么配对

```csharp
// 方式一：给出"怎么认出同一只"的键 + 属于谁
TogetherInterop.RegisterPetKeyRule("MyMod", c => c.Monster is MySummon ? "MySummon" : null,
                                   c => c.PetOwner ?? MyOwnerOf(c));

// 方式二：完全自己配对（优先级最高）
TogetherInterop.RegisterPetPairing("MyMod", c => FindPartnerSummon(c));
```

### 4.6 读设置（生效值）

```csharp
if (TogetherInterop.SymbiosisEnabled && TogetherInterop.HpBonusPercent > 0) { … }
```

### 4.7 自检：牌堆顺序指纹

```csharp
Logger.Info($"Draw 顺序指纹：{TogetherInterop.PileFingerprint(pile.Cards)}");
```

本 mod **不重排任何牌堆**（弃牌堆 / 主卡组 / 抽牌堆都保持你给它的顺序），所以指纹是纯只读的取证手段；
两份 log 一比就知道顺序漂没漂。

### 4.8 对账：把两端日志按 `chk` 对齐

```csharp
uint chk = TogetherInterop.Checkpoint("my_checkpoint", "after_reward");   // 0 = 非联机，忽略
if (chk != 0) Logger.Info($"chk={chk} my_state hand={hand} draw={draw}");
```

`Checkpoint` 内部就是"生成一次校验和"，所以：

* 它返回的号就是本体 `ChecksumTracker` 分配的 **id**，而 id 严格按调用顺序递增、且本体要求"每端调用次数一致"
  → **两端同一个号 = 同一步骤**，这是唯一天然一致的定位键（context 文案不唯一，靠文案对不上）；
* 调它之后，together 自己会输出一行 `[sync] chk=<号> ctx=… tag=together.state …`（把锚点/回声的
  hp / block / powers / 各牌堆数量 / 金币 / 球位都打在一起）；
* together 自己的诊断日志（`order.probe` / `summon.*` / `orb.*` / `power.*` …）也会自动带上 `chk=<当前号>` 前缀，
  所以两份 log 直接按 `chk=` 分组就能逐行对照；
* **必须在两端都调用、且调用次数完全一致**，否则之后所有号错位 → 本体自己会报假分歧。

---

## 5. 语义细节（容易搞错的点）

| 问题 | 答案 |
|---|---|
| `IsActive` 和 `IsBound` 有什么区别？ | `IsBound` 只看"配对过没有"；`IsActive` 还要求**联机**。单人局里可能留着上一局的配对（设计如此，避免读档误清），所以要判战场行为必须用 `IsActive` |
| 拿到 `IsMirroredPower(power) == true` 意味着什么？ | 它是 together 为了让"两个人共用同一份能力"造出来的副本。副本**不参与联删 / 层数同步**（那些由原件收口），你写逻辑时按"这是一份独立的、但代表同一件事的能力"处理即可 |
| `OtherBody` 和 `PetCounterpart` 的分工？ | `OtherBody` 是**玩家的身体**；召唤物的 `Creature.Player` 是 null，必须用 `PetCounterpart`（否则永远拿不到） |
| `Unbind()` 之后会发生什么？ | ① `IsActive`/`IsMember` 立刻变 false，镜像 / 共享牌堆 / 事件保护整体停；② 两人的主卡组按当前共享卡组顺序的 1-based 奇偶拆开；③ 合作模式开关置 false 并广播（设置页跟着变）；④ 本局不再自动配对，新开一局复位 |
| 我会不会和 together 抢牌堆顺序？ | 不会。together 不重排任何牌堆（顺序归玩家 / 别的 mod）；它只在两处做**只读**指纹日志 |
| 我注册的规则什么时候被问？ | 只在 `Arm()`（新开一局 / 读档 / 重连）。不会在战斗中途问，所以规则要幂等、无副作用 |

---

## 6. 我们保证什么 / 不保证什么

**保证**

* `TogetherApi` 这个类型**不会破坏性变更**：只会新增成员（新增即向后兼容）；确需修改时会在 §8 发布并升版本；
* 不注册任何接口时，你的行为与不装 together 时一致（together 只在共享局里介入）；
* 所有查询在非共享局返回安全默认值，不抛异常。

**不保证**

* `Together.*` 下除 `TogetherApi` 之外的任何类型（内部实现，可能随时改名 / 合并）；
* 共享局里"每个玩家各一份的对象"（球位队列 / 召唤物生物 / 手牌 / 能量）的**内部结构**——
  要协作请只用 §3 的接口；
* together 不会替你做同步：你的规则 / 覆写必须自己在两端算出同样的结果。

---

## 7. 已知边界

* **关系型能力**（"我施加给你"）在镜像时采用"互复述"语义（原件：我施给你；副本：你施给我），这是本 mod 的定义、不是本体语义；
* 牌堆顺序一致性**不做强制归一**，靠"两端同逻辑 + `PileFingerprint` 取证"；
* 召唤物只有一边召出来时不会替另一边补建（只留 `summon.missing` 诊断）；
* 更多内部细节见仓库里的 `ARCHITECTURE.md`。

---

## 8. 变更记录（**以后 API 变更都写在这里**）

> 规则：新增成员 = 向后兼容（记在表里即可）；**破坏性变更必须先在这里公告并升版本**，
> 同时更新 §1.1 里推荐的 `min_version`。

| 版本 | 日期 | 变更 |
|---|---|---|
| **未发布** | 2026-09-25 | ① 新增 `RegisterPowerMirrorOverride(Type, bool singleInstance)` —— 跨 mod 联动重载（原来的枚举重载在 `[ModInterop]` / `[AssemblyInterop]` 存根里用不了，见 §1.2 的"interop 友好度"表）。② 新增 `IsReady`（恒 `true`）—— 给存根当"对方在不在"的判据。③ 文档补 §1.3 可直接抄的存根模板。均向后兼容；发布时并进下一个版本号 |
| **0.3.1** | 2026-09-24 | ① **新增 `Checkpoint(tag, data = "")`**：打对账点（生成一次校验和 → 输出 `[sync] chk=<id> ctx=… tag=together.state …` → 返回 id；非联机返回 0）。同期 together 侧：**自检默认在共享局开启**（`TOGETHER_SELFCHECK` 仍可 `1`/`0` 强制），所有自带诊断行自动加 `chk=<当前号>` 前缀 —— 于是两端日志可以直接按 `chk` 分组对照。用 `Checkpoint` 时**两端调用次数必须一致**。② **选人界面改版（合作模式）**：按钮文案改为「加入合作模式」/「退出合作模式」（无悬浮人数分数），按下 = 登记 + 让本体走一遍"确认准备"（内部反射调 `NCharacterSelectScreen.OnEmbarkPressed`，退出走 `OnUnreadyPressed`）；**删除起程门控与人数上限**（谁都能加入、≥2 人自成组、只有 1 人加入时按普通联机打），官方确认键不再被本 mod 触碰。`GroupSize` 连同设置项一起退役为历史字段（接口保留，行为不变） |
| **0.3.0** | 2026-09-24 | **首次发布对外 API 说明**。新增三组接口：`RegisterPairRule`（配对规则）、`IsBound` / `Unbind`（解绑）、`SymbiosisEnabled` / `GroupSize` / `MergeStarterDecks` / `HpBonusPercent` / `ShareGold`（设置只读）。同时把此前已在 `TogetherApi` 上的成员一并纳入正式承诺：`IsActive` / `IsMember` / `Anchor` / `Members` / `OthersOf` / `Counterpart` / `OtherBody` / `PetCounterpart` / `IsSharedOrbQueue` / `SharedOrbQueueOwner` / `IsMirroredPower` / `RegisterPowerMirrorOverride` / `RegisterPetKeyRule` / `RegisterPetPairing` / `PileFingerprint` |
| （模板） | yyyy-mm-dd | 新增 `xxx`；**破坏性**：`yyy` 由 `a` 改成 `b`（调用方需要改这里）；废弃 `zzz`（改用 …） |
