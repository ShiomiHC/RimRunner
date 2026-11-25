# RimRunner

RimWorld mod 的游戏内自动回归：用 C# 写用例，`rimrunner` 起游戏、载基线档、在游戏里执行用例，产出报告与通过率台账。

接入与使用见 [package/README.md](package/README.md) 与 [skills/rimrunner/references/onboarding.md](skills/rimrunner/references/onboarding.md)。

## 构建

需要 Windows、Steam 版 RimWorld 1.7、创意工坊的 Harmony、.NET SDK 10 与 PowerShell 7。

[RimTooling](https://github.com/ShiomiHC/RimTooling) 是 git 子模块。游戏目录按 Steam 安装信息找，不在 Steam 库里的设环境变量 `RimWorldDir`。

```powershell
git clone --recursive https://github.com/ShiomiHC/RimRunner.git
cd RimRunner
pwsh build.ps1
```

克隆时没有带 `--recursive` 的，先执行 `git submodule update --init`。GitHub 的「Download ZIP」不含子模块。

产物是 `dist/`，安装步骤见 `dist/README.md`。

## 目录

| 路径 | 内容 |
| --- | --- |
| `Source/Engine/` | 引擎：装进测试 mod、在游戏进程里执行 |
| `Source/EngineBuild/` | 引擎的工程文件 |
| `Source/Cli/` | 命令行 `rimrunner` |
| `Source/Analyzers/` | 产品测试工程的编译期分析器 |
| `Source/FixtureIndex/` | 作者面名册工具 |
| `RimTooling/` | 子模块：命令行框架、平台与 RimWorld 基础库、mod 工程的构建口径 |
| `package/` | 随安装目录分发的说明、产品样板与范例用例 |
| `skills/` | 两个 Claude Code skill：执行批次与裁决、写用例 |

## 许可

[MIT](LICENSE)
