# 用例契约 —— 作者面

一条用例要声明什么、引擎据此为它准备什么、结论怎么归结。本文件写契约的语义与取舍。成员签名用 `rimrunner engine find <类型>.` 查。机制读 RimRunner 安装目录的 `engine/src/`。夹具与原语在 `fixtures.md`。

---

## 1. 必答项

| 成员 | 答的问题 |
|---|---|
| 派生哪个档基类 | 主结论怎么产生、什么时候执行。档基类是 `RimRunnerRegression`、`RimRunnerReadout` 两者之一，档由派生的基类定，用例不自报档（§2） |
| `Level` | 需要什么运行环境——框架要为它准备什么 |
| `[RimRunnerGroup("<组>")]` | 归哪几组：按组选批时只选带所列任一组的用例。每个用例类都须有，可挂在基类上（§12.2） |

`Level` 取 `RimRunnerLevel` 的值，写作 `public override RimRunnerLevel Level => RimRunnerLevel.Assembly;`；各档要框架准备什么见 `rimrunner engine find RimRunnerLevel`。

### 1.1 用例名册读的成员只能写常量

CLI 开始执行前从编译出的 DLL 读下面这些成员生成用例名册，据此按 Id 选取、定档、圈定 `QuietMachine`，只认常量写法：

| 成员 | 能写的形状 |
|---|---|
| `Id`（场面另有 `Title`） | 字符串字面量或 `const string`，表达式体或块体 `return` 均可 |
| `MinTextures` / `QuietMachine` | 枚举值 / `true` `false` |
| 参数化 `Expand()`（§6） | `new RimRunnerTestCase[] { new 本类("Id", 常量…), … }` 或集合表达式 `[ new 本类("Id", 常量…), … ]`：构造本类，首参是 Id 字面量，其余实参只能是常量 |
| 场面 `Steps` | `new[] { new RimRunnerSceneStep("步名", ctx => …, after: "前驱", until: ctx => …, withinTicks: 1200, dwellTicks: 60, filmSpeed: 4), … }`：步名是字面量，`after`、`withinTicks`、`dwellTicks`、`filmSpeed` 是常量、用命名参数；动作与 `until` 写不捕获任何东西的 lambda（要用的值写成 `const` 或调本类静态方法）；集合表达式 `[ … ]` 同样认 |

---

## 2. 档

| 派生 | 结论来源 | 何时执行 | 结果 |
|---|---|---|---|
| `RimRunnerRegression` | 断言即结论 | 每轮重算 | 五态；零断言判 `NO_ASSERTIONS` |
| `RimRunnerReadout` | 没有结论，只产读数正文、截图、样本、诊断；需要判断的由查看产出的一方判断 | 只在指定 Id 时执行，不进任何批。 | 完成或未完成。读数用例不进通过率台账。 |

读数用例执行完、无故障、有产出（读数正文、拍成的截图、样本、诊断之一）是**完成**（`DONE`）；
零产出、有一张截图没拍成、或调用 `RimRunnerFault.Fail` 是**未完成**（`INCOMPLETE`）。两种故障（`UNCLAIMED_FAULT`、`SENTINEL_FAULT`）在两档都照上面的归结。
`ENGINE_FAULT` 在两档都可能出现，优先于其他态：引擎在这条用例里没做成该做的事，本条用例的结论作废。
读数用例的上下文是 `RimRunnerReadoutContext`，交产出；回归用例的是 `RimRunnerRegressionContext`，下断言。

### 2.0 两个档基类的正文

单段用例的正文在 `Run`。要跨帧的写 `RunSteps`（§2.2）。多阶段（§7）在 `RunPhase`。真实帧（§7.1）在 `RunSteps`。一条用例只取一种。各基类的成员用 `rimrunner engine find <基类>.` 查。

### 2.1 读数用例的两种正文

- **文字**：`ctx.AddReadoutText(标题, 正文)`。正文落成 `artifacts/readouts/` 下的文件，报告与产出清单只记标题与路径。只放运行期才知道、读侧算不出的内容。只看这一轮的单值走 `AddDiagnostic`。要跨轮曲线走 `AddSample`（`fixtures.md`）。
- **图**：`ctx.AddScreenshot(说明, 取景)`。说明随图进报告与产出清单。截图用例申报 `MinTextures => RimRunnerTextures.Native`。

标题与说明只写认出这一份产出要的内容（拍的是什么、运行时才知道的值、与同条其它产出的区别），40 字以内。**判定写法写在调用处正上方的 `//` 注释里**，一份产出一段。用例整体的范围仍写在类头 `<remarks>`。

取景从 `RimRunnerCrop` 入手：`On(thing)` 框对象并选中它，`Cells(map, rect)` 框一片格子，`Gui(rect)` 框一块屏幕 UI，`Full` 整屏。`On`、`Cells`、`Gui` 入手的取景再按需叠加 `WithPane()` 检查面板、`WithGizmos()` 按钮栏、`WithGui(rect)`。逐项见 `rimrunner engine find RimRunnerCrop.`。

