# 接入一个产品

把一个 RimWorld mod 接成能执行回归的产品：从复制样板开始，到第一条用例结果为 PASS。
样板在 RimRunner 安装目录的 `template/`，下文的路径都相对产品根（`rimrunner.toml` 所在的目录，CLI 从 cwd 往上找产品根）

## 接线顺序

1. 把 `template/` 的内容复制进产品根。样板里 `ExampleMod`、`yourname.examplemod` 是占位名，全部替换为自己的名称；
   已有 mod 工程的，删掉 `src/ExampleMod/`，测试工程的 `ProjectReference` 与描述符的 `[[builds]]` 改指自己的工程；
   描述符里 `[[units]]` 的 `source` 改指自己的 mod 目录（packageId 读自它的 `About/About.xml`）
2. 在机器级的 `config.toml` 写 `gameExe = "<RimWorld.exe 的绝对路径>"`（`rimrunner machine` 输出这份文件的路径）。游戏位置随机器而定，产品根里不写 `gameExe`
3. 改编排描述符（下节）。`rimrunner status` 能输出「产品：…」即解析通过，无法解析时返回退出码 4
4. 设环境变量 `RimWorldDir` 为 RimWorld.exe 所在的目录，编译从这里取游戏程序集。Harmony 按 Steam 的目录布局从它推出，布局不同时另设 `HarmonyAssembliesDir` 为 `0Harmony.dll` 所在的目录
5. 改测试工程里的产品描述符（`ExampleModDescriptor.cs`）：探针换成自己 mod 的入口类
6. 写名单 `RimRunner/modlists/<名单名>.toml`（格式见 `modlists.md`），描述符 `defaultModList` 写它的名字
7. 建基线档（下「建档」节）：名单还没有台账时，`run` 见档不存在时先执行一轮 `build-save`，建档这一步可与第 8 步合并
8. 改样板用例或写第一条自己的用例，`rimrunner run <Id>` 执行该用例。写用例在 `rimrunner-authoring` skill

确认接线可用（编译成功、能启动实例、能进档）且本轮不执行用例时用 `rimrunner start`：启动或复用实例并进档，保持空闲

## 编排描述符

编排描述符分四份，样板里各有一份，含该份的全部键，每个键上方一行说明：

| 文件 | 写什么 |
|---|---|
| 产品根 `rimrunner.toml` | 产品身份与构建：产品名、工作区目录 `dir`、档名前缀、缺省名单、必需 mod、`[[units]]`、`[[builds]]`、`[testMod]` |
| `<dir>/runtime.toml` | 实例运行参数：`[config]`（Config 基底与 Prefs）、`[samples]`、`[breaker]`、`[windowSize]`、`[diagnostics]`、`[experiments]` |
| `<dir>/preflight.toml` | 前置检查：`[[cold]]`（冷启动前）、`[[mount]]`（worktree 首次挂载前） |
| `<dir>/excluded.toml` | 排除名单：`[[cases]]`，任何批都不执行的用例 |

`dir` 缺省为 `RimRunner`：名单在 `<dir>/modlists/`，测试数据在 `<dir>/data/`（基线档 `saves/`、设置 `config/`、建档描述符 `newgame.toml`、期望表 `expectations/`），产出在 `<dir>/output/`。
`runtime.toml`、`preflight.toml`、`excluded.toml` 三份可以不建；不建与按样板原样保留（全是注释）等效，各键全取缺省。
必填键标「必填」。可选键整行以注释给出，值为缺省值。按说明改值，要用的可选键取消注释即可；注释整表时（`# [breaker]`），连表头一起取消注释。
缺省为「未指定」的键，值是 `<整数>`、`<字符串>` 这样的占位；取消注释后须填入值。
`units`、`builds`、`cold`、`mount`、`cases` 一项写一个 `[[…]]` 表。键写进别的文件时，报错点名该键应在的文件。

`[[units]]` 一个 mod 一项：`source` 是 mod 目录（含 `About/About.xml`），packageId 读自那份 About，`name`（载入目录名的前缀）缺省取 `source` 的末段目录名。
非可选单元自动算必需；`requiredMods` 只写单元以外每轮须装的 mod（Harmony、DLC、前置框架）。
`[[builds]]` 按编译顺序列产品与兼容程序集，被引用的在前，测试工程不写在这里，由 `[testMod]` 接在最后；`dll` 的文件名须与工程的 `AssemblyName` 一致，未指定 `csproj` 即预编译产物，只核新鲜度、不编译。

