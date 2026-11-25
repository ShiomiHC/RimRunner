using RimRunner;

namespace RimRunnerCli;

internal enum CommandRoute
{
    Local,
    Forward,
    ForwardIfRunning,
}

internal sealed record OperationMeta(
    CommandRoute Route,
    Func<ParsedCommand, LaunchKind>? Launch = null,
    string? TreePurpose = null,
    bool TreeNamesInstance = false);

internal static class Switches
{
    internal static readonly CliOption Modlist = CliOption.Text("modlist");
    internal static readonly CliOption Save = CliOption.Text("save");
    internal static readonly CliOption Input = CliOption.Repeat("input");
    internal static readonly CliOption IncludeExcluded = CliOption.Flag("include-excluded");
    internal static readonly CliOption EnvChecks = CliOption.Flag("env-checks");
    internal static readonly CliOption Group = CliOption.List("group", StringSplitOptions.TrimEntries, rejectEmptyTokens: true);
    internal static readonly CliOption NoCompat = CliOption.Flag("no-compat");
    internal static readonly CliOption Force = CliOption.Flag("force");
    internal static readonly CliOption ExpectCurrent = CliOption.Flag("expect-current");
    internal static readonly CliOption Test = CliOption.Flag("test");
    internal static readonly CliOption Paths = CliOption.Flag("paths");
    internal static readonly CliOption KeepFrames = CliOption.Flag("keep-frames");
    internal static readonly CliOption Clean = CliOption.Flag("clean");
    internal static readonly CliOption Mute = CliOption.Flag("mute");
    internal static readonly CliOption Json = CliOption.Flag("json");
    internal static readonly CliOption Full = CliOption.Flag("full");
    internal static readonly CliOption Failed = CliOption.Flag("failed");
    internal static readonly CliOption Case = CliOption.Text("case");
    internal static readonly CliOption All = CliOption.Flag("all");
    internal static readonly CliOption Estimates = CliOption.Flag("estimates");
    internal static readonly CliOption Hediff = CliOption.Flag("hediff");

    internal static readonly CliOption Tree = CliOption.Text("tree");
    internal static readonly CliOption RunId = CliOption.Text("run-id");
    internal static readonly CliOption Stat = CliOption.Text("stat");
    internal static readonly CliOption Label = CliOption.Text("label");
    internal static readonly CliOption Rect = CliOption.Text("rect");
    internal static readonly CliOption Prefix = CliOption.Text("prefix");
    internal static readonly CliOption Find = CliOption.Text("find");
    internal static readonly CliOption To = CliOption.Text("to");

    internal static readonly CliOption Product = CliOption.Text("product");
    internal static readonly CliOption Tag = CliOption.Text("tag");
    internal static readonly CliOption A = CliOption.Text("a");
    internal static readonly CliOption B = CliOption.Text("b");

    internal static readonly CliOption TimeoutMinutes =
        CliOption.Int("timeout-minutes", minimum: 0, minimumMessage: "--timeout-minutes 不能为负数");

    internal static readonly CliOption LongEventSeconds =
        CliOption.Int("long-event-seconds", minimum: 0, minimumMessage: "--long-event-seconds 不能为负数");

    internal static readonly CliOption QueueMinutes =
        CliOption.Int("queue-minutes", minimum: 0, minimumMessage: "--queue-minutes 不能为负数");

    internal static readonly CliOption Limit = CliOption.Int("limit", minimum: 1);
    internal static readonly CliOption Fps = CliOption.Int("fps", minimum: 1);
    internal static readonly CliOption Roll = CliOption.Int("roll", minimum: 2, minimumMessage: "--roll 至少 2 条");

