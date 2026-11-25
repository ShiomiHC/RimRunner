---
name: rimrunner-authoring
description: 写或改一条 RimRunner 用例时加载——能力索引（判「引擎够不到」之前先对表）、做错而结论仍为通过的判据（覆盖写宽 / 与既有用例重叠 / 前提写成断言 / 现挂补丁被内联绕过）、认领成员、写完后的验证。用例契约作者面在 references/contract.md，夹具与原语在 references/fixtures.md。执行批次、裁决、排障见 rimrunner skill。
---

# 写一条用例

## 真相源的位置

用例契约在 `references/contract.md`：两轴、开关与认领成员、登记期闸、契约闸、多阶段存读档、输出去向（诊断、样本、截图、读数正文）、五态归结、期望值落点与期望表 schema。
夹具与原语、`AddDiagnostic` 与 `AddSample` 的取舍、扫描型断言写法在 `references/fixtures.md`，用到哪个再查。场面的写法在 `references/showcase.md`：场面是立起后保持暂停的一幕，不参与裁决；用法有展台、镜头、路径录像三种。断言 vanilla 玩法（做配方、穿衣、研究解锁）时，引擎能协助的件、各件保证到哪一步，在 `references/vanilla.md`。

**作者面的规则只在本 skill。** 引擎能力在本文与各附文里未列的，按下面三步查；查到就照用。三步仍查不到的，报给 RimRunner 的提供方，由其补进附文：

1. 已知或能估计名字时执行 `rimrunner engine find <名字…>`。每个名字各输出签名、摘要与位置；查询写成 `类型.` 时列出该类型全部成员。`ctx` 的类型：回归用例是 `RimRunnerRegressionContext`，断言写在该类型上；读数用例是 `RimRunnerReadoutContext`；两者共用的成员在 `RimRunnerContext`。查询未命中时，`engine find` 按所属类型分行，列出名字相近的成员
2. 要查机制、或第 1 步找不到入口时，grep RimRunner 安装目录下的 `engine/src/`。安装目录指 `rimrunner --version` 输出的「安装目录」行所写的目录。`engine find` 输出的位置相对于安装目录下的 `engine/src/`，输出第一行写出这个源码根
3. 要骨架时，先看安装目录下的 `examples/`，再在产品根既有用例里 grep 同一个原语

---

## 能力索引：下「引擎够不到」的结论之前先对这张表

下表是引擎已有的能力面：