游戏位置、实例空闲时限、拍摄目录的保留配额与整机资源随机器而定，写在机器级的 `config.toml`，产品根里写这些键即拒绝；`rimrunner machine` 输出这份文件的路径与各键生效值，文件不存在即全取缺省（唯 `gameExe` 必写）。
`[instances]` 表：`batchIdleMinutes`、`shardIdleMinutes`、`filmIdleMinutes`、`showcaseIdleMinutes` 是自动实例、分片实例、拍摄实例、展台实例四类空闲多少分钟后自行退出（0 = 不自动停止，缺省见 `rimrunner run --help`），
`liveGraceMinutes` 是展台实例与拍摄实例以外的实例上驱动命令执行完后再保留的分钟数；`liveWaitMinutes`、`liveAfterWait`、`liveLimitMinutes` 依次是驱动命令的前台等待、等满之后转后台（`background`）还是取消（`cancel`）、从开始执行起的总上限（缺省都见 `rimrunner live do --help`）。转后台时终端输出 `[后台操作] 操作 <id> …`，之后用 `rimrunner live status <id>` 查、`rimrunner live cancel <id>` 取消；在别的树里执行这些操作时带原命令所用的 `--tree` 取值。
`[film]` 表：`keepRecent`、`keepTagged`、`keepFramesRuns` 是每个场面保留的拍摄目录份数（见 `rimrunner film --help`）。
其余是顶层键：

| 键 | 管什么 | 缺省 |
|---|---|---|
| `gameExe` | RimWorld.exe 的绝对路径 | 无，必写 |
| `ffmpegPath` | 拍摄用的 ffmpeg 的绝对路径 | 未指定即在 PATH 上查找 |
| `busyCap` | 同时执行的实例上限：执行批次、建档、分片组的每一片、展台加载各算一个，满即排队（行首「同时执行的实例已满」）。0 = 不限 | 0（不限）：日常并行度由 `busyReserve` 与实测 P 核空闲决定 |
| `busyReserve` | 入场后要留给编译、桌面与系统的 P 核数；0 = 不做 CPU 准入。无法读取 P 核余量且 `busyCap` 未配正数时拒绝执行 | 1 |
| `vramHeadroomMb` | 显存预留：显存用到整卡减去该键的值之后，超出的部分记进内存需求 | 2000 |
| `loadBudgetMb` | 同时加载的实例预期显存峰值合计上限，超即排队 | 1000 |
| `memHeadroomMb` | 物理内存预留，冷启动准入时从可用里扣除 | 2000 |
| `instanceMemMb` | 没有实测时的单实例内存估值（准入行写「缺省估值，无实测」）。0 = 不做内存准入 | 4000 |

描述符里路径类的值都相对产品根、用正斜杠、不含 `..`；机器级配置里的路径写绝对路径。无法识别的键当场拒，报错列出同层认得的键。缺少必填键时，报错给出该键的说明。
升级 RimRunner 之后，用自己的四份描述符与新安装目录 `template/` 里的同名文件 diff，看新增的键与已改变的缺省

## 测试工程

样板的 `src/ExampleModTests/` 即一份可用的测试工程。改测试工程时须遵守以下几条：

- 框架 `net10.0`；`AppendTargetFrameworkToOutputPath` 为 `false`，`OutputPath` 指向 `testMod.dll` 所在目录
- 引擎只经 `Import $(RimRunnerHome)\RimRunner.targets` 引用。`RimRunnerHome` 是环境变量，
  指向 RimRunner 安装目录（CLI 所在的目录）：IDE 与手动执行的 `dotnet build` 读 `RimRunnerHome`；CLI 冷启动前自行编译时按自身所在的安装目录编译，不读 `RimRunnerHome`
- 测试程序集由 CLI 每轮与引擎一起合成为一个独立的测试 mod

## 产品描述符

测试程序集里须有**恰好一个** `RimRunnerProductDescriptor` 的非抽象子类，由引擎反射发现；零个即抛出异常。三个成员都要写：