    internal static readonly CliOption Layer = CliOption.Text("layer");
    internal static readonly CliOption File = CliOption.List("file",
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    internal static readonly CliOption EvalFile = CliOption.List("file");
    internal static readonly CliOption Using = CliOption.Repeat("using");

    internal static readonly CliOption KeepCompat = CliOption.List("keep-compat",
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

internal static class CommandGroup
{
    internal const string Execute = "执行";
    internal const string Showcase = "展台";
    internal const string Instance = "实例";
    internal const string Machine = "机器";
    internal const string Directory = "目录";
    internal const string Record = "记录";

    internal static readonly string[] Order =
        [Execute, Showcase, Instance, Machine, Directory, Record];
}

internal static class CommandTable
{
    private const string LayerLaunch = "拉起游戏";
    private const string LayerNoLaunch = "不拉起";

    private static readonly Dictionary<CliOperation, OperationMeta> Meta = new(ReferenceEqualityComparer.Instance);

    internal static readonly IReadOnlyList<CliCommand> All =
    [
        new()
        {
            Name = "run",
            Group = CommandGroup.Execute,
            Summary = "拉起游戏执行一批测试并输出结果；冷启动前产物陈旧或缺失即编译 builds 与 testMod，rimrunner.toml 的 autoBuild = false 时不编译",
            Operations =
            [
                Op("run",
                    "拉起游戏执行一批测试并输出结果",
                    [
                        Switches.Modlist, Switches.Save,
                        Switches.IncludeExcluded, Switches.EnvChecks, Switches.Group,
                        Switches.NoCompat, Switches.KeepCompat, Switches.Force,
                        Switches.TimeoutMinutes, Switches.QueueMinutes, Switches.Input, Switches.Tree,
                    ],
                    new(CommandRoute.Forward, parsed => parsed.Flag(Switches.NoCompat) ? LaunchKind.NoCompat : LaunchKind.Batch),
                    max: int.MaxValue,
                    positional: "<用例Id>",
                    validate: ValidateRun,
                    exitNotes:
                    [
                        new(ExitCodes.EmptyOrFindings, "有待处理项或有用例被拒（该条未执行、其余仍执行），或报告里没有任何用例结果"),
                        new(ExitCodes.Rejected, "批次在开始执行前被拒，零执行；或不另起临时实例的批（须先建档、--no-compat）所用实例正在执行、冷启动中、被分片组持有或被 live 保留"),
                        new(ExitCodes.InternalFault, "引擎故障（含用例窗外的）：本轮结论作废"),
                    ]),
            ],
        },
        new()
        {
            Name = "build-save",
            Group = CommandGroup.Execute,
            Summary = "建一份基线档，产出写入 <dir>/data/saves/",
            Operations =
            [
                Op("build-save",
                    "建一份基线档，产出写入 <dir>/data/saves/",
                    [
                        Switches.Modlist, Switches.Force, Switches.TimeoutMinutes, Switches.QueueMinutes,
                        Switches.Tree,
                    ],
                    new(CommandRoute.Forward, _ => LaunchKind.BuildSave),
                    exitNotes: [new(ExitCodes.Rejected, "本树的自动实例正在执行、冷启动中、被分片组持有或被 live 保留，拒绝接手")]),
            ],
        },
        new()
        {
            Name = "showcase",
            Group = CommandGroup.Showcase,
            Summary = "启动或复用展台实例，指定 <展台Id> 即立场；list 读展台名册；step / teardown 作用于已启动的展台实例",
            Operations =
            [
                Op("showcase",
                    "启动或复用展台实例，指定 Id 即立场",
                    [
                        Switches.Modlist, Switches.Force, Switches.TimeoutMinutes, Switches.QueueMinutes,
                        Switches.Tree,
                    ],
                    new(CommandRoute.Forward, _ => LaunchKind.Showcase),
                    label: "showcase [<展台Id>]",
                    brief: "[<展台Id>] 启动或复用展台实例，指定 Id 即立场",
                    max: 1,
                    positional: "<展台Id>",
                    validate: ValidateShowcase,
                    exitNotes: [new(ExitCodes.EmptyOrFindings, "立场后有中止、装配段错误级日志或步骤中止")]),
                Op("showcase list",
                    "读展台名册",
                    [Switches.Tree],
                    new(CommandRoute.Local),
                    verb: "list",
                    brief: "list 读展台名册",
                    readOnly: true),
                Op("showcase step",
                    "在当前展台上执行一步",
                    [Switches.TimeoutMinutes, Switches.Tree],
                    new(CommandRoute.Forward, TreePurpose: ClaimRecord.PurposeShowcase, TreeNamesInstance: true),
                    verb: "step",
                    brief: "step 执行一步",
                    min: 1,
                    max: 1,
                    positional: "<步骤名>",
                    missing: "写法：showcase step <步骤名>（步骤名含空格时加引号）",
                    exitNotes: [new(ExitCodes.EmptyOrFindings, "该步有中止、装配段错误级日志或步骤中止")]),
                Op("showcase teardown",
                    "拆场",
                    [Switches.TimeoutMinutes, Switches.Tree],
                    new(CommandRoute.Forward, TreePurpose: ClaimRecord.PurposeShowcase, TreeNamesInstance: true),
                    verb: "teardown",
                    brief: "teardown 拆场",
                    exitNotes: [new(ExitCodes.EmptyOrFindings, "拆场后有中止、装配段错误级日志或步骤中止")]),
            ],
        },
        new()
        {
            Name = "film",
            Group = CommandGroup.Showcase,
            Summary = "从场面的镜头出静帧、GIF 或 MP4，按叶路径出路径录像，或做场面测试（只立场、不出帧）",
            Operations =
            [
                Op("film",
                    "从场面的镜头拍摄、路径录像或场面测试",
                    [
                        Switches.Modlist, Switches.Test, Switches.Paths, Switches.Fps, Switches.Roll, Switches.KeepFrames,
                        Switches.Tag, Switches.Clean, Switches.Mute, Switches.Force,
                        Switches.TimeoutMinutes, Switches.QueueMinutes, Switches.Tree,
                    ],
                    new(CommandRoute.Forward, _ => LaunchKind.Film),
                    label: "film",
                    brief: "<场面Id>[/<镜头名>] 拍摄镜头、路径录像或场面测试",
                    min: 0,
                    max: int.MaxValue,
                    positional: "<场面Id>[/<镜头名>]",
                    excludes:
                    [
                        new(Switches.Test, Switches.KeepFrames, "--test 不编码帧，没有帧序列可供 --keep-frames 保留"),
                        new(Switches.Paths, Switches.Test, "--paths 与 --test 不能同时传入"),
                        new(Switches.Test, Switches.Tag, "--test 不产出拍摄产物，没有拍摄目录可供 --tag 命名"),
                        new(Switches.Test, Switches.Clean, "--test 不出帧，--clean 无从生效"),
                        new(Switches.Test, Switches.Mute, "--test 不产出拍摄产物，--mute 无从生效"),
                    ],
                    validate: ValidateFilm,
                    exitNotes:
                    [
                        new(ExitCodes.EmptyOrFindings, "场面名册有登记期错误，或有镜头拍摄失败、路径录像拍摄失败、拍摄产物未通过（编码失败、超出声明的体积上限、静帧张数不匹配）、"
                            + "最新副本未更新；--test 时有中止、装配段错误级日志、步骤中止、拍摄失败、失败断言或带键警告"),
                    ]),
                Op($"film {Program.FilmCompareWord}",
                    "把两次拍摄左右拼接为一份",
                    [Switches.A, Switches.B, Switches.Paths, Switches.Tree],
                    new(CommandRoute.Local),
                    verb: Program.FilmCompareWord,
                    brief: "compare 把两次拍摄左右拼接为一份",
                    readOnly: true,
                    min: 1,
                    max: 1,
                    positional: "<场面Id>[/<镜头名>]",
                    missing: "写法：film compare <场面Id>[/<镜头名>] [--a <引用>] [--b <引用>]",
                    validate: ValidateFilmCompare,
                    exitNotes: [new(ExitCodes.EmptyOrFindings, "有一项拼接失败（如两侧高度不同）")]),
            ],
        },
        new()
        {
            Name = "start",
            Group = CommandGroup.Instance,
            Summary = "启动或复用实例并进档，不执行批次",
            Operations =
            [
                Op("start",
                    "启动或复用实例并进档，不执行批次",
                    [
                        Switches.Modlist, Switches.Force, Switches.TimeoutMinutes, Switches.QueueMinutes,
                        Switches.Tree,
                    ],
                    new(CommandRoute.Forward, _ => LaunchKind.Warm),
                    label: "start"),
            ],
        },
        new()
        {
            Name = "status",
            Group = CommandGroup.Instance,
            Summary = "按 cwd 反查本树的登记与实例，或按 --tree 输出该树、该实例",
            Operations =
            [
                Op("status",
                    "按 cwd 反查本树的登记与实例，或按 --tree 输出该树、该实例",
                    [Switches.Tree, Switches.All, Switches.Product, Switches.Json],
                    new(CommandRoute.ForwardIfRunning, TreeNamesInstance: true),
                    readOnly: true,
                    excludes:
                    [
                        new(Switches.Tree, Switches.All),
                        new(Switches.All, Switches.Product),
                    ],
                    validate: ValidateStatus,
                    exitNotes: [new(ExitCodes.EmptyOrFindings, "登记里没有 --product 指定的产品")]),
            ],
        },
        new()
        {
            Name = "stop",
            Group = CommandGroup.Instance,
            Summary = "停止实例，零参停止本树全部实例，登记保留",
            Operations =
            [
                Op("stop",
                    "停止实例，零参停止本树全部实例，登记保留",
                    [Switches.Tree, Switches.All, Switches.Force],
                    new(CommandRoute.Forward, TreeNamesInstance: true),
                    excludes: [new(Switches.Tree, Switches.All)],
                    exitNotes: [new(ExitCodes.Rejected, "有实例被拒，未停止（其余仍停止）")]),
            ],
        },
        new()
        {
            Name = "unmount",
            Group = CommandGroup.Instance,
            Summary = "卸载树，零参卸载本树；挂载由拉起游戏的命令自动完成",
            Operations =
            [
                Op("unmount",
                    "卸载树，零参卸载本树",
                    [Switches.Tree, Switches.All, Switches.Force],
                    new(CommandRoute.Forward),
                    excludes: [new(Switches.Tree, Switches.All)],
                    exitNotes: [new(ExitCodes.Rejected, "实例正在执行批次、冷启动中、被分片组持有或被 live 保留，拒绝卸载")]),
            ],
        },
        new()
        {
            Name = "live",
            Group = CommandGroup.Instance,
            Summary = "驱动运行中的实例",
            BareShowsHelp = true,
            Operations =
            [
                Op("live pawn",
                    "列出当前地图上的 pawn 或查其 stat / hediff",
                    [Switches.Stat, Switches.Hediff, Switches.Json, Switches.Tree],
                    new(CommandRoute.Forward, TreeNamesInstance: true),
                    verb: "pawn",
                    max: 1,
                    positional: "<昵称或 ThingID>",
                    validate: ValidateLivePawn),
                Op("live screenshot",
                    "截图到 <dir>/output/artifacts/screenshots/ 并等到文件写完",
                    [Switches.Label, Switches.Rect, Switches.Json, Switches.Tree],
                    new(CommandRoute.Forward, TreeNamesInstance: true),
                    verb: "screenshot",
                    validate: ValidateLiveScreenshot),
                Op("live save",
                    "把当前世界的现场档导出到 <dir>/output/artifacts/saves/",
                    [Switches.Json, Switches.Tree],
                    new(CommandRoute.Forward, TreeNamesInstance: true),
                    verb: "save",
                    min: 1,
                    max: 1,
                    positional: "<存档名>",
                    missing: "save 需要存档名：rimrunner live save <名>",
                    validate: ValidateLiveSave),
                Op("live perf",
                    "查看或操作实例探针",
                    [Switches.Prefix, Switches.Json, Switches.Tree],
                    new(CommandRoute.Forward, TreeNamesInstance: true),
                    verb: "perf",
                    max: 1,
                    positional: "arm|reset|compare|dump",
                    validate: ValidateLivePerf),
                Op("live dev",
                    "列原版 dev 菜单子项或搜索",
                    [Switches.Find, Switches.Json, Switches.Tree],
                    new(CommandRoute.Forward, TreeNamesInstance: true),
                    verb: "dev",
                    max: 1,
                    positional: "<路径>",
                    validate: parsed => parsed.Positionals.Count > 0 && parsed.Text(Switches.Find) != null
                        ? "live dev 指定路径时列子项、指定 --find 时搜索，二选一"
                        : null,
                    exitNotes: [new(ExitCodes.EmptyOrFindings, "--find 无匹配")]),
                Op("live menu",
                    "列最上面那个右键菜单的选项",
                    [Switches.Json, Switches.Tree],
                    new(CommandRoute.Forward, TreeNamesInstance: true),
                    verb: "menu"),
                Op("live windows",
                    "列当前窗口栈",
                    [Switches.Json, Switches.Tree],
                    new(CommandRoute.Forward, TreeNamesInstance: true),
                    verb: "windows"),
                Op("live gizmos",
                    "列当前选中那一栏按钮",
                    [Switches.Json, Switches.Tree],
                    new(CommandRoute.Forward, TreeNamesInstance: true),
                    verb: "gizmos"),
                Op("live progress",
                    "看进度、tick 心跳与当前用例已产的读数",
                    [Switches.Json, Switches.Tree],
                    new(CommandRoute.Forward, TreeNamesInstance: true),
                    verb: "progress"),
                Op("live do",
                    "执行一个内置原语或脚本动作",
                    [Switches.Json, Switches.Tree, Switches.LongEventSeconds],
                    new(CommandRoute.Forward, TreeNamesInstance: true),
                    verb: "do",
                    max: int.MaxValue,
                    positional: "<名> [--<参数>=<值> …]",
                    validate: parsed => ValidateMethodCall("live do", parsed),
                    takesMethodParams: true,
                    exitNotes:
                    [
                        new(ExitCodes.EmptyOrFindings, "执行完，期间有错误级日志；不带名字时有脚本编译失败或无法装载"),
                        new(ExitCodes.Rejected, "实例忙或前置条件不满足、没有这个原语或脚本、脚本编译失败或无法装载、参数与参数表不匹配，零执行"),
                        new(ExitCodes.InternalFault, "引擎执行时抛出，动作中断"),
                    ]),
                Op("live eval",
                    "把一段 C# 编译后当一个动作执行一次",
                    [Switches.EvalFile, Switches.Using, Switches.Json, Switches.Tree, Switches.LongEventSeconds],
                    new(CommandRoute.Forward, TreeNamesInstance: true),
                    verb: "eval",
                    max: 1,
                    positional: "<代码>",
                    validate: ValidateLiveEval,
                    exitNotes:
                    [
                        new(ExitCodes.EmptyOrFindings, "执行完，期间有错误级日志"),
                        new(ExitCodes.Rejected, "实例忙或前置条件不满足、eval 代码编译失败，零执行"),
                        new(ExitCodes.InternalFault, "引擎执行时抛出，eval 代码中断"),
                    ]),
                Op("live until",
                    "判一个内置条件或脚本条件",
                    [Switches.Json, Switches.Tree, Switches.LongEventSeconds],
                    new(CommandRoute.Forward, TreeNamesInstance: true),
                    verb: "until",
                    max: int.MaxValue,
                    positional: "<条件> --within=<时长> [--<参数>=<值> …]",
                    validate: parsed => ValidateMethodCall("live until", parsed),
                    takesMethodParams: true,
                    exitNotes:
                    [
                        new(ExitCodes.EmptyOrFindings, "条件不成立（含推到 --within 上限仍不成立）；不带名字时有脚本编译失败或无法装载"),
                        new(ExitCodes.Rejected, "实例忙或前置条件不满足、没有这个条件或脚本、脚本编译失败或无法装载、参数与参数表不匹配，零执行"),
                        new(ExitCodes.InternalFault, "引擎执行时抛出，判定中断"),
                    ]),
                Op("live status",
                    "查一次转后台的操作",
                    [Switches.Json, Switches.Tree],
                    new(CommandRoute.Forward, TreeNamesInstance: true),
                    verb: "status",
                    min: 1,
                    max: 1,
                    positional: "<操作 id>",
                    missing: "status 需要操作 id：rimrunner live status <id>",
                    exitNotes:
                    [
                        new(ExitCodes.EmptyOrFindings, "已结束：do 期间有错误级日志，或 until 不成立"),
                        new(ExitCodes.Rejected, "实例上没有这个操作 id，或该操作开始执行前被拒"),
                        new(ExitCodes.InternalFault, "已结束：引擎执行时抛出"),
                    ]),
                Op("live cancel",
                    "取消转后台的操作",
                    [Switches.Json, Switches.Tree],
                    new(CommandRoute.Forward, TreeNamesInstance: true),
                    verb: "cancel",
                    min: 1,
                    max: 1,
                    positional: "<操作 id>",
                    missing: "cancel 需要操作 id：rimrunner live cancel <id>",
                    exitNotes:
                    [
                        new(ExitCodes.EmptyOrFindings, "已结束：期间有错误级日志，或 until 条件不成立"),
                        new(ExitCodes.Rejected, "实例上没有这个操作 id，或该操作开始执行前被拒"),
                        new(ExitCodes.InternalFault, "已结束：引擎执行时抛出"),
                    ]),
            ],
        },
        new()
        {
            Name = "machine",
            Group = CommandGroup.Machine,
            Summary = "整机账：机器锁、物理内存准入（含显存溢出）、排队",
            Operations =
            [
                Op("machine",
                    "整机账：机器锁、物理内存准入（含显存溢出）、排队",
                    [Switches.Estimates],
                    new(CommandRoute.Local),
                    readOnly: true),
            ],
        },
        new()
        {
            Name = "cache",
            Group = CommandGroup.Machine,
            Summary = "游戏的贴图与着色器缓存：查看与清空",
            Operations =
            [
                Op("cache",
                    "列出缓存大小与正在使用该缓存的实例",
                    [],
                    new(CommandRoute.Local),
                    label: "cache（查看）",
                    readOnly: true),
                Op("cache clear",
                    "清空没有实例使用的缓存槽",
                    [],
                    new(CommandRoute.Forward),
                    verb: "clear",
                    exitNotes: [new(ExitCodes.Rejected, "游戏缓存正由实例使用，未清空")]),
            ],
        },
        new()
        {
            Name = "vram",
            Group = CommandGroup.Machine,
            Summary = "名单的显存体检",
            Operations =
            [
                Op("vram",
                    "名单的显存体检",
                    [Switches.Tree],
                    new(CommandRoute.Local),
                    readOnly: true,
                    max: int.MaxValue,
                    positional: "<名单>"),
            ],
        },
        new()
        {
            Name = "cases",
            Group = CommandGroup.Directory,
            Summary = "按 Id、类名、类头摘要与范围查用例，列出用例组",
            Operations =
            [
                Op("cases find",
                    "按 Id、类名、类头摘要与范围查用例",
                    [Switches.Json, Switches.Full, Switches.Tree],
                    new(CommandRoute.Local),
                    verb: "find",
                    readOnly: true,
                    min: 1,
                    max: int.MaxValue,
                    positional: "<词>",
                    missing: "cases find 至少要一个关键词",
                    exitNotes: [new(ExitCodes.EmptyOrFindings, "无匹配")]),
                Op("cases groups",
                    "列出产品用例的各用例组与带该组的用例",
                    [Switches.Json, Switches.Tree],
                    new(CommandRoute.Local),
                    verb: CasesCommand.GroupsVerb,
                    readOnly: true),
            ],
        },
        new()
        {
            Name = "config",
            Group = CommandGroup.Directory,
            Summary = "把本机游戏的设置抄进设置目录（<dir>/data/config）",
            Operations =
            [
                Op("config capture",
                    "把本机游戏的设置抄进设置目录",
                    [Switches.Modlist, Switches.Layer, Switches.File],
                    new(CommandRoute.Local),
                    verb: "capture",
                    validate: ValidateConfigCapture,
                    exitNotes: [new(ExitCodes.EmptyOrFindings, "目标已在且内容不同，或 --file 指定的条目不在：未写入任何文件")]),
            ],
        },
        new()
        {
            Name = "engine",
            Group = CommandGroup.Directory,
            Summary = "查引擎的作者面，读 RimRunner 安装目录",
            Operations =
            [
                Op("engine find",
                    "查引擎的作者面类型与成员",
                    [Switches.Full],
                    new(CommandRoute.Local),
                    verb: "find",
                    readOnly: true,
                    min: 1,
                    max: int.MaxValue,
                    positional: "<词>",
                    missing: "engine find 至少要一个关键词",
                    exitNotes: [new(ExitCodes.EmptyOrFindings, "无匹配")]),
                Op("engine fixtures",
                    "列作者面类型",
                    [Switches.Json],
                    new(CommandRoute.Local),
                    verb: "fixtures",
                    readOnly: true),
            ],
        },
        new()
        {
            Name = "judge",
            Group = CommandGroup.Record,
            Summary = "裁决一份报告文件或按 runId 找到的批次归档，分档与 run 同源",
            Operations =
            [
                Op("judge",
                    "裁决一份报告文件或按 runId 找到的批次归档，分档与 run 同源",
                    [Switches.ExpectCurrent, Switches.Case, Switches.Tree],
                    new(CommandRoute.Local),
                    readOnly: true,
                    max: 1,
                    positional: "<报告路径|runId>",
                    validate: ValidateJudge,
                    exitNotes:
                    [
                        new(ExitCodes.EmptyOrFindings, "报告有待处理项或有用例被拒（该条未执行、其余仍执行），或报告里没有任何用例结果；--case 时这一条待处理、为用例拒绝、报告为批次拒绝或报告里没有这个 Id；分片组目录下用 --case 时，有片的报告无法读取、全部片的报告都是批次拒绝或另有不归用例的待处理项，也按 1 退出"),
                        new(ExitCodes.Rejected, "批次拒绝的报告，零执行（不带 --case 时）"),
                        new(ExitCodes.InternalFault, "引擎故障（含用例窗外的）：结论作废（不带 --case 时）"),
                    ]),
            ],
        },
        new()
        {
            Name = "reports",
            Group = CommandGroup.Record,
            Summary = "列出批次与单条用例的归档文件",
            Operations =
            [
                Op("reports",
                    "列出批次与单条用例的归档文件",
                    [Switches.Failed, Switches.Limit, Switches.Tree],
                    new(CommandRoute.Local),
                    readOnly: true),
            ],
        },
        new()
        {
            Name = "samples",
            Group = CommandGroup.Record,
            Summary = "读数库：在 jsonl 与 SQLite 两种格式之间整份转换",
            BareShowsHelp = true,
            Operations =
            [
                Op("samples convert",
                    "在 jsonl 与 SQLite 两种格式之间整份转换",
                    [Switches.To, Switches.Tree],
                    new(CommandRoute.Local),
                    verb: "convert",
                    validate: ValidateSamplesConvert,
                    exitNotes: [new(ExitCodes.Rejected, "本树还有正在执行的实例，未转换")]),
            ],
        },
        new()
        {
            Name = "history",
            Group = CommandGroup.Record,
            Summary = "通过率台账：查询，或插纪元标记",
            Operations =
            [
                Op("history",
                    "查通过率台账",
                    [Switches.Modlist, Switches.Save, Switches.Tree],
                    new(CommandRoute.Local),
                    label: "history（查询）",
                    readOnly: true,
                    max: int.MaxValue,
                    positional: "<用例Id>",
                    validate: ValidateHistoryQuery,
                    exitNotes: [new(ExitCodes.EmptyOrFindings, "台账存在，但其中没有指定的用例")]),
                Op($"history {Program.HistoryMarkWord}",
                    "在用例历史里插纪元标记",
                    [Switches.Modlist, Switches.Save, Switches.Tree],
                    new(CommandRoute.Forward),
                    verb: Program.HistoryMarkWord,
                    max: int.MaxValue,
                    positional: "<用例Id>",
                    validate: ValidateHistoryMark),
            ],
        },
    ];

    internal static IEnumerable<string> CommandsRefusingTree =>
        All.Where(command => !command.Operations.Any(operation => operation.Accepts(Switches.Tree)))
            .Select(command => command.Name);

    internal static readonly CliSibling RimSearcher = new("rimsearcher",
    [
        "callers", "code-search", "datamod", "economy", "export", "fields", "get", "il", "inherit", "keyed", "list",
        "members", "modlist", "mods", "read", "search", "snapshot", "sources", "types", "usages", "values", "where",
    ]);

    internal static readonly CliRegistry Registry = new()
    {
        ExeName = "rimrunner",
        Commands = All,
        Siblings = [RimSearcher],
        LayerOrder = [LayerLaunch, LayerNoLaunch],
        GroupOrder = CommandGroup.Order,
        TopicResolver = CommandHelp.ResolveTopic,
        OverviewExitCodes =
        [
            new(ExitCodes.Ok, "完成"),
            new(ExitCodes.EmptyOrFindings, "完成，有发现：用例待处理项、查询无匹配、镜头拍摄失败等"),
            new(ExitCodes.Usage, "用法错误：命令、选项或参数有误"),
            new(ExitCodes.Incomplete, "未完成：流程错误、超时、或前置条件不满足而中途停止"),
            new(ExitCodes.Rejected, "开始执行前被拒，零执行"),
            new(ExitCodes.GameUnavailable, "游戏不可用：游戏进程无法启动或未连上引擎即退出"),
            new(ExitCodes.InternalFault, "引擎故障或未捕获异常：未完成它该做的事"),
        ],
        OverviewExtraSections =
        [
            new HelpSection("通用", [.. CommandHelp.CommonOverviewLines(CommandsRefusingTree)]),
        ],
        OverviewTrailingLines =
        [
            "几种同时成立时取最大（70 > 69 > 5 > 4 > 2 > 1 > 0）",
        ],
        Policy = ParsePolicy.Strict with { DefaultCommand = "status" },
        Messages = CliMessages.Chinese,
    };

    internal static IEnumerable<CliOption> AllSwitches =>
        All.SelectMany(command => command.Operations).SelectMany(operation => operation.Options).Distinct();

    internal static CliCommand? Find(string name) => Registry.FindCommand(name);

    internal static OperationMeta MetaOf(CliOperation operation) => Meta[operation];

    internal static CommandRoute RouteOf(CliOperation operation) => Meta[operation].Route;

    internal static LaunchKind? LaunchOf(ParsedCommand parsed) =>
        Meta[parsed.Operation].Launch?.Invoke(parsed);

    internal static string? TreePurposeOf(ParsedCommand parsed) =>
        Meta[parsed.Operation] is { Launch: { } launch }
            ? LaunchKinds.PurposeOf(launch(parsed))
            : Meta[parsed.Operation].TreePurpose;

    internal static bool NamesInstance(CliOperation operation) =>
        Meta[operation] is var meta && (meta.Launch != null || meta.TreeNamesInstance);

    internal static IReadOnlyList<string> TailAfterCommand(this ParsedCommand parsed) =>
        parsed.Operation.Verb == null ? parsed.Positionals : [parsed.Operation.Verb, .. parsed.Positionals];

    internal static List<string>? SplitCaseIds(CliOperation operation, IReadOnlyList<string> tokens, out string? error)
    {
        error = null;
        List<string> ids = [];
        foreach (string token in tokens)
        {
            foreach (string part in token.Split(','))
            {
                string trimmed = part.Trim();
                if (trimmed.Length == 0)
                {
                    error = $"{operation.Name} 的用例 Id 含空项";
                    return null;
                }

                if (trimmed.StartsWith('-'))
                {
                    error = $"用例 Id 不以 - 开头：{trimmed}";
                    return null;
                }

                ids.Add(trimmed);
            }
        }

        string[] duplicates =
        [
            .. ids.GroupBy(id => id, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
        ];
        if (duplicates.Length > 0)
        {
            error = $"重复的用例 Id：{string.Join(",", duplicates)}";
            return null;
        }

        return ids;
    }

    private static CliOperation Op(
        string name,
        string summary,
        CliOption[] options,
        OperationMeta meta,
        string? verb = null,
        string? label = null,
        string? brief = null,
        bool readOnly = false,
        int min = 0,
        int max = 0,
        string? positional = null,
        string? missing = null,
        OptionExclusion[]? excludes = null,
        Func<ParsedCommand, string?>? validate = null,
        bool takesMethodParams = false,
        CliExitNote[]? exitNotes = null)
    {
        bool takesRunId = meta.TreeNamesInstance && meta.TreePurpose == null;
        CliOption[] withProduct = options.Contains(Switches.Tree) && !options.Contains(Switches.Product)
            ? [.. options.SelectMany(option => option == Switches.Tree
                ? new[] { option, Switches.Product }
                : [option])]
            : options;
        CliOption[] expanded = takesRunId
            ? [.. withProduct.SelectMany(option => option == Switches.Product
                ? new[] { option, Switches.RunId }
                : [option])]
            : withProduct;

        bool acceptsTree = expanded.Contains(Switches.Tree);
        bool namesInstance = meta.Launch != null || meta.TreeNamesInstance;
        IReadOnlyList<(CliOption Option, string Placeholder)> placeholders = acceptsTree
            ?
            [
                (Switches.Tree, namesInstance ? "<名>[/<用途>]" : "<名>"),
                (Switches.Product, "<产品名>"),
                .. takesRunId ? new[] { (Switches.RunId, "<runId>") } : [],
            ]
            : [];

        var operation = new CliOperation
        {
            Verb = verb,
            Name = name,
            Label = label,
            Summary = summary,
            Brief = brief,
            Layer = meta.Launch != null ? LayerLaunch : LayerNoLaunch,
            ReadOnly = readOnly,
            MinPositionals = min,
            MaxPositionals = max,
            PositionalDisplay = positional,
            MissingPositionalMessage = missing,
            Options = expanded,
            OptionPlaceholders = placeholders,
            UsesGlobals = false,
            TakesMethodParams = takesMethodParams,
            Excludes = excludes ?? [],
            Validate = parsed => WrapValidate(name, acceptsTree, meta, validate, parsed),
            ExitNotes = exitNotes ?? [],
        };
        Meta[operation] = meta;
        return operation;
    }

    private static string? WrapValidate(
        string opName,
        bool acceptsTree,
        OperationMeta meta,
        Func<ParsedCommand, string?>? inner,
        ParsedCommand parsed)
    {
        if (meta.Launch == null && !meta.TreeNamesInstance
            && acceptsTree
            && parsed.Text(Switches.Tree) is string treeSpec
            && TreeSelector.SplitPurpose(treeSpec.Trim()).Purpose != null)
        {
            return $"--tree 不收用途段：{opName} 作用于整棵树";
        }

        if (parsed.Operation.Accepts(Switches.RunId) && parsed.Text(Switches.RunId) is string runId)
        {
            if (!RimRunnerRunId.IsValid(runId))
                return $"--run-id 须是 {RimRunnerRunId.Shape}（收到 {runId}）";
            if (parsed.Operation.Accepts(Switches.All) && parsed.Flag(Switches.All))
                return "--run-id 与 --all 不能同时传入";
            if (parsed.Text(Switches.Tree) is string runIdTree
                && TreeSelector.SplitPurpose(runIdTree.Trim()).Purpose is string runIdPurpose
                && !(PurposePolicies.TryForWire(runIdPurpose, out PurposePolicy? runIdPolicy)
                     && PurposePolicies.IsAuto(runIdPolicy.Purpose)))
                return $"--run-id 只指定自动实例，不能与 --tree 的 /{runIdPurpose} 同时传入";
        }

        if (acceptsTree && parsed.Text(Switches.Tree) is string multiSpec
            && TreeSelector.SplitPurpose(multiSpec.Trim()) is { Purpose: string splitPurpose } split
            && PurposePolicies.TryForWire(splitPurpose, out PurposePolicy? splitPolicy)
            && splitPolicy.AllowsMultiplePerTree && opName != "status")
        {
            if (splitPolicy.Purpose == InstancePurpose.Spare)
                return parsed.Operation.Accepts(Switches.RunId)
                    ? $"--tree 的 /{splitPurpose} 只用于 status；{opName} 指定临时自动实例用 --run-id <runId>"
                    : $"--tree 的 /{splitPurpose} 只用于 status";
            if (split.Ordinal == null)
                return $"--tree 的 /{splitPurpose} 不带编号只用于 status；{opName} 指定一片写 /{splitPurpose}N";
        }

        return inner?.Invoke(parsed);
    }

    private static string? ValidateRun(ParsedCommand parsed)
    {
        List<string>? caseIds = SplitCaseIds(parsed.Operation, parsed.Positionals, out string? error);
        if (error != null) return error;
        bool named = caseIds is { Count: > 0 };
        if (parsed.Given(Switches.KeepCompat) && !parsed.Flag(Switches.NoCompat))
            return "--keep-compat 须与 --no-compat 同时传入";
        if (parsed.Flag(Switches.NoCompat) && parsed.Given(Switches.Save))
            return "--no-compat 与 --save 不能同时传入";
        return StructuredRunRequest.CombinationError(named,
            parsed.Flag(Switches.IncludeExcluded), parsed.Flag(Switches.EnvChecks),
            parsed.Given(Switches.Group) ? parsed.List(Switches.Group) : null);
    }

    private static string? ValidateConfigCapture(ParsedCommand parsed) =>
        parsed.Text(Switches.Layer) is string layer
        && layer is not (ConfigCaptureCommand.LayerRoot or ConfigCaptureCommand.LayerModList)
            ? $"--layer 只能是 {ConfigCaptureCommand.LayerRoot} 或 {ConfigCaptureCommand.LayerModList}（收到 {layer}）"
            : null;

    private static string? ValidateHistoryQuery(ParsedCommand parsed)
    {
        SplitCaseIds(parsed.Operation, parsed.Positionals, out string? error);
        return error;
    }

    private static string? ValidateSamplesConvert(ParsedCommand parsed) =>
        SampleStore.ParseKind(parsed.Text(Switches.To)) == null
            ? $"{parsed.Operation.Name} 须指定 --to {SampleStore.SqliteName} 或 --to {SampleStore.JsonlName}"
            : null;

    private static string? ValidateHistoryMark(ParsedCommand parsed)
    {
        List<string>? ids = SplitCaseIds(parsed.Operation, parsed.Positionals, out string? error);
        if (error != null) return error;
        return ids is { Count: 0 } ? $"{parsed.Operation.Name} 至少须一个用例 Id" : null;
    }

    private static string? ValidateFilmCompare(ParsedCommand parsed)
    {
        FilmFlow.FilmMode mode = parsed.Flag(Switches.Paths) ? FilmFlow.FilmMode.Paths : FilmFlow.FilmMode.Shots;
        FilmFlow.ParseTargets(parsed.Positionals, mode, out string? error);
        return error;
    }

    private static string? ValidateFilm(ParsedCommand parsed)
    {
        FilmFlow.FilmMode mode = parsed.Flag(Switches.Paths) ? FilmFlow.FilmMode.Paths
            : parsed.Flag(Switches.Test) ? FilmFlow.FilmMode.Test
            : FilmFlow.FilmMode.Shots;
        if (parsed.Int(Switches.Fps) is int fps)
        {
            if (mode != FilmFlow.FilmMode.Paths) return "--fps 只用于 --paths";
            if (!FilmFlow.PathsFpsValid(fps)) return $"--fps 须是 60 的约数：{fps}";
        }

        if (parsed.Text(Switches.Tag) is { } tag && FilmRuns.TagError(tag) is { } tagError) return tagError;
        List<FilmFlow.Target>? targets = FilmFlow.ParseTargets(parsed.Positionals, mode, out string? error);
        if (error != null) return error;
        if (parsed.Given(Switches.Roll)
            && (mode != FilmFlow.FilmMode.Shots || targets is not [{ Shot: not null }]))
            return "--roll 只收一个 <场面Id>/<镜头名>，不能与 --paths、--test 同时传入";
        return null;
    }

    private static string? ValidateJudge(ParsedCommand parsed) =>
        parsed.Given(Switches.Case) && parsed.Flag(Switches.ExpectCurrent)
            ? "--case 不能与 --expect-current 同时传入"
            : null;

    private static string? ValidateStatus(ParsedCommand parsed) =>
        parsed.Flag(Switches.Json)
        && (parsed.Given(Switches.Tree) || (!parsed.Flag(Switches.All) && parsed.Text(Switches.Product) == null))
            ? "--json 只用于 status --all 与不带 --tree 的 status --product"
            : null;

    private static string? ValidateShowcase(ParsedCommand parsed) =>
        parsed.Positionals is [ShowcaseAction.Stop]
            ? $"showcase 没有 {ShowcaseAction.Stop} 子命令：可用 {Find("showcase")!.FormatVerbList(Registry.Messages)}"
            : null;

    private static string? ValidateLivePawn(ParsedCommand parsed)
    {
        if (parsed.Positionals.Count == 0 && (parsed.Text(Switches.Stat) != null || parsed.Flag(Switches.Hediff)))
            return "查 pawn 的 stat / hediff 须指定昵称或 ThingID";
        if (parsed.Positionals.Count == 1 && parsed.Text(Switches.Stat) == null && !parsed.Flag(Switches.Hediff))
            return "指定 pawn 时须带 --hediff 或 --stat <Def>";
        return null;
    }

    private static string? ValidateMethodCall(string name, ParsedCommand parsed)
    {
        if (parsed.Positionals.Count > 0 && parsed.Positionals[0].StartsWith("--", StringComparison.Ordinal))
            return $"{name} 先写名字，参数跟在名字后面，收到：{parsed.Positionals[0]}";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string token in parsed.Positionals.Skip(1))
        {
            if (LiveSession.MethodParam(token) is not var (key, _))
                return $"{name} 的参数写作 --<名>=<值>，名以字母开头、只用字母、数字、- 与 _，收到：{token}";
            if (Registry.AllKnownOptions().Any(option => string.Equals(option.Name, key, StringComparison.Ordinal)))
                return $"--{key} 是选项，取值用空格分开：--{key} <值>";
            if (!seen.Add(key))
                return GivenTwice($"{name} 的参数 {key}");
        }

        return null;
    }

    internal static string GivenTwice(string subject) => $"{subject} 指定不止一次";

    private static string? ValidateLiveScreenshot(ParsedCommand parsed) =>
        parsed.Text(Switches.Rect) is { Length: > 0 } rect && LiveSession.ParseRect(rect) == null
            ? $"--rect 需要 4 个数：x,y,w,h，收到：{rect}"
            : null;

    private static string? ValidateLiveEval(ParsedCommand parsed)
    {
        IReadOnlyList<string> files = parsed.List(Switches.EvalFile);
        if (files.Count > 1)
            return $"live eval 的 --file 只收一个文件，收到 {files.Count} 条：{string.Join("、", files)}；--file 的取值按逗号分隔，路径里不能有逗号";
        bool code = parsed.Positionals.Count > 0;
        if (code == (files.Count == 1))
            return code
                ? "live eval 的代码与 --file 不能同时传入"
                : "live eval 需要代码或 --file <路径>：rimrunner live eval \"<代码>\"";
        return code && parsed.Positionals[0].Length == 0 ? "live eval 的代码为空" : null;
    }

    private static string? ValidateLiveSave(ParsedCommand parsed) =>
        parsed.Positionals is [string name] ? SaveExport.RejectName(name) : null;

    private static string? ValidateLivePerf(ParsedCommand parsed)
    {
        string? sub = parsed.Positionals.Count > 0 ? parsed.Positionals[0] : null;
        if (parsed.Text(Switches.Prefix) != null && sub != "dump")
            return "--prefix 只用于 live perf dump";

        return sub is null or "arm" or "reset" or "compare" or "dump" ? null : $"未知 perf 动作：{sub}";
    }
}