- **界面档**：取景都能接 `WithUi(RimRunnerUiLevel)`。带屏幕部件的取景（含 `Gui` 起手的）只收 `Screen`，传入其余档即抛。四档逐档多画一些（逐档见 `rimrunner engine find RimRunnerUiLevel.`）。验「截图模式下产品的内容已隐藏」用 `ScreenshotMode`。未指定 `WithUi` 时，带屏幕部件的取景与 `Full` 取 `Screen`，其余取 `Map`。整屏去掉界面写 `Full.WithUi(RimRunnerUiLevel.Map)`。
- **拍照时刻**：图在本 phase 返回之后、`Defer` 清理之前统一拍，同一 phase 里交的几张是同一时刻的不同取景。要几种状态各一张，每种状态一个 Id（参数化用例）或同一时刻摆几份现场。
- **一张一图**：同一张截图里的对象须在同一张地图上；拍时切到那张图，拍完切回。
- **窗口与界面摆设**：拍时要切图（用例在小图上，或对象在别的图上）时，切图会重建开着的主 tab，用例体里打开的研究窗、选定的分页不会带到图里。开窗与选分页写进 `ctx.AddScreenshot(说明, 取景, prepare: () => …)`：引擎切图、设好相机与选中之后调 `prepare`。`prepare` 只布置界面，里面推 tick 或再交截图即抛。`prepare` 抛出时这张图没拍成。`prepare` 开的窗口拍完即关，`prepare` 改的选中在下一张之前还原。
- **相机与出图框按开始出图时的相机投影取**：框的是这片地面在屏幕上的外接框，改成斜视角的相机 mod 下同样能框住。
- **画在别处的格**：引擎按逻辑位置（占格、`DrawPos`）对焦与裁框。分层画法的相机 mod 把楼上的格画在地面格正上方时，对到的是空处。产品描述符登记 `DrawnCellCenter(换算)` 后，取景、场面悬停与机位跟随都按画出位置。画出哪几层由相机 mod 的视图状态定，层切换留在相机 mod。
- 要截图的写单段或多阶段用例：真实帧用例收尾即回载基线档，无法截图。

### 2.2 跨帧的正文

能在本帧做完的照常同步调用。要等帧的写 `RunSteps`：它返回 `IEnumerable<RimRunnerWait>`，`yield return` 一次等待即交还这一帧，下一次从让出处接着执行。

- **`RimRunnerWait.Frames(n)`**：让出 n 帧，期间游戏暂停、帧照常渲染。未声明真实帧的用例只用 `RimRunnerWait.Frames`：一个 phase 累计限 1800 帧（按帧计，不随帧率变），期间一旦游戏推 tick，本用例即中止并记引擎故障。收尾仍走主图还原。
- **`RimRunnerWait.Ticks(n, 速度)`**：按原版速度逐帧推 n 个 tick，推完暂停。只给真实帧用例（§7.1）。
- **`RimRunnerWait.RealSeconds(秒, 速度)`**：同样按原版速度推，推到墙钟超过传入的秒数，`.Result` 是实际的墙钟、tick 与帧数。只给真实帧用例。判据挂在真实时间上的（音频、按墙钟过期的项目）用它，不以 tick 数换算秒数：帧率低时原版每帧推的 tick 有上限，tick 会落后于墙钟。
- **跨帧原语**：返回 `RimRunnerOp<T>` 的引擎方法（如 `RimRunnerCamera.Frame`），`yield return` 该原语即推至完成，之后读 `.Result`。完成前读 `.Result`、或对同一个原语 `yield return` 两次，都即抛。调用该原语却不 `yield return` 的，在收尾记一条错误。原语改动的进程状态随用例收尾还原。
- burst 推 tick（`RimRunnerTimeDriver.AdvanceBurst` 一族）仍同步调用，`RunSteps` 里随处可用。

---

## 3. 开关与认领成员

| 成员 | 语义 |
|---|---|
| `MinTextures` | 贴图档位下限。读数受渲染影响（帧时间、显存、加载时长）的用例申报 `Native` |
| `QuietMachine`（读数用例的成员，默认关） | 开 = 本用例开始执行时机器上没有别的批：CLI 开始执行前等别的批收尾，独占到报告消费完。报告条目 `otherBusyCores` 记用例窗内本机其他进程正在使用的核数均值，锁外的负载由该条目读出 |
| `MapSite` | 在哪张图上执行，缺省按档定（`rimrunner engine find RimRunnerMapSite.Default`）。独立小图省的是主图那份每 tick 成本，生成每槽付一次。小图用例的用例体内，vanilla 挑家园图的入口（`Find.AnyPlayerHomeMap` 一族、任务挑图）只挑本用例持有的小图。用例自己取图用 `ctx.Map` |

