# RimRunner

RimWorld mod 的游戏内自动回归：用 C# 写用例，`rimrunner` 起游戏、载基线档、在游戏里执行用例，产出报告与通过率台账。

## 安装目录里有什么

| 路径 | 是什么 |
| --- | --- |
| `rimrunner.exe` | 命令行。`rimrunner --help` 列全部命令 |
| `RimRunner.targets` | 测试工程 `Import` 它来引用引擎 |
| `engine/` | 引擎程序集（`ref/` 下是编译用的 ref 程序集与 IDE 悬停摘要）、引擎内容、作者面名册 `authoring.json`、引擎源码 `src/`（与程序集同一次构建，带 `///`，不带 `//` 注释） |
| `skills/` | 两个 Claude Code skill：`rimrunner` 执行批次与裁决，`rimrunner-authoring` 写用例 |
| `template/` | 最小产品样板：编排描述符、名单、测试工程、产品描述符、一条用例 |
| `examples/` | 范例用例：只依赖引擎与 vanilla，随 RimRunner 构建编译过 |
| `tools/FixtureIndex/` | 作者面名册工具：扫一个目录的 C# 源码，按 `///` 摘要列出类型与成员。产品要给自己的夹具建名册时调它，`RimRunnerFixtureIndex <目录> --profile product` |
| `THIRD-PARTY-NOTICES.txt` | 第三方组件的许可 |
| `package.json` | 构建清单：commit、构建时间、所含部分、构建时的游戏版本与程序集指纹 |

## 安装

需要 Windows、Steam 版 RimWorld、.NET SDK 10 或更新。

1. 把 RimRunner 安装目录（本文件所在的目录）加进 PATH。`rimrunner --version` 能输出版本即可
2. 设环境变量 `RimRunnerHome` 为安装目录。IDE 与手敲的 `dotnet build` 靠它找到 `RimRunner.targets`；CLI 自己编时按自己所在的安装目录编
3. 使用 Claude Code 时，把 `skills/` 下两个目录复制进 `~/.claude/skills/`

执行批次会起真游戏：要在有桌面会话、Steam 客户端在运行的 Windows 机器上。
CI 里在产品根执行 `rimrunner run`，按退出码判通过与否（0 = 无待处理项，其余各码见 `rimrunner --help`）。

## 下一步

接入第一个产品：读 `skills/rimrunner/references/onboarding.md`，从复制 `template/` 开始。