| 想断的事 | 引擎入口 | 契约 |
|---|---|---|
| 正在执行或正在变化的现场经存读档 | 主图成员写 `CheckpointTickBudget`；按 `TicksGame` 到期的量写 `CheckpointClockBudget`。两者都取现场里最先到期量的下界，成员都在多阶段接口上。两 phase 之间不许其他代码执行的，写 `RequiresBatchTail`。全局单例要过屏障时写 `CheckpointResources` | `contract.md` §7 |
| 多阶段用例的后续 phase 取回 phase 0 的同一个对象。对象在地上，或收在图上的容器里；容器包括建筑的容器、打包壳、pawn 的行囊与手上 | phase 末 `ctx.RegisterThing(key, thing)` / `RegisterPawn(key, pawn)` 交棒，续段 `ctx.GetThing<T>(key)` / `GetPawn(key)` 取回；交棒对象名下的 `TrackThing` 跟到末 phase 才收。不要自己存 ThingID 再按号找：那样无法取回时没有 Fail / Require 分档，收尾的跟踪也无法跟到末 phase | `contract.md` §7.0 |
| 帧耗时、音频时钟这类按真实时间计的量，或只在游戏逐帧推 tick 时才出现的现象 | 声明 `IRimRunnerRealFrameCase`，正文只写 `RunSteps`，里面 `yield return RimRunnerWait.Ticks(n)`。速度缺省 `Normal`；要按指定时长尽快执行完，显式写 `Max` | `contract.md` §7.1 |
| 等渲染这类只需几帧的 | 正文写 `RunSteps`，`yield return RimRunnerWait.Frames(n)`。无需声明 `IRimRunnerRealFrameCase`，游戏全程暂停 | `contract.md` §2.2 |
| 相机对准一格或一片之后的画面：目标是否整片落在画面内，以及视野矩形 | 正文写 `RunSteps`，`yield return RimRunnerCamera.Frame(map, 矩形或格)` 后读 `.Result`；目标图须是当前地图（`MapSite` 取 `RimRunnerMapSite.Main(理由)`），收尾还原相机 | `engine find RimRunnerCamera --full` |
| 相机扫过一片矩形（缺省全图）、逐块停留若干帧，要的是扫完之后的现象 | 正文写 `RunSteps`，`yield return RimRunnerCamera.Sweep(map, 矩形, 视距, 每块帧数)`，`.Result` 给出块数与帧数。目标图须是当前地图（`MapSite` 取 `RimRunnerMapSite.Main(理由)`），收尾还原相机 | `engine find RimRunnerCamera.Sweep --full` |
| 装配器置的整局态（叙事者 comp、异象标志一类）在多阶段用例的续段里还在，到批末才还原 | `Ensure` 里登记 `ctx.DeferScenarioRestore(还原, 说明)`；读档后要重做的进程级设置写进 `Resume` 覆写 | `contract.md` §3.2、§7 |
| 留在已启动的实例上、按名执行的一段产品侧操作（摆局面、推到某事发生再停止），或给 `live until` 一个产品侧条件；这一段不进入批次 | 写成 `<dir>/live/<名>.cs` 脚本：一个文件恰好一个派生 `RimRunnerLiveAction`（动作）或 `RimRunnerLiveCondition`（条件）的非抽象类，文件名即名字，不重写 `Id`。不放进测试工程，放进去即 RR0003 编译错误。脚本每次调用时按实例已装载的程序集编译，改完不必重编测试工程、不必冷启动；带测试工程的 global using，引擎、游戏与测试程序集的非公开成员也可访问，共用的代码留在测试工程。动作带参数表，正文写 `Run`；要推到某事发生时，正文里 yield `RimRunnerTimeDriver.WaitUntil`。`rimrunner live do <名> --名=值 …` 执行该动作；命令不带名字时列出原语、脚本与有错的脚本。名字与内置原语重名即拒，参数名以字母开头。条件带参数表，在 `Begin` 里取参数。`rimrunner live until <名> --within=<时长> --名=值 …` 判定该条件：成立返回退出码 0，不成立返回退出码 1。参数名用 `within` 或 `speed` 即拒。自动实例、展台实例、拍摄实例都受理。ctx 与场面相同：失败断言列进回执、动作继续；`RimRunnerFault.Fail` 或 `Require` 中止动作，中止后还原与清理都不执行。只用一次的读数、改状态或挂探针不写脚本，用 `rimrunner live eval "<C# 代码>"`：局部名 `ctx`、`map`，整段是表达式时输出它的值，语句里用 `return 值` 或 `Show(值, 标签)` 输出 | `rimrunner live do --help`；`engine find RimRunnerLiveAction RimRunnerLiveCall RimRunnerLiveCondition --full` |
| 点选中对象按钮栏上的一个 gizmo，按玩家点它的那条入口走（同组合点、组输入），读开关前后的状态、新开的窗口与选目标态 | 先 `Find.Selector.Select(pawn)`。`RimRunnerGizmos.Bar()` 读栏；按钮按 `Gizmo` 的类型或 `Command.hotKey` 认，按钮文字随语言变。`RimRunnerGizmos.Click(按钮)` 同步点下。点完后选目标态保持、窗口保持打开，选中由用例自己清除 | `engine find RimRunnerGizmos --full` |
| 放置物品或建筑：按 Def 放下若干物，读放不下的与玩家摆不出来的 | `RimRunnerSpawn.Place(ctx, def, 矩形, count, stuff)`，一次一个 Def。回执四项：`Placed`；`Unplaced`（找不到落点）；`Failed`（生成失败）；`Warnings`（照放但玩家摆不出来）。放下的物由引擎在收尾销毁或 DeSpawn | `engine find RimRunnerSpawn --full` |
| vanilla 玩法本身（做配方、穿衣、研究解锁） | 用例直接调游戏 API，签名从游戏程序集取。从玩家入口进入还是直调下游、世界层起点如何设置，引擎能协助的件在 `vanilla.md` | `vanilla.md` |
| 整条用例依赖某个可缺席的 mod 包（子 mod、DLC、第三方 mod） | 类上挂 `[RimRunnerRequiresPackage("<packageId>")]`：名单未列该包、且进程未装该包时，整类不实例化。只有某一面依赖时，用 `RimRunnerPackages.Present` 分支，并把该面放进 `NoInlining` 方法隔离 | `contract.md` §12.1 |
| 靶受击不损失血量：中止伤害（命中整笔不结算），或关闭伤口（照常结算、不挂伤口，可读一笔实际落下多少） | 靶规格写 `with { Damage = RimRunnerTargetDamage.Absorbed }`（中止伤害）或 `Woundless`（关闭伤口）。未经靶规格生成的 pawn 用 `RimRunnerInvulnerability.AddAbsorbed` 或 `AddWoundless` | `engine find RimRunnerTargetDamage. RimRunnerInvulnerability. --full` |
| 近战是否打中、哪一拍打中（含踢土入眼这类直挂 hediff、伤害账里没有的） | 先 `RimRunnerDamageRecorder.Watch([受击者])`，单个时改用 `Start(受击者)`，之后读 `MeleeHitTicks(攻方谓词, 受击者谓词)`。造成多少伤害，读同一类的伤害账。按受击者读账前须先 `Watch`；`Stop` 之后可照读。本窗未监控的受击者按名读即抛。谓词读口在一笔漏账同时命中两个谓词时抛 | `engine find MeleeHitTicks --full` |
| 每一次各造成多少伤害（连击类按笔比对；合计账给的是窗内总和，无法分出每一笔） | `RimRunnerDamageRecorder.Watch([受击者])` 后读 `HitsOn(受击者)`：每一次的名义量与实伤按发生先后排列，被中止伤害的那一次也在。按施加者筛用 `Hits(施加者谓词, 受击者谓词)`，结果只含施加者的那几次。`NominalTakenBy` 与 `SettledTakenBy` 是窗内合计 | `engine find RimRunnerDamageHit RimRunnerDamageRecorder. --full` |
| mote 是否出现、出现在哪 | 是否出现读 `ctx.CountObservedMotes`；出现在哪读 `CountObservedMotesNear`。落格因 tween 可偏 ±1，按邻域计数 | `engine find CountObservedMotes --full` |
| 性能读数里的 tick 分档率（视野内外结算频率不同；改相机位置无法改这一档） | `ctx.PinTickRate(RimRunnerTickRatePin.OffScreen)` | `fixtures.md` |
| 用例读产品侧的 JSON（名册、聚焦清单），某一批要指定另一份输入件时不改盘上的文件 | `RimRunnerInput.TryRead(相对路径, out 正文)`；缺失时无法执行的用 `Read(相对路径, 缺席提示)`。路径相对数据根；查找顺序为 `run --input <路径>=<文件或正文>` 指定的那份、本树、主树。指定而本批没有用例读到判红。读到的正文不跨批缓存 | `engine find RimRunnerInput --full` |
| 认领一条故障（用例禁止裸 `throw`，裸抛归 `UNCLAIMED_FAULT`） | 被测侧行为有误时调用 `RimRunnerFault.Fail`：回归用例记 FAIL 并写入通过率台账，读数用例落未完成。用例自己的前提不成立时调用 `RimRunnerFault.Require`：记哨兵，不写入通过率台账。两者都是静态入口，helper 里不传 ctx | `contract.md` §8 |
| 带 `mentalBreakMtbDays` 的 hediff 在长窗里掷中崩溃、打断读条（这一路径绕过钉住） | 单个 pawn 用 `ctx.BlockHediffBreaks(pawn)`；各族同施的场景用 `ctx.BlockHediffBreaksForCase()` | `fixtures.md` |
| 版面、图标、检查面板这类只能由人看图判定的 | 另写一条派生 `RimRunnerReadout` 的用例交截图：`ctx.AddScreenshot(说明, RimRunnerCrop.On(thing).WithPane())`，并申报 `MinTextures => RimRunnerTextures.Native` | `contract.md` §2.1 |
| 黑暗 / 天光相关 stat（用例期天光恒钉 1.0） | `using (ctx.AllowNightSky())` | `engine find AllowNightSky --full` |
| vanilla 原生行为（叙事者投放、逐帧节奏、基线档现成的殖民地、world pawn 开局）：引擎默认改变哪些，退出口一栏是哪几行 | 先对默认偏离表。退出口一栏为「无」的面，在类头 `<remarks>` 写明范围 | `contract.md` §3.1 |
| 只给用例用的 Def 或 Patch（量具零点一类）；这些内容只进测试、不进产品分发 | 描述符 `testMod.contentDir` 指的目录，冷启动时整树拷进测试 mod。def 要进 `DefDatabase` 或产品启动期索引时用该目录。只在用例内构造、不进库的哨兵，仍用 `new XDef()` 加 `ResolveDefNameHash` | `fixtures.md` |
| 只记录、不判定的过程行（补丁、夹具、真实帧用例的推进流水）。`Log.Message` 落进 Player.log，下一轮冷启动即覆写 | 改用 `RimRunnerTrace.Write`。该调用写入引擎日志，并进入本命令的 RimRunner 日志；调用不经 ctx，行首自带测试程序集名与用例 Id。要判的写断言；结论数据走 `AddSample` 或 `AddDiagnostic` | `engine find RimRunnerTrace --full` |