**小图上的用例**：小图是一张无人查看的地图，视野矩形（`CameraDriver.CurrentViewRect`）是主图相机的坐标，与真实游戏里相机停在别的图上时一样。
以视野判定玩法的代码不先判当前地图，在小图上与玩家的多图存档里同样读到错误的值。要测「玩家正看着」的行为，覆写 `public override RimRunnerMapSite MapSite => RimRunnerMapSite.Main("<留主图的理由>");`，
用 `RimRunnerCameraFixture.LockOn` 把目标锁进视野（`LockAwayFrom` 锁离屏）；小图上的锁在 `ctx.AsCurrentMap` 内读才生效。
真实帧用例例外：在小图上时引擎整段把小图设为当前地图、相机对准图心，帧内绘制与相机都属小图，按相机判档与真实游戏同口径。

**小图落在替补层上时**：基线档世界里主殖民地周围找不到同 biome、无变体、无污染的空地块，小图改落在引擎注册的替补层上（首张在与主图同号的格子，其余的在邻近格子），
报告逐条 `onParallelSite` 为真，终端 `[并行落点替补]` 行列出 Id。同号格子上 biome、经纬度、季节、时刻、日照与主图相同。逐日温度随机项与主图不同，从同一份基线档起逐轮相同。
这一层不是根地表，vanilla 按层判的内容在这里可能取另一分支：带 `layerWhitelist` 的到达方式与事件（空投类到达、围攻、迫击炮、部分仪式）
不在该 `layerWhitelist` 上，按 `IsRootSurface` 判的同样不通过。商队不跨层。只涉及图内模拟的用例不受影响。断言依赖这些内容的，在 `[并行落点替补]` 列出的轮次里先看落点，
要稳定复现，换用一份主殖民地周围有空地块的基线档。

### 3.1 引擎默认偏离 vanilla 的面

下表的世界初态与叙事者停用（缺省下叙事者不投放，用例窗内入队的事件推迟到收尾，见 `LiveStoryteller`）是读数确定、用例间隔离的来源。只关掉其中一项时，读到的是上一条用例留下的世界，不是原生世界。
成套的退出口是真实帧用例（`IRimRunnerRealFrameCase`，§7.1）的 `LiveStoryteller`：先回载基线档，再把世界初态与叙事者一并放开。
要测的 vanilla 行为落在下表哪一行，就按那一行的退出口写。退出口一栏是「无」的，那一面的原生读数不在本引擎的测量范围内，在用例类头 `<remarks>` 写明范围。

| 面 | 默认 | 退出口 | 没退出时读到的 |
|---|---|---|---|
| 世界初态 | 图面起点是「基线档清扫装配后」；世界层与整局组件的起点含基线档原有条目与装配写入条目，多阶段用例另含同屏障批别的成员 phase 0 写入的条目（分步见表下） | 真实帧接口上声明 `LiveStoryteller`：跳过装配，在基线档原样世界上执行。其余要殖民者的须自行生成 | 按地图扫描取「现成的殖民者」落空；用例自己布下、在用例体内还要读的对象仍在，收尾时才收 |
| 逐帧节奏 | 单段与多阶段用例体内推 tick（`RimRunnerTimeDriver.AdvanceBurst` 一族）在同一帧内推完，帧时间保持不动。每 tick 前 pawn 的绘制位置重置到移动插值根（所在格与下一格按移动进度插值），同 vanilla 6 倍速以上 | 真实帧用例（§7.1）：游戏自己逐帧推 tick | 绘制位置只有 6 倍速及以上的跳格，没有 1 倍速与 3 倍速的平滑滞后。帧时间恒定。由逐帧（`Update`）驱动的逻辑要等推进结束才执行 |
| world pawn 冬眠 | 载入基线档时，可冬眠的 world pawn 随载入移进冬眠集合。vanilla 要等到第一个 15000 整点 | 无 | world pawn 在开局那一段少调用 `DoTick`；读 world 侧逐 tick 行为的读数与玩家开档后的最初一段不同 |

世界初态的默认分这几步：

- `Level=World` 的用例开始执行前都过一遍环境清扫与产品场景装配（每条用例都过）。
- 清扫由引擎做（`RimRunnerEnvironmentSweep.SweepAll`）：基线档的殖民者、动物、休眠 pawn、尸体、火、产 pawn 的敌方建筑全部清除。装配做什么由产品场景装配器定。
- 收尾时引擎把主图还原到本用例开始执行前（多阶段用例按整个屏障批一段）：留下的 pawn、物、建筑、污物清除，地形、屋顶、污染、区域逐格回写。无法还原的（基线物被毁或挪动、揭雾、删区）记残留并回档。
- 图、世界、整局三层组件另按存档视图与开始执行前比，不匹配时怎么修见 §3.2。
- 世界层（研究、发现、条件、派系、world object、产品登记项）另按开始执行前比，只比较、不回写、不判定：差记进 RimRunner 日志，本条未通过时附进失败指纹。

