# 夹具与原语

写用例的通用判据在 `SKILL.md`，用例契约在 `contract.md`。下文只写各类原语管什么、互相怎么分，以及签名里无从得知的取舍。逐成员签名与行为用 `rimrunner engine find <类型>.` 查，机制读 RimRunner 安装目录的 `engine/src/`。

## 引擎共享原语

| 原语 | 管什么 |
| --- | --- |
| `RimRunnerPawns` | 隔离生成与清理。引擎入口生成的 pawn 不带 vanilla 生成期的老年病与旧伤；`RimRunnerRaid` 的成员仍带老年病与旧伤。 |
| `RimRunnerEnvironmentSweep` | 清扫地图。`SweepAll` 由引擎在每条 World 级用例开窗之前调用，产品代码不调用 `SweepAll`。`SweepPawns` 在用例体内也可调用，只清 pawn。 |
| `RimRunnerRaid` | 生成并部署一队敌人。成员不归一化：trait 与武装是敌队的一部分 |
| `RimRunnerClearRect` | 找一块 W×H 的空矩形，全图枚举、确定序；产品特有的判据经逐格谓词或整块谓词传入。**不预留**：找到第一块后，调用方在其上未放物体时再次调用，该原语重新查找，两次返回同一块。失败文案按格报挡格理由，Thing 分本用例新增与用例开始时已在。自己写的找地逻辑无法分辨本用例新增与用例开始时已在。单个建筑连交互格的落点用 `RimRunnerBuildingCells` |
| `RimRunnerObservationBuffer` | 观察补丁记下的事件（job 起止、mote 等）。job 观测缺省只收玩家下令或带能力的 job；要判思维树签发或产品命令式发起的 job，先 `ctx.ObserveAllJobs(pawn)` |
| `RimRunnerTickDelivery` | 按 Thing 记 vanilla 实际交给 `TickInterval` 的 tick。**逐 tick 结算的量推 N tick 后拿到的不是 N**：交付按批，窗两端各留一段残批。期望按记下的 Σdelta 计算。 |
| `RimRunnerTickRatePin` | 把 tick 分档钉成一档（`ctx.PinTickRate`）。被测图是当前地图时，计量与对照两组对象都要钉：不钉时档位随 pawn 进出视野逐物不同，频率差数倍，毫秒列上无法体现这个差。 |
| `RimRunnerMapCostMeter` | 按图分桶的开销计量：几组样本各放一张同构的独立小图、同一段 tick 一起推。**比值有两种归约口径，按所量的成本选择**：同块配对的比再取中位，量稳态成本；整轮合计相除，量含卡顿与 GC 的尾部成本，被测物自己造成的偶发重算只在整轮合计相除里体现出来。 |

**样本**：只看这一轮的单值交 `ctx.AddDiagnostic`（各档的 ctx 都有）。要跨轮曲线，读数用例交 `ctx.AddSample`（只在 `RimRunnerReadoutContext` 上）。样本推进前 `ctx.OpenSampleWindow()`，量完那一刻 `Close()` 定格段长，再交；未关就交即抛。
口径字段 `scope` 收**会让两轮不可比**的量（窗口语义、对照组、名单、开关）：漏写一项，读侧把不同前提的两轮并成一条线，曲线看起来正常，漏掉的那一项带来的差是假的。落图由引擎记在每轮的封批行上（`mapSite`）。

### 一次性靶：规格

三种规格（`RimRunnerTargetSpec`、`RimRunnerDummySpec`、`RimRunnerSubjectSpec`）都是 `record`：从静态组合入手，偏离项写 `组合 with { 项 = 值 }`。

先站定、首次被打断后交回靶自己的树，用 `StandThenFreeHuman`。

引擎组合按测试角色起名、不带产品语义；带产品语义的组合（某个被测能力专用的靶）写成产品侧的规格常量。

### 一次性靶：假人

def 扫描面 `RimRunnerDefScope` 的两档按意图选，要收窄就交 `where`：判空在收窄之后。调用方拿到结果再自己收窄的，空集不进这道判空。

产品用例扫全部种族、在假人上判 FAIL 时，修用例。两档缺省滤掉的只是引擎内容里的假人，产品经 `ParentName` 派生的假人照常在内。

假人的几个特性，读数时容易当成被测侧的效果：

- **全 tag 假人的意识随伤势下降**：部位血量经 `ConsciousnessSource` 等 tag 降低意识，伤势占池比例另经疼痛降低意识，读意识的量（心灵敏感一类的对抗幅度）受这两条影响。要 capacity 不随部位血量漂移，换 `BareKind`：以脑为目标的 hediff 与伤害落进池里，经 tag 降低意识的那条不再降低。剩下的变量是疼痛；要意识恒定，再用无痛伤口把疼痛降到 0。
- **缺省假人出血恒为 0**：读出血用 `RimRunnerDummies.BleedingKind`（单部位、全 tag），出血、失血与血迹全按 vanilla。要别的 body 时种族写 `ParentName="RimRunnerDummyBleedingBase"` 再写 `race.body`
- **缺省树是待机，会还手**：原版 `Wait`，邻格有敌对就近战，挨打的殖民者身上多出的伤会被当成由被测侧造成的伤。要靶永不出手，选 `RimRunnerDummies.HoldTree`。
- **`Threat` 缺省 true**：敌对立场下，图上的炮塔与征召中的殖民者会对它开火；狂化动物与 Sightstealer 按威胁选目标，也会扑它。`Threat=false` 时这几类都不选它
- 征召的 pawn 不能从假人身上穿过