---

## 做错而结论仍为通过的判据

### 覆盖边界写宽，「已测」与「未看过」同形

用例类头是两条 `///`：`<summary>`（一句话说测什么，50 字内，以「。」收尾）与可选的 `<remarks>`（作用范围，120 字内）。
「测的是什么」的登记面由产品根定（清单勾选，或从类头的 `<remarks>` 现算）。
部分覆盖不得记为完整覆盖。
`<remarks>` 用肯定句把集合封上（「只验 A、B 与 C」）。

### 与既有用例重叠，「两条各测一半」与「一条测全」同形

动笔前用 `rimrunner cases find <词…>`（Id、类名、摘要、范围里取交；`--full` 输出范围）检索同主题用例并读它们的范围；
范围不重叠的两条用例仍可断同一件事，范围碰到同一面时读邻接用例的断言。

### 场景前提写成断言，「场景未搭成」与「被测侧出错」同形

断言失败记 FAIL 并写入通过率台账，读结果的人先查被测侧。
不成立时要改的不是被测侧的条件属于前提，写 `RimRunnerFault.Require`：游戏本体的行为（那一次打中、缺省值是多少）、夹具的产出、布场步骤（物放下、状态挂上）。
被测侧的条件已由别的用例断言的，在本用例里同样是前提。
判法：问两件事——这一条不成立时，要改的是被测侧吗？没有别的用例先报出来吗？两问都答「是」才写断言。