### 3.2 组件状态不匹配时按写入方选出路

图、世界、整局三层组件（`MapComponent` / `WorldComponent` / `GameComponent`）在开始执行时与收尾时（收物、回写之后）各空存一次，按存档视图比，与主图还原同段（多阶段用例整个屏障批一段）。被测产品的组件不匹配时出 `PRODUCT-COMPONENT-DRIFT` 警告；世界组件与整局组件不回档，偏差带入其后的用例，其后有用例因此未通过时回查这条警告。图组件不匹配时另回档；他方的世界组件与整局组件只记残留。

引擎无法判断写入方，先读组件源码判是哪一类：

| 写入方 | 出路 |
|---|---|
| mod 本体滞留的失效条目（已回收对象的登记、拆掉的对象的余值） | 在组件里清：Saving 分支剪枝，或对象退场时摘 |
| 逐 tick 自然变的字段 | 产品描述符里 `ComponentStateExemption(组件类型, 元素名…)` 登记，按元素豁免 |
| vanilla、DLC 或他 mod 的组件到点才给字段赋值，档存在那之前（报的是多出一个元素，原有元素不变） | `build-save` 建档时量出这类元素自动豁免；档是这一机制之前建的就重建。只有产品用例才触发的，建档时不在测量范围内，仍按未豁免上报 |
| mod 按设计记下的整局进度（解锁记录、单向旗标；生成被测体就会写） | 产品场景装配器 `RimRunnerScenario.Ensure` 里整份拍下、`ctx.DeferScenarioRestore` 写回。产品根建有夹具名册（RimRunner 安装目录的 `tools/FixtureIndex/`）的，先按组件名在夹具名册里找现成的快照还原夹具 |

整局进度在装配器里还原，不逐用例用 `Defer` 补：只有本批第一个写出某条的用例被列出。装配器在组件基线开始记录之前调。

`DeferScenarioRestore` 与组件比对同段执行：单阶段用例在本用例收尾时、多阶段用例在整个屏障批收尾时，都在比对之前。写回用 `ctx.Defer` 时，多阶段用例的写回落在 phase 0 末、屏障存档之前：存进档的是提前还原过的局面，续段里写入的条目无人收取。

- 只能在 `Ensure` 里调，别处调用即抛。
- 写回的值取 `Ensure` 返回那一刻的：同一份状态先归一再拍快照。先拍后归一时，写回的是归一之前的值，与组件基线不匹配。
- 写回可能在屏障读档之后才执行，那时 `Game` 已更换：回调只捕获值，整局对象执行时用 `Find.*` 现取。
- 只写回整局状态。图上的物件仍用 `TrackThing` / `Defer` 收：屏障批收尾时写回排在收尾成员的残留判定之后。

### 3.3 按名绑定的成员

随包检查 HANDLE1 在每批里检查产品程序集与测试程序集。`static readonly` 的反射句柄（成员对象、委托，含 `AccessTools.FieldRef` 一族）在静态构造运行后不得为空，静态构造不得抛出；非 readonly 的静态字段（惰性缓存、补丁期才赋值）跳过核对。Harmony 特性须写在类上：方法上有 Harmony 特性而类上没有时，`PatchAll` 跳过整个类；类上带 Harmony 特性（含继承来的）的类须在补丁表里有补丁方法。静态构造里用 `AccessTools.TypeByName` / `Type.GetType` 按字符串名探测别的 mod 类型、且类型名在本进程无法解析的类，这些类的空句柄跳过核对、只进诊断（兼容代码探测缺席的 mod 即属这种情形）。`harmony.Patch` 手挂的补丁与只有反向补丁（`[HarmonyReversePatch]`）的类在 HANDLE1 的范围之外，反向补丁不进补丁表。

| 情形 | 出路 |
|---|---|
| 句柄设计上允许为空（有退路，且不属于上面按名探测的那一形） | 声明成可空引用（`FieldInfo?`，产品开启 nullable 时）；没开 nullable 的在产品描述符里 `OptionalHandle(类型全名, 成员名, 理由)` 登记 |
| 补丁类设计上不经 `PatchAll` 挂上（`Prepare` 返回 false、产品自己手挂） | 产品描述符里 `InactivePatchClass(类型全名, 理由)` 登记 |

类型全名与 `Type.FullName` 相同，嵌套类型用 `+` 连接；只读自动属性写属性名。登记不匹配任何句柄或补丁类时 HANDLE1 判红；引用本轮缺席包的类型整类跳过核对，登记在被跳过类型上的例外本轮无法判断是否陈旧。

测试程序集的补丁类由引擎逐类安装、逐类捕获异常，一个类安装失败不影响其他类。特性或签名引用的程序集在本进程无法解析、而该程序集不算本轮缺席的类型，HANDLE1 无法判断是否为补丁类，只进诊断；诊断写明是哪一种情形：不属于任何产品单元、所属包写在名单里却未装入、所属包不在名单里却已装入，或所属包已装入而该程序集未装入进程。