- `ProbeTypes`：产品每个程序集的一个类型（通常是各 mod 的 `Mod` 子类），用 `typeof` 写。引擎凭 `ProbeTypes` 识别哪些 Def、补丁与错误级日志属于本产品，IL 与反射类随包检查只扫这些类型所在的程序集
- `DefNameFallbackPrefix`：本产品 Def 的 defName 前缀，Def 找不到所属 mod 时据此认领
- `Configure(c)`：登记本产品的能力（期望表、场景、生成钩子等），无能力可登记时留空方法体。
  可登记的项用 `rimrunner engine find RimRunnerProductCapabilities.` 列出。
  `Configure` 里只登记类型、常量与委托，不读游戏状态。返回后再调登记方法即抛

## 多个 mod 的产品

产品由几个 mod 组成（主 mod 加兼容子 mod 一类）时，每个 mod 一个 `[[units]]`，各 mod 的程序集都列进 `[[builds]]`。

冷启动时 CLI 给各单元的 packageId 加后缀，About.xml 里含本产品各单元 id 的这几处一起改：自己的 `<packageId>`，`modDependencies`、`loadBefore`、`loadAfter`、`forceLoadBefore`、`forceLoadAfter`、`incompatibleWith`（含 `…ByVersion`）下的条目。
比对不分大小写，与游戏认 packageId 一样。本产品的 id 写在别处（`description` 与依赖项的 `displayName` 除外）时冷启动前即拒，点名那个位置。

某个子 mod 只在部分名单里装（兼容子 mod 只在对方 mod 在场时装）：

- 该子 mod 仍是非可选单元（自动算必需），不带该子 mod 的那份名单写 `[rimrunner].omit_required = ["<id>"]`
- 测试程序集里引用该子 mod 类型的代码，在该子 mod 缺席时 JIT 失败。`ProbeTypes` 按是否在场取值，引用放进 `NoInlining` 方法，仅在在场时调用；用例与夹具同理（`rimrunner-authoring` 的 `contract.md`「按名单选用例」）：

```csharp
public override Type[] ProbeTypes => RimRunnerPackages.Present("yourname.examplemod.compat")
    ? [typeof(ExampleModMod), CompatProbe()]
    : [typeof(ExampleModMod)];

[MethodImpl(MethodImplOptions.NoInlining)]
private static Type CompatProbe() => typeof(ExampleModCompatMod);
```

不属于产品、只借 `[[units]]` 部署进实例的 mod（扮演第三方 mod 的夹具、维护者用的探针）在其 `[[units]]` 写 `optional = true`，即为可选单元：名单带它才部署、改写成后缀版；不带时实例里没有该 mod，也不报错。名单闸与装载闸不核它在场，产品自己的子 mod 不用这种写法。

实例里产品各 mod 的 packageId 带后缀（区分各树的载入目录）。按本产品 mod 的 id 查询是否在场，与玩家环境里一样判在场：`ModsConfig.IsActive`、`ModLister.GetActiveModWithIdentifier` 一族与 XML 的 `MayRequire` 都接受裸 id。
例外有两处：`LoadFolders.xml` 的条件写本产品 mod 的 id，冷启动前就拒绝（`LoadFolders.xml` 在程序集装载前求值）；产品自己的 `Mod` 构造函数里查询是否在场，仍判缺席。`ModContentPack.PackageId` 读到带后缀的那一份。

## 产品自备的面

引擎不认识任何产品的期望表与覆盖关系，下面几样由产品根自己建：

- **期望表**：`<dir>/data/expectations/<id>.json`，每份在 `Configure` 里用 `c.ExpectationCaseKeys` 登记，未登记的加载即抛。格式在 `rimrunner-authoring` 的 `contract.md`
- **覆盖登记**：哪些产品功能由哪些用例覆盖。覆盖登记只由产品读，形式由产品定

## 实例 Config

实例的 `ModsConfig.xml`、`Prefs.xml` 与 mod 设置由 CLI 每次冷启动生成，缺省不带本机游戏的任何设置。Prefs 以游戏缺省为基底（语言为英文）。界面缩放不乘系统缩放（`ignoreSystemScaling = true`，要按系统缩放在 `[config.prefs]` 写 `false`）。引擎另锁定一组与复现相关的键（后台运行、自动暂停、自动存档间隔、界面缩放、窗口尺寸、音量等）。
批要在某份设置上执行就把该设置写进产品根：