### 用重试吸收随机，「偶尔没走到」与「这次没掷中」同形

同一段施放失败后重试、直到达到预期，会把固有随机与 bug 一并吸收：某条路径偶尔不运行、观察窗口过早关闭而未观测到迟到的结算，都表现为「第一次未成功、重试后成功」，结论仍是 PASS。

随机要钉住，按这个顺序取：

1. 让那一次掷骰在结构上不发生：把概率固定为 0 或 1（vanilla `Rand.Chance` 在概率 ≤0 或 ≥1 时直接返回、不取随机数），或改靶的状态，让掷骰的那条分支不运行。
2. `RimRunnerRandControl.Seeded(seed)` 作用域（某一侧的 seed 要固定时，用 `FindSeedAllAtLeast` 找）：只在被断言的那几抽都落在同一次同步调用里时有效。跨多拍推进时全图各系统都在取随机数，断言那一抽之前被取走几次随世界状态变，seed 无法固定。

无法固定的「未执行到」判 FAIL，并把施放方状态与本窗观测作诊断输出。

### 用例里现挂的补丁，「补丁没进」与「那件事没发生」同形

用例运行期才 `harmony.Patch` 的探针、计数或覆盖，对已内联目标的调用方不生效，也不报错：计数读 0，覆盖不生效。单独执行时不出现，冷启动全批里才出现。
补丁写成测试程序集里的 `[HarmonyPatch]` 类，引擎在启动期随引导装上；平时由静态开关控制，用例开始执行时置位，收尾时清除。
启动加载里就被内联的目标（读 Def、建缓存、算 stat）改断不经它的量，读结果状态而不数入口调用。

---

## 错误级日志与警告级日志的认领

须出现的错误级日志写 `ctx.AssertLogsError`，须出现的警告级日志写 `AssertLogsWarning`；未出现即记失败断言。同一步两级都要验的，两个断言互相套在同一个 action 外（同级不能套）。会出现、出现不算错的，写 `[RimRunnerAcknowledgeLog(RimRunnerLogLevel.Error 或 Warning, "<子串>", "<理由>")]`，结论不变。执行到尾的轮里连续未命中超过该许可的 `Tolerance`（缺省 0）出 `ACK-NO-HIT`：稀疏出现的写 `Tolerance = N`，只在部分参数下走得到的在那条分支上调 `ctx.MarkLogAckUnreachable`（`contract.md`「登记期核对」）。回归用例上的错误级日志许可照常生效，另出 `JUDGING-LOG-EXEMPTION` 警告：命中的错误级日志不进本条裁决。一条许可在同一轮认领到几种正文（数字与 pawn 名归一后）时出警告：只该认领其中一种的，把子串写长；几种都该认领的，拆成各自带理由的许可。

未经认领的错误级日志自动判 `UNCLAIMED_FAULT`。未经认领的警告级日志只进 `[警告级日志]` 段，结论不变。
要守住某条警告级日志不出现，断言退路的产物：那条警告级日志表示已走退路，退路的产物与正路不同。

---

## 写完后的验证

写用例独有的两步：

1. **故意让该断言失败一次**：改坏该断言所依据的值（期望表里的条目，或用例侧的字面量），执行 `rimrunner run <Id>`，
   确认结果为 FAIL 且失败位置指名正确；改回后再执行一次，确认结果为 PASS。
   改回并得到 PASS 后插纪元标记（`rimrunner history mark <Id>`，判据见 `rimrunner` skill「通过率台账」）。
   读数用例对应的两态是未完成与 DONE。调用一次 `RimRunnerFault.Fail`，或让一张截图没拍成，确认结果为未完成且 `errorSummary` 指名正确；改回后确认 DONE。读数用例不进通过率台账，也不插纪元标记。
2. 回产品根登记覆盖，边界按「覆盖边界写宽」一节写