测试程序集的补丁目标不能是共享泛型方法（泛型实参里有引用类型）：方法重编后补丁静默失效，启动时报错。改挂该泛型方法的调用方或该泛型方法调用的非泛型方法，或实参全为值类型的泛型。

---

## 4. 登记期核对

登记期按后果分：照字面执行后的结论、批的组成、其后用例的世界，任一**必然**与声明不匹配的，拒该条（终端 `[用例拒绝/契约闸] <Id>`）或拒批（`[拒绝/登记期]`）。声明只是不起作用或写法少见时出警告，结论仍按原归结。拒绝正文写出成员与违反的约定。警告键全表：`rimrunner engine find RimRunnerWarningKey.` 列常量名、键值与摘要；认领写的是键值（终端 `[LEVEL-OVER]` 这种形状）。

观测到的正是用例有意为之的（例如用例本来就要基线档里的 pawn 或建筑打靶，出 `TARGET-FOREIGN-DAMAGE`），在类上 `[RimRunnerAcknowledge]` 认领这个键并写理由。`ASSERT-CONSTANT-TRUE` 的出路是改断言：这一段因场景不满足而跳过时写 `ctx.AddDegradedNote(理由)`。整条不适用时写 `NotApplicableReason()`。

有四个键的警告行首标出读点或发起方的归属：`CURRENT-MAP-READ`、`FROZEN-MAP-SPAWN`、`STORYTELLER-BLOCKED`、`UNITY-STUB`，形如 `[用例]`、`[产品]`、`[vanilla]`、`[mod:<packageId>]`、`[程序集:<名>]`。
认领这四个键要写 `Origin`，照行首方括号里的字抄，只认领这一个归属：`[RimRunnerAcknowledge(RimRunnerWarningKey.CurrentMapRead, "<理由>", Origin = "mod:<packageId>")]`。
几个归属各写一条。`CURRENT-MAP-READ` 未指定 `Origin` 的认领不生效、出 `INERT-DECLARATION`；别的键带 `Origin` 同样出 `INERT-DECLARATION`，它们的警告不带归属。
已认领的 `CURRENT-MAP-READ` 归属，读点首次截栈后改道只计次，不再占截栈上限；未认领的读仍按截栈处理，所以认领一个第三方读点不会把本条别处的读一并压制。
按归属认领的宽度是整个 mod：本条里经产品代码调进那个 mod 发生的读（读到的值交给产品），也一并认领。
他 mod 挂在原版代码上的读（行首方括号后标 `环境链`）不落在用例上，本批内按链并成一行，由名单按链锚认领（`rimrunner` skill 的 `modlists.md`「`acknowledge`」），不必在用例类上挂；
类上认领那一归属的用例，它的这类读仍落在本条、按类上的认领走。

认领说的是「这件事会在本条里出现，出现不算错」，所以要它出现：用例执行到尾的轮里连续未命中的轮数超过认领的 `Tolerance`（缺省 0，即执行到尾而没命中的那一轮），出 `ACK-NO-HIT`。按 mod 名单分别计数，命中一次清零。日志许可同一套。
`ACK-NO-HIT` 不可认领，三种出路：认领的现象已不再出现就删掉认领；出现得稀疏（随机落点、线程竞态）就按它多少轮出一次写 `Tolerance = N`；只在部分参数下才走得到（聚焦轮没点那一列）就在用例体里那条分支上调 `ctx.MarkAckUnreachable(键, 理由[, origin])` 或 `ctx.MarkLogAckUnreachable(级别, 子串, 理由)`，这一轮不计。
`Origin = "mod:<packageId>"` 的认领在那个 mod 本轮未装载时自动不计，不用为此声明。声明够不到却命中，出 `ACK-REACH-HIT`，命中仍计入：改正声明的条件。登记期判的键（`LEVEL-OVER` 这类，认领而本轮没出即报 `INERT-DECLARATION`）不计连续未命中；这类键上写 `Tolerance` 即出 `INERT-DECLARATION`。名单里的认领不计。

---

## 5. 契约闸（两轴申报的真伪）

只看声明就能确认的不匹配拒该条；要看调用闭包推断的出警告。下面这条警告的出路取决于闭包外的事实，警告正文里没有这条出路：

- **`LEVEL-OVER`** 是推断：闸只按调用闭包认档。反射读写、vanilla 基类型上的 virtual 派发（经 vanilla 的 `ThingSetMaker`、`StockGenerator` 调到的 override）与载档在闭包外，不进这次认档。确认读的是这些 → 认领，理由写读到什么；一批用例同轮出现同一形状的 → 先查闸的盲区，再修改申报。

---

## 6. 参数化用例

一个类产多个 Id 的唯一形态：

```csharp
[RimRunnerParameterizedCase]
public sealed class SomeCase : RimRunnerRegression
{
    public static RimRunnerTestCase[] Expand() => new RimRunnerTestCase[] { /* … */ };
}
```