- mod 设置放 `<dir>/data/config/mods/<packageId>/<Mod 类名>.xml`，产品自己的 mod 也放该目录；文件名由 CLI 按本机载入目录推算，换机无需更改
- mod 自己写在 Config 根下的文件或目录（如 `RimHUD/`）放 `<dir>/data/config/files/`，原样复制进实例；游戏自有的 `ModsConfig.xml` / `Prefs.xml` / `KeyPrefs.xml` / `Knowledge.xml` / `LastPlayedVersion.txt` 与 `Mod_*.xml` 放入该目录即拒（`设置目录（<dir>/data/config）有误`）
- Prefs 字段写进 `runtime.toml` 的 `[config.prefs]` 表，键名即 `Prefs.xml` 的元素名；引擎锁定的键写入即拒
- 只对某份名单生效的设置写进 `<dir>/data/config/modlists/<名单>/` 与 `<dir>/modlists/<名单>.prefs.toml`：前者中的同名文件整份覆盖产品根层的同名文件，`<名单>.prefs.toml` 逐键覆盖 `[config.prefs]`，未指定的键沿用产品根层的值
- 要继承本机：`runtime.toml` 的 `[config]` 写 `prefsBase = "player"`、`modSettingsBase = "player"`

`rimrunner config capture [--modlist <名单>] [--layer root|modlist]` 把本机当前的设置复制进上述位置（缺省复制进产品根层，名单层加 `--layer modlist`），用于换 RimRunner 构建之后使批仍执行在原来的输入上。
目标已存在且内容不同时，一份都不写；无法归入 mod 的 Config 根下条目只列名，需复制的条目用 `--file <名>` 指定，可在首次复制之后再补充复制。
产品根里写入但游戏未读到的（键名写错、Mod 类名不匹配）由随包检查 `CFG1` 判为未通过。带产品根内或本机输入的轮，冷启动输出 `[冷启动] 实例 Config：` 一行，写本轮各输入的来源与份数

## 存档命名与落点

存档名是 `<saveNamePrefix>-<名单名>`，建档描述符里不写档名。未指定 `--modlist` 时名单取描述符的 `defaultModList`，档名同样带后缀；
`--modlist` 与 `defaultModList` 都没有才落到不带后缀的 `<saveNamePrefix>`，该情形下的生效名单是本机游戏当前启用的 mod。
档位于 `<dir>/data/saves/`，随产品根定位；每轮复制进实例，游戏内另存无法覆写该目录下的档

非按名单构建的存档（旧版本留下的样本档、外来的档）用 `run --save <档名或路径>` 执行，查找存档的规则见 `run --help`；`build-save` 不写这份档。
通过率等台账按名单 + 档另记一本，`history --save` 用同一写法查；存档头与名单不匹配的警告级日志、读这份档本体的警告级日志，
都只列成 `[指定存档]` 观测，无需认领。只在旧档上成立的用例放进排除名单（`excluded.toml`），用到时指定该用例并加 `--save` 执行

## 建档

建档参数写在 `<dir>/data/newgame.toml`。样板里那份含全部键与说明、键整份注释，保持原样即等于内置的 `realistic` 预设；
只取消注释要改的键。某份名单需要不同取值时，写 `<dir>/modlists/<名单名>.newgame.toml`，同样只写与上层不同的键。
三层依次合并：内置预设 → 产品基底层 → 名单层；表逐键合并，数组整体替换。基底层写过的键，名单层只能写成另一个值

`build-save` 核名单：名单上的 mod 有一个没出现在产出档的存档头（建档进程没装上它），建档失败并点名该 mod。改名单或补安装之后重建才生效。

`run` 进档前看基线档是否存在。基线档不存在、且本树与主树都没有这份名单的台账：输出 `[建档]`，先执行一轮 `build-save`，建档的输出原样出现在批之前，
建成后执行本批，收尾再输出一行 `[建档] 本次命令新建的基线档：…`；建档失败即停止，批不执行。
基线档不存在而台账在：退出码 4，不自动建档。
先查原档去向：切分支时是否带上、`dir` 是否改过；找回后放回原处，找不回时用 `rimrunner build-save --modlist <名单>` 重建（不带 `--modlist` 建的是 `defaultModList` 那份档）。带 `[rimrunner].save_from` 的名单，查看与建档都针对被引名单的档
之后指定另一份名单、重建、换档后未通过的处理方法见 `saves.md`
