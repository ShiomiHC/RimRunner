---
name: rimrunner
description: rimrunner CLI 的使用要点（命令面在 --help）：日志落点、通过率台账的纪元标记。需要执行 RimWorld 游戏内测试、或需要对一批回归结果下结论时查看。新产品接入回归、建档、排障、结果裁决各见 references/ 下的附文。写或改用例见 rimrunner-authoring skill
---

# rimrunner

游戏内自动回归的编排 CLI

**命令面、开关、哪些开关不能同时传入全在 `rimrunner --help` 与 `rimrunner <命令> --help` 里，退出码在总览页**

命令名 `rimrunner` 是 RimRunner 安装目录下的 `rimrunner.exe`。

## 落点

|        |                                                                 |
| ------ | --------------------------------------------------------------- |
| RimRunner 日志 | 产品**主树** `<dir>/output/reports/rimrunner-log/<时刻>-<命令>-<runId\|norun>.log`。`run`、`start`、`build-save`、`showcase`、`film` 各写入一份，worktree 里执行的也写入主树；同名已存在时文件名再加 `-<进程号>-<命令号>`。轮换保留 500 份。`<dir>` 是编排描述符 `dir` 指的工作区（缺省 `RimRunner`）。其余命令存在只记行、或有行被截断时，写入机器级的 `~/.rimrunner/rimrunner-log/<时刻>-<进程号>-<命令号>.log`；文件名不带命令名与 runId。其余命令只有未截断的上屏行时，终端输出即全部记录。有 RimRunner 日志的命令，终端是日志的子集。报告归档轮换保留，修剪后仍保留的现场在 RimRunner 日志 |
| 引擎日志 | 引擎日志并进 RimRunner 日志（`引` 道），不另成文件。条目经实例管道从实例推来；本命令期间的条目并入本命令的 RimRunner 日志。命令之外的条目（空闲、停止时）续在该实例最近用过的那份 RimRunner 日志末尾，位置在 `命令结束；实例 <名> 此后的引擎行续在下面` 一行之后。编排进程装载引擎的输出与测试侧诊断流水同在引擎日志：测试程序集（用例、夹具、Harmony 补丁）经 `RimRunnerTrace.Write` 写的过程行，行首自带 `[<测试程序集名>][<用例 Id>]` |
| 拍摄产物 | `film` 每次拍摄进 `<dir>/output/film/<场面Id>/runs/<时间>-<runId>[-<tag>]/`，终端的产物行输出的就是这份。场面目录上的 `<名>.latest.<格式>` 是下一次拍摄就覆盖的最新副本（`--roll` 的候选只写入本次 `runs/`）。发给人看、用于改前改后对比时，取 `runs/` 下的路径。`film compare` 拼出的左右对照在 `film/<场面Id>/compare/` |
| 实例 Player.log | `status` 输出的 `savedata：` 目录下的 `Player.log`，**每次冷启动覆写**，只是当次的临时诊断现场。被测 mod 自己的日志，以及错误级日志与警告级日志，都在 `Player.log`，测试侧 `Log.Message` 也写入 `Player.log`，下一轮即丢失。引擎载入之后的行不带栈，错误级日志的栈在 RimRunner 日志的 `引` 道 |

## 同一棵树上并发执行

同一棵树可以同时执行几批（几个会话共用主树，或一个会话在不同名单上同时执行）：常驻自动实例不可用时，`run` / `start` 改用临时自动实例，上屏 `[另起]` 一行，不必另开 worktree。之后按本会话那条命令的 runId 处理：

- **结果**（`run`）：`judge <runId>`。`reports/latest.json` 是最后写完的那一批，不一定是本会话那一批
- **点名**：落定实例那一行（`[冷启动]`、`[复用]` 或 `start` 的 `LOAD ONLY`）写着 `后续点名：--run-id <runId>` 时，本树有不止一个自动实例。`live` 带 `--run-id`，不带即拒；`stop --run-id <runId>` 只停止那一个实例，零参 `stop` 停止本树全部实例，含别的会话正在使用的实例

`build-save` 只用常驻实例，常驻实例忙即拒。

## 通过率台账

台账文件是 `<dir>/output/history/passrate-<名单名>.json`，读写经 `rimrunner history` 完成。`run --save` 的批另记一本，`history --save` 查。

**纪元标记**（`history mark <用例Id>`，开关见 `--help`）：通过率只算标记之后的格。
换档标由引擎按基线档内容指纹自动插入（条带上的 `¦`），只作分界；样本与读数跨换档标仍累计，见 `saves.md`。
判据是「不插入，通过率就失真」：旧格反映的不是用例与被测代码现在的样子。修复并得到 PASS 之后插入，常见三种：

- **故意失败的那几轮**：写用例时「故意让用例失败一次」的那一轮，以及灵敏度自测里故意改坏的那一轮。未插标记时，那几格 `X` 会被之后的 `[偶发诊断]` 报成偶发
- **用例自己修过**：按「测试口径错」改用例 C#（钉状态、改夹具、改装配）。旧格量的是旧用例
- **被测代码的缺陷已修复**：用例确定性未通过、修复缺陷后转为通过。旧格量的是带缺陷的代码，不是偶发

## 附文

写或改一条用例（契约作者面、能力索引、夹具与原语、写完后的验证）见 `rimrunner-authoring` skill。

|                                 | 什么时候开                                              |
| ------------------------------- | -------------------------------------------------- |
| `references/onboarding.md`      | 接入一个新产品（新产品根、新 mod）：从复制 RimRunner 安装目录的 `template/` 开始，编排描述符、测试工程、产品描述符、实例 Config（mod 设置与 Prefs 放哪、继承本机）、存档落点、第一次建档 |
| `references/modlists.md`        | 写或改一份名单：格式与继承、按组选用例、`[rimrunner]` 段的各条许可与认领 |
| `references/saves.md`           | 档建好之后：改名单要重建、建完之后的比对手法、换档后未通过时先怀疑的那一族 |
| `references/troubleshooting.md` | 一轮执行不出结果时：`status` 判卡、内存排队、换构建后看不到效果、开始执行前被闸拒（按首行文案查）；拍出的 mp4 没有声音或缺少声音 |
| `references/verdict.md`         | 结果已产出、需要裁决时：各输出段的处理判据、看截图、五态裁决树、复用轮与冷启动轮的对照     |