可分片接口 `IRimRunnerShardable` 只给读数档：回归档的各片结论没有合并环节。回归用例要按格摊开，写成参数化用例，每格一个 Id。

挂标记就改为调用 `public static Expand()` 取实例，写法只能是 §1.1 那一种：CLI 开始执行前从 DLL 读出展开的 Id。参数化用例的 `Id` 读构造参数存进的实例字段：`private readonly string id;` 加 `public override string Id => id;`。

---

## 7. 多阶段：跨存读档屏障

**存 → 读 → 比对的往返是引擎原语**。跨存读档的用例派生 `RimRunnerRegression`（或 `RimRunnerReadout`），实现 `IRimRunnerPhasedCase<TContext>`，TContext 取本档上下文（回归取 `RimRunnerRegressionContext`），正文在 `RunPhase`：

```csharp
public sealed class SomeBarrierCase : RimRunnerRegression, IRimRunnerPhasedCase<RimRunnerRegressionContext>
{
    public int PhaseCount => 2;
    public IReadOnlyCollection<string> CheckpointResources => Array.Empty<string>();
    public bool RequiresBatchTail => false;
    public int? CheckpointTickBudget => 1;
    public void RunPhase(RimRunnerRegressionContext ctx, int phase) { /* 存读档由引擎在 phase 之间做 */ }
    /* Id / Level 照常 */
}
```

各成员的语义与缺省用 `rimrunner engine find IRimRunnerPhasedCase.` 查。两个预算的取值：`CheckpointTickBudget` 取现场里**最先到期**那个量的常量下界（含被测量之外未钉住的愈合、衰减、休眠销毁），估不出写 1；按 `TicksGame` 判到期的量（冷却、到期戳）用 `CheckpointClockBudget`，同样取最先到期量的下界。

**读档期按设计必出错误级日志**（被测侧在读档时报错是期望行为）：用 `[RimRunnerAcknowledgeLog]` 认领那一行，并声明 `RequiresSoloBatch => true`。同批成员共用存档窗与载档窗，窗里的日志记到每个成员头上，许可只认领本用例那一份。不独占时，同批别的成员全按这一行判 FAIL。每条声明者让一轮多一次存读档，只给按设计必出日志的用例用。与 `RequiresSoloBatch` 并存的批尾、资源键、两个预算都不起作用，写上即出 `INERT-DECLARATION`；留主图的 World 级用例声明后可以不写 `CheckpointTickBudget`。

**读档刚还原、还未推过首拍的状态**：phase 体看到的图已推过读档后那一拍，「读档时就已还原」与「首拍里才补上」在 phase 体里无法分辨。独立小图上的成员可写 `BeforeLoadTick(ctx, phase)`：续段领回小图之后、引擎替它补推那一拍之前调，与 phase 体同一个 `ctx`，交棒对象已可取回，里面不能推 tick。留在主图的用例写该成员，登记期即拒：主图那一拍在 vanilla 载档收尾里就已推。

**续段的装配**：`Ensure` 只在 phase 0 调。读档会按定义重建一部分整局对象（`Storyteller` 的 comp 一类），phase 0 的归一不随存档传递。要在续段重做的归一写在装配器的 `Resume` 里：World 级多阶段用例每个续段读档后、续段体之前调。小图上的成员在引擎补推读档那一拍之前调，主图上的成员在那一拍之后调。`Resume` 里不推 tick，也不能调 `DeferScenarioRestore`。单阶段用例与场面只走 `Ensure`，归一在 `Ensure` 里仍要做。

### 7.0 交棒：跨 phase 取回同一个对象

读档换掉整局 `Game`，phase 0 手里的对象引用到续段都指向旧世界；`ctx` 也逐 phase 新建。跨 phase 要用的对象在 phase 末登记：物用 `ctx.RegisterThing(key, thing)`，存活的 pawn 用 `ctx.RegisterPawn(key, pawn)`。续段用 `ctx.GetThing<T>(key)` / `ctx.GetPawn(key)` 取回读档后的新对象。

- **找得到哪些**：物在全部地图的地上，或收在图上某个容器里（建筑的容器、打包壳、pawn 的行囊与手上），都按 ThingID 取回；pawn 只找已生成的。读档后是否在地上由用例自己断。
- **无法取回时怎么判**：交棒过却取不回、登记过却已销毁或不在任何地图上，判 `Fail`（丢失可能出在被测侧的存读档上）。phase 0 取没登记的键判 `Require`（装配没登记或键名写错）。续段取没登记也没交棒的键判 `Fail`；续段要区分「上一 phase 没走到交棒」，用 `TryGetThing` / `TryGetPawn`。自己存 ThingID 再按号找，取不回时没有 `Fail` / `Require` 分档。
- **收尾随交棒对象带过屏障**：交棒对象名下的 `TrackThing`、`DeferUntilRecycled`、钉住与需求冻结不在 phase 末执行，带到末 phase 才收。末 phase 登记的不再豁免，照常回收、照常报残留。
- **容器的持有者也要交棒**：只交棒容器里的物、不交棒持有它的建筑或打包壳时，持有者在 phase 末作为本用例的残留报出。
- **不交棒的**：phase 末照常清理、照常报残留。ThingID 以外的数据（格坐标、组号、计数）用 `ctx.WriteCheckpointState` 带过屏障。