换树见 `rimrunner engine find RimRunnerDummySpec.`。产品自己写的树写成 `contentDir` 里的 `ThinkTreeDef`，挂 `RimRunnerDummyThinkTreeExtension` 声明这棵树是否走动、是否出手。

`WithStat` 写的是底值。stat 的 worker 改写取底值的方法时生成被拒：要定的量由别的 stat 推出时改设那个 stat 的底值，要定死读数用 `WithStatFinalized`。

### 被测体

派系写 `Affiliation`，异种写 `Xenotype`。`Unforced` 只按 kind、派系与意识形态掷异种；读数受基因影响（stat、越狱、心情、能否暴力）的写 `Baseliner` 或 `Of(def)`。

被测体规格显式给 `RimRunnerDisposableAgency`：

- `Pinned`：思维树自发的 job 改为无期限 `Wait`，`hostilityResponse` 置 Ignore，连带抑制贴身自动近战、征召待命自动瞄准、扑火；随机精神崩溃与社交斗殴也按 pawn 关闭
- `Free`：保留自主 AI，被测对象是 AI 自发行为（找食物、反击、自取 job、贴身自动近战、征召后自动开火）时用

中途放开（先站定、到点交回思维树）调 `ctx.ReleaseAgency(pawn)`，放开后可再钉。瞬移、被搬运、受击打断之后，把钉住的 pawn 收回原地时再调 `ctx.PinAgency`。

精神态各条路各有挡法：

| 来路 | 挡法 |
|---|---|
| 随机精神崩溃、社交斗殴 | 按 pawn：`Pinned`；全图（未钉的 pawn 崩溃后会攻击被测 pawn 时）：`ctx.SuppressRandomMentalStates()` |
| hediff 阶段的 `mentalBreakMtbDays` | `ctx.BlockHediffBreaks(pawn)`；生成路径分散、须各族同施时 `ctx.BlockHediffBreaksForCase()` |
| `mentalStateGivers`、强制施加（`TryStartMentalState(forced:)`、能力触发） | 无挡法 |

被测对象本身就是 hediff 崩溃的用例不开 `BlockHediffBreaks`：开启后崩溃被拦截，断言量到的是拦截之后的世界。

## 测试专用 Def / Patch 的落点：`testMod.contentDir`

产品根的 `rimrunner.toml` 里，`testMod.contentDir` 指一棵 mod 内容树，冷启动时整树拷进合成测试 mod 的根目录，这棵树下直接放 `Defs/` `Patches/` `Languages/`。改引擎假人的定义层走 `ParentName` 派生：种族派生自 `RimRunnerDummyBase`，kind 派生自 `RimRunnerDummyKindBase`。

## 多目标断言

一次判多个目标（每个 def、每个 pawn、每个方法）用 `ctx.AssertNoneOffending(label, offenders, …)`，每个违例目标一项：报告里逐项成条，终端列到上限、写出截掉几项。违例目标拼进 `AssertTrue` 的 `detail` 或 `AssertEqual` 的实际值，报告里是一整句，终端只给首行、过 400 字截断，违例目标只留在报告全文里。比名册是否全被遍历：必扫集来自扫描、不该为空的用 `AssertNoneMissing`（空即失败），名册本身可以合法为空的用 `AssertRosterAligned`；未遍历的项同样逐项成条。

## 上屏文字

断言名与说明、诊断名、截图说明、读数标题在终端上只给首行，过 400 字截断，全文留在报告里。这几处写单行短句，内容为辨别是哪一处所需的信息、运行时才知道的值；判定写法与判定理由写在调用处正上方的 `//` 注释里。诊断的值同样只写运行期数据，判读说明与场面看点写在 `//` 注释里。期望值、实际值、违例项是运行期数据，多行时终端上同样只有首行，全文看报告 `asserts`。

## IL 扫描型断言

扫描面失效，判据仍得出「零违例」，报告与真实零违例逐字相同。失效有三种：

- **无法读取 IL**：每份 `RimRunnerIlScan.Stats` 下结论前过 `RimRunnerIlScanGate.AssertReadClean` 或 `AssertScanIntegrity`。
- **程序集未纳入扫描**：遍历整个程序集的扫描过 `AssertScanIntegrity(ctx, key, stats, surface, …)`，`surface` 传这一次遍历的程序集集合（`RimRunnerIlScan.OurAssemblies()`、`RimRunnerDefScope.GaugeAssemblies`）。扫描面要并入引擎时，surface 也取并入后的那份。
- **名单偏窄**：方法名单来自反射、Harmony 补丁表、名册或别的扫描结果时，名单失效仍能通过覆盖闸。要用领域闸证明：名单非空、对独立来源逐项比对（`AssertNoneMissing`），或者在**出判决的那一次扫描**上断一个必然命中的点（被测代码里一处已知合法的调用或写口）。另做一次只为提供计数的扫描，无法证明出判决的那一次。

检测器自证（哨兵命中、对照不命中）另开一份 `Stats`：哨兵混在主统计里时，主统计失效与未失效无法区分。