**断全局表的计数断增量**：世界层与整局组件不回写（§3.1 世界初态行）。本用例 phase 0 之前，表里已有条目，其中含同屏障批别的成员在 phase 0 写入的条目。
断「表里有 N 条」只在本用例独占该表时成立。同批再有一个写同表的成员就判 FAIL。phase 0 先记基数，`ctx.WriteCheckpointState` 带过屏障，末 phase 断增量；
或在 phase 0 开头用 `RimRunnerFault.Require` 核起点（表为空、没有某键），起点不对时认领成前提不成立，归 `RimRunnerFault.Require`，不归被测侧失败；
两 phase 之间要独占同一张表的，声明 `CheckpointResources`。

**屏障上的未通过怎么处置**：
- **`CHECKPOINT-DRIFT`，或「批内未通过、独占一批重新执行后通过」**：按 `TicksGame` 判到期的量声明 `CheckpointClockBudget`；按图拍数判到期的量收紧 `CheckpointTickBudget`（只对主图成员）。被测量之外随时间走的过程（再生愈合、休眠销毁、衰减）在夹具里钉住，或在续段开头用 `RimRunnerFault.Require` 核现场；加大余量（多挂一道伤、多留几百 tick）不能替代钉住，窗口变长后仍未通过。与同批成员争用同一全局状态的，声明 `CheckpointResources`；两 phase 之间不许其他代码执行的，声明 `RequiresBatchTail`。
- **`HANDOFF-DRIFT`**：变动落在执行过同批成员代码的窗里时，同批成员布场避开 `ctx.IsHeldForCheckpoint` 为真的对象，或本条声明 `RequiresBatchTail`。窗里只有存读档与读档后补推的 1 tick 时，变动出自被测侧的存读档（`ExposeData` / `PostLoadInit`）或那 1 tick。

### 7.1 真实帧用例

帧耗时、音频时钟这类按真实时间计的量，以及只在游戏自己逐帧推 tick 时才出现的现象：声明 `IRimRunnerRealFrameCase`，正文写 `RunSteps`（§2.2），在里面 `yield return RimRunnerWait.Ticks(n, 速度)`，判据挂在真实时间上的写 `RimRunnerWait.RealSeconds(秒, 速度)`。要在第 N tick 做的动作写在推到那一刻之后的代码里，采样写成「推一段、读一次」的循环。成员与速度档用 `rimrunner engine find IRimRunnerRealFrameCase. RimRunnerFrameSpeed. RimRunnerWait.` 查；`WallClockTimeoutSeconds` 从正文开始执行起算，超时只停止本条，批接着执行下一条。`Superfast` 的倍率随游戏状态变，引擎保持游戏给出的倍率。
测帧耗时、音频时钟这类按真实时间计的量用缺省的 `Normal`；只求尽快执行完的显式写 `Max`。用过的速度写进报告条目的 `frameSpeed`，多档以 `+` 相连。

按档进批：回归档的随回归批（裸 `run`）执行。读数档的只在指定 Id 时执行。排在队列最尾，每条收尾回载基线档（§11），所以只在自动实例上执行。

---

## 8. 五态归结

`RimRunnerFault.Fail` 在 `Readout` 上落未完成，理由进 `errorSummary`；`Require` 两档都落 `SENTINEL_FAULT`。

不能排除被测侧的归 `RimRunnerFault.Fail`：误归 `RimRunnerFault.Require`，真回归离开台账，终端记成 `SENTINEL_FAULT` 而不是 `FAIL`。误归 `Fail`，台账多一条带 `subject` 的可见噪声。`Require` 只给确定与被测侧无关的（文件不在盘上、用例名册缺条目、参数越界）。拆成两次调用只在代码里有现成判别量时做（判定次数、已打完的场数）。没有现成判别量就把判别信息放进 `subject`。

引擎的夹具助手在基线档、地图或数据根不满足前提时已按 `Require` 认领：取派系（`RimRunnerFactions.FindNeutral`、按立场生成的靶、`RimRunnerRaid.GenerateSquad` 的派系不在存档里或没有可用派系）、找落点（`RimRunnerSpawnAt` 的出界 / 不可站 / 距离带内无格、`RimRunnerPawns.AnchoredDropSpot`、`RimRunnerBuildingCells`、`RimRunnerClearRect`、`RimRunnerRaid.Deploy` 的边缘进入格）、`RimRunnerExpectationStore.Load` 的期望表缺失。调用点之后不再判空或补断言。它们不认领的（规格互斥、null 实参、指定格上已有 pawn、指定的派系不满足袭击判据）仍直接抛。

---

## 9. 期望值的落点

期望镜像单一上游字段（某个 Def 或 Props 的值）时现读该字段，末参写 `RimRunnerExpectationSource.FromDef(<字段路径>)`。

---

## 10. 期望表

- 每份表在产品描述符 `Configure` 里 `c.ExpectationCaseKeys(表 id, 列…)` 登记自己 `cases` 允许的列。多数表的列是 `caseId` / `expected` / `tolerance` / `source`。
- `cases` 里的条目是断言。文件级算法参数（下限、margin 一类）走表级 `params`。比较用的 `tolerance` 留在 case 字段。
- 文件是 `<dir>/data/expectations/<id>.json`，用例里 `RimRunnerExpectationStore.Load(id)` 读。该文件经 `RimRunnerInput` 读 `expectations/<id>.json`；用 `run --input expectations/<id>.json=…` 指定输入件，同样能改变用例读到的正文。根对象的 `id` 须与文件名相同。`params` 每项写 `value` 与 `source`。`cases` 每条必有 `caseId`、`tolerance`（≥ 0）、`source`。条目里出现的每一列（这三列在内）都要登记。可登记的列只有这三列，以及 `checkKind`、`subject`、`hediffDef`、`stageIndex`、`statDef`、`expected`。`cases` 与 `params` 不能都空：

```json
{
  "id": "XYZ1",
  "note": "可选，说明这份表",
  "params": { "minHits": { "value": 3, "source": "设计文档：至少三连击" } },
  "cases": [
    { "caseId": "steel", "subject": "Steel", "expected": 1.5, "tolerance": 0.01, "source": "Defs/Things.xml 手抄" }
  ]
}
```

  `Configure` 里写 `c.ExpectationCaseKeys("XYZ1", "caseId", "subject", "expected", "tolerance", "source")`。

---

## 11. 执行槽与批序

槽的顺序是 `Normal`（单阶段）、`CheckpointBarrier`（多阶段：主图上的批在前，小图上的批在后，不混编）、`IsolatedMap`（在独立小图上执行的单阶段）、`RealFrame`（真实帧用例，排队尾）。
槽内按 `Level`（`Assembly` → `Defs` → `World`）→ 类型名字典序，参数化实例保持 `Expand()` 顺序。
多阶段那一段再按排批结果重排（批序、批内 phase 0 序）。单阶段的独立小图那一段恒在多阶段那一段之后。
独立小图那一段期间主图冻结，`TicksGame` 仍推进。声明主图的真实帧用例仍在主图上执行，按时间戳判的逻辑要算上那段冻结。
每批都从基线档开始：冷启动是刚载入的基线档，复用实例的批开始执行前先回载一次。批内各条按本节的槽序共用一个世界。图面靠每条收尾的还原（§3.1）。世界层与整局只比不回写。

---

## 12. 按名单选用例

### 12.1 依赖某个可缺席的 mod 包

整条用例依赖该包：类上挂 `[RimRunnerRequiresPackage("<packageId>")]`。名单未列入该 packageId、进程也未装上该包时，整类不实例化。只有某一面依赖：把引用提取到签名不含该包类型的 `NoInlining` 方法里（隔离），调用处以 `RimRunnerPackages.Present` 包住；在场判定写在调用处。写进隔离方法体内时，JIT 仍会在缺席时编译到该包的类型。
声明依赖的用例调用得到的助手不必再声明：该助手只在包在场时执行。
基类、接口与值类型字段不能用可缺席包的类型：缺该包的名单下整个测试程序集无法装入。这些位置改用恒在的类型，用到该包类型的代码挪进声明依赖的类或隔离方法。

**引擎在带着那个包的名单下查**（包缺席闸），查出来的是警告（`ABSENTABLE-*`）：「可缺席」= 产品名单目录（`<dir>/modlists`）下至少一份能单独执行的名单不含该包。只有一份名单时跳过这项检查。违例只在可缺席包装着的那一轮能判定。

### 12.2 用例组

类上挂 `[RimRunnerGroup("<组>")]`，可挂多个、随基类继承（挂在中间基类上即整族带组）。组名只许英文字母、数字、`-` 与 `_`，`all` 留作「不按组过滤」，组名不合格即编译失败，输出 RR0002；不预先登记，用例名册里有用例带着的就是已有的组。`rimrunner run --group <组>,…` 与名单的 `[rimrunner].groups` 按组选批时，只选带所列任一组的产品用例；随包检查不受组过滤。每个用例类都须有组，回归档、读数档与排除名单里的用例一样须有组，环境体检（`[RimRunnerEnvCheck]`）除外：无组的类编译失败，输出 RR0001。`rimrunner cases groups` 列各组的用例。只有一个用例类带着的组标 `[WARN]`：核对该组名是否拼错。组只决定进不进批。
