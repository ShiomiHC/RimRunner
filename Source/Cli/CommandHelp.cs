using System.Text;
using RimRunner;

namespace RimRunnerCli;

public static class CommandHelp
{
    private const int DescriptionColumn = 34;

    private static readonly OptionDoc QueueDoc = OptionDoc.Value("N",
        "每段排队各自的上限：机器锁（同时执行的实例数、P 核余量、独占）、内存准入与加载闸、产出根锁",
        "缺省 " + QueuePolicy.DefaultMinutes + "，0 = 不设上限；不受 --timeout-minutes 影响");

    private static OptionDoc TimeoutDoc(string segments) => OptionDoc.Value("N",
        segments + "各自的无进展上限：进度连续 N 分钟无变化、或在一帧里不再推进即停止；",
        "缺省 " + TimeoutPolicy.DefaultMinutes + "，0 = 不超时");

    private static readonly OptionDoc TreeInstanceDoc = OptionDoc.Value("<名>[/<用途>]",
        "在那棵树里执行、不看 cwd，带用途（batch、showcase、film、shardN）时只对该实例；",
        "<名> 是清单里的树名，匹配到多棵时列出候选；也收路径。缺省按 cwd 向上找产品根");

    private static readonly OptionDoc TreeOnlyDoc = OptionDoc.Value("<名>",
        "在那棵树里执行、不看 cwd，作用于整棵树，不收用途段；",
        "<名> 是清单里的树名，匹配到多棵时列出候选；也收路径。缺省按 cwd 向上找产品根");

    private static readonly OptionDoc ProductDoc = OptionDoc.Value("<产品名>",
        "与 --tree 同时传入时只在该产品的树里认树名；不带 --tree 时核 cwd 所在的产品，不匹配即拒");

    private static readonly OptionDoc RunIdDoc = OptionDoc.Value("<runId>",
        "按当前 runId 指定本树的一个自动实例（常驻或临时），与 --tree <名> 同时传入时在那棵树里找；",
        "本树运行中的自动实例不止一个时，live 与 stop 指定其中一个只能用 --run-id。当前 runId 由最近接手该实例的命令写入，见 run / start 落定实例那一行");

    private static readonly OptionDoc TimeoutSingleDoc = OptionDoc.Value("N",
        "无进展上限：进度连续 N 分钟无变化、或在一帧里不再推进即停止；",
        "缺省 " + TimeoutPolicy.DefaultMinutes + "，0 = 不超时");

    private static readonly OptionDoc LiveJsonDoc = OptionDoc.Flag("schema 校验后原样输出回包");

    private static readonly OptionDoc LiveTreeDoc = OptionDoc.Value("<名>[/<用途>]",
        "对该树或该实例执行，可在任何目录执行；未指定时取 cwd 所在树的唯一登记");

    private static readonly OptionDoc LongEventDoc = OptionDoc.Value("<秒>",
        "开始前与两帧之间遇到长事件时等它结束的上限，缺省 "
            + RimRunnerLiveContract.Drive.DefaultLongEventSeconds + "，0 = 遇到即拒或中止；",
        "等过的写进回执，等满上限即拒（未开始）或中止，写出在途的长事件");

    private static (CliOption, OptionDoc)[] LiveCommonDocs =>
    [
        (Switches.Json, LiveJsonDoc),
        (Switches.Tree, LiveTreeDoc),
        (Switches.Product, ProductDoc),
        (Switches.RunId, RunIdDoc),
    ];

    private const string ShowcaseVerdictNote = "  场面里的失败断言与带键警告不影响判定，须查看终端";

    private const string ModlistBareLine = "不带取值时列出全部名单，命令不执行";

    internal static readonly HelpPage[] Pages =
    [
        new()
        {
            CommandName = "run",
            Detail =
            [
                "",
                "实例",
                "  实例进程内的 DLL 与内容均未变、名单相同则复用空闲实例；",
                "  复用模式下自动回载基线档；",
                "  复用条件不满足则冷启动",
                "  常驻自动实例正在执行、冷启动锁被持、被分片组持有或被 live 保留时，改用临时自动实例（上屏 [另起]），",
                "  临时自动实例的自动停时限同常驻自动实例（" + RimRunnerConfig.BatchIdleKey + "），停止后登记收回；--force 不改用，抢占常驻自动实例",
                "  基线档不存在，且本树与主树都没有该名单的台账时，先执行 build-save 再执行批；",
                "  基线档不存在而台账存在时不自动建档；--save 指定的档不存在时直接拒绝，不建档",
                "  复用或冷启动接管后，实例里手工布置的局面消失",
                "  空闲满各自的自动停时限后自行退出：自动实例 " + IdleExit.BatchDefault + " 分钟、分片实例 "
                    + IdleExit.ShardDefault + " 分钟、拍摄实例 " + IdleExit.FilmDefault
                    + " 分钟、展台实例 " + IdleExit.ShowcaseDefault + " 分钟，",
                "  时限在 " + "机器级 " + RimRunnerConfig.MachineFileName + " 的 [" + RimRunnerConfig.InstancesTable + "] 里指定："
                    + RimRunnerConfig.BatchIdleKey + " / " + RimRunnerConfig.ShardIdleKey,
                "  / " + RimRunnerConfig.FilmIdleKey + " / " + RimRunnerConfig.ShowcaseIdleKey
                    + "，0 = 不自动停，也不当驱逐候选；",
                "  空闲满时限的一半（可停止时限）时，排队等内存准入的其他命令会停止该实例；",
                "  时限在冷启动时定，改配置文件只对之后冷启动的实例生效；run 与 live 查询（live progress 除外）重新计时，",
                "  窗口里的键鼠操作也重新计时；status 不计；live 保留期内不计时，保留到期或放开后重新计时（见 rimrunner live do --help）",
                "  本树的常驻自动实例被 live 保留期间 run 不接手该实例、改用临时自动实例，--force 停止该实例再冷启动；受过 live 驱动的实例不复用（见 rimrunner live do --help）",
                "  贴图档位由名单 [rimrunner]." + TextureTierPolicy.DirectiveKey + " = \"<档>\" 定："
                    + string.Join(" / ", TextureTierPolicy.Ladder),
                "                                  （清晰度升序），名单未写档位 = " + TextureTierPolicy.DefaultTier
                    + "；placeholder 以占位像素替代贴图，只在名单声明时用；",
                "                                  本批用例申报的 MinTextures 高于名单档位时自动升档；展台恒 native；",
                "                                  实例档位低于本轮所需即不复用",
                "",
                "选批",
                "  rimrunner run 不带用例 Id 与选批选项时执行回归批：全部回归用例（名单写有 [rimrunner]." + CaseGroupPolicy.DirectiveKey + " 时只取这些组），按排除名单（excluded.toml）排除；",
                "  环境体检按本批有无 World 级用例并入，指定用例 Id 时同样。",
                "  复用实例时，该实例已执行并通过环境体检，且本轮排除名单与体检通过时相同，则不再并入环境体检",
                "  用例 Id：逗号分隔或多个；执行指定的用例，不受用例组与排除名单约束，本环境不适用时该条被拒、其余照常执行；",
                "                                  不能与下面的选批选项同时传入",
                "  读数用例只在指定用例 Id 时执行，不进任何批",
                "  未指定用例 Id 时按选批选项选批：本批 = 回归用例 ∩ 用例组 ∩ 排除名单之外",
                HelpPage.OptionMarker(Switches.EnvChecks),
                HelpPage.OptionMarker(Switches.Group),
                "  用例组：用例类挂 [RimRunnerGroup(\"<组>\")]，可多个、随基类继承；名单写 [rimrunner]." + CaseGroupPolicy.DirectiveKey
                    + " = [\"<组>\", …] 时",
                "                                  本名单的回归批只选这些组，未指定 --group 时生效；--env-checks 不套用名单的组",
                "                                  每个产品用例类都须有组，没有即编译报错（RR0001）；",
                "                                  按组选批时随包检查照常执行。",
                "                                  按组跳过的用例不入台账，结论区 [按组跳过] 行输出条数与组",
                HelpPage.OptionMarker(Switches.IncludeExcluded),
                "",
                "读数分片",
                "  本批只有一条可分片用例时自动分片执行，片数见 [分片] 行；主树与 worktree 都分片，",
                "  片装载本树的代码、台账折进本树。组目录 <dir>/output/reports/shards/<组 id>/。混有别的用例时不分片",
                "",
                "输入件",
                "  用例经 RimRunnerInput 读数据根下的输入件（期望表、名册、聚焦文件等），来源由近及远：",
                "  本批指定的 → 本树数据根 → 主树数据根（本树是 worktree 时）；读到的来源与用例见报告 inputs",
                HelpPage.OptionMarker(Switches.Input),
                "",
                "兼容关闭",
                HelpPage.OptionMarker(Switches.NoCompat),
                HelpPage.OptionMarker(Switches.KeepCompat),
                "",
                "执行",
                HelpPage.OptionMarker(Switches.Modlist),
                HelpPage.OptionMarker(Switches.Save),
                HelpPage.OptionMarker(Switches.Force),
                HelpPage.OptionMarker(Switches.TimeoutMinutes),
                HelpPage.OptionMarker(Switches.QueueMinutes),
            ],
            OptionDocs =
            [
                (Switches.EnvChecks, OptionDoc.Flag(
                    "替换回归批：只执行随包环境体检")),
                (Switches.IncludeExcluded, OptionDoc.Flag(
                    "本次忽略排除名单（excluded.toml）；不能与用例 Id 同时传入")),
                (Switches.Group, OptionDoc.Value("<组>[,<组>…]",
                    "批部分只选带所列任一组的产品用例，覆盖名单 [rimrunner]." + CaseGroupPolicy.DirectiveKey + "；"
                    + RimRunnerCaseGroups.AllToken + " = 不按组过滤，也不套用名单的组；",
                    "不能与用例 Id、--env-checks 同时传入；组名须有产品用例声明，否则拒绝")),
                (Switches.NoCompat, OptionDoc.Flag(
                    "关闭游戏 runtimeconfig 里的全部 WaylithClr.* 兼容开关执行一批：",
                    "游戏从游戏目录旁的镜像目录冷启动，",
                    "占本树的 nocompat 实例，与 batch 实例互不复用；",
                    "不写台账与读数库，只出报告；不分片；基线档在兼容开启时建")),
                (Switches.Input, OptionDoc.Value("<路径>=<文件或正文>",
                    "本批读 <路径>（数据根下的相对路径）时以指定的这份输入件为准，可重复指定；",
                    "取值以 { 或 [ 开头即正文，否则是文件路径（相对当前目录）；正文在发车前读入，分片各片同一份；",
                    "指定而没有用例读到即按待处理退出；已读指定输入件的用例在结论行点名")),
                (Switches.KeepCompat, OptionDoc.Value("<名>[,<名>…]",
                    "与 --no-compat 同时传入时列出的开关保留游戏原值，名字不带 WaylithClr. 前缀；",
                    "不在现装 runtimeconfig 里的名字即拒")),
                (Switches.Modlist, OptionDoc.Value("<名单>",
                    "指定 mod 名单替代描述符的 defaultModList，通过率记入该名单的台账；",
                    "无 defaultModList 时默认生效名单为本机当前的 ModsConfig；",
                    "名单里可写 " + ModListDefinition.ModsFromKey
                        + " = \"<名单>\" 继承被引名单的清单与指令，只加不减；",
                    "[rimrunner]." + ModListDefinition.SaveFromKey
                        + " = \"<名单>\" 进档用被引名单的档，两份名单的生效清单须同序相同；",
                    ModlistBareLine)),
                (Switches.Save, OptionDoc.Value("<档名或路径>",
                    "本批进这份档，替代名单的基线档；生效清单由名单决定，不取自档。只指定档名（不带 .rws）时在 <dir>/data/saves/ 里找，",
                    "否则按路径；通过率等台账按名单 + 档另记一本；存档头与名单不匹配的警告级日志、读这份档本体的警告级日志",
                    "只列成观测，不记带键警告；换档即冷启动；不能与 --no-compat 同时传入")),
                (Switches.Force, OptionDoc.Flag(
                    "本树的实例正在执行批次、冷启动中或被 live 保留时也停止该实例（撤销持锁的命令），改为冷启动；",
                    "实例空闲且未被保留时仍判复用；被分片组持有时撤销那一组（它正在执行的片停止）；",
                    "机器锁、准入与产出根锁照常排队")),
                (Switches.TimeoutMinutes, TimeoutDoc("载档、复位、等报告")),
                (Switches.QueueMinutes, QueueDoc),
                (Switches.Tree, TreeInstanceDoc),
                (Switches.Product, ProductDoc),
            ],
            UsageOverrides =
            [
                ("", "rimrunner run [<用例Id>[,<用例Id>…] …] [选项…]"),
            ],
        },
        new()
        {
            CommandName = "judge",
            Detail =
            [
                "  <报告路径>：相对路径按 cwd 解析；未指定时 = 产出根的 reports/latest.json；",
                "  本树有多个实例同时执行批次时，latest.json 是最后写完的那一批，不一定是本命令执行的那一批；",
                "  指定分片组目录 reports/shards/<组>/ 时读组清单与各片归档，出与 run 收口同一份组视图",
                "  <runId>：盘上没有同名路径时，到产出根的 reports/ 下按 runId 取那一批的归档（不含 single/ 与 shards/）",
                "",
                HelpPage.OptionMarker(Switches.ExpectCurrent),
                HelpPage.OptionMarker(Switches.Case),
            ],
            OptionDocs =
            [
                (Switches.ExpectCurrent, OptionDoc.Flag(
                    "报告须是当前的结论（比对 MVID 与内容指纹），",
                    "无法核对或已陈旧即按未完成退出。缺省只输出一行，不因此改变判定；不能与分片组目录同时传入")),
                (Switches.Case, OptionDoc.Value("<用例Id>",
                    "只输出这一条的明细，只裁决这一条。",
                    "分片组目录下把各片的这一条并成一条，状态取最严重的一片；组上另有不归用例的待处理项时列出，并按待处理退出。不能与 --expect-current 同时传入")),
                (Switches.Tree, TreeOnlyDoc),
                (Switches.Product, ProductDoc),
            ],
        },
        new()
        {
            CommandName = "build-save",
            Detail =
            [
                "  进行一次新建殖民地的流程，参数来自描述符",
                "  亲属关系指向档内不存在的 pawn 时，移除该条目并在输出中列出",
                "  发布前用新档执行一遍随包用例（不计台账）：非被测产品组件首次填值的元素记进档的边角文件，",
                "  之后载这份档的批不比对这些元素，输出中列出",
                "  完成后暂停并进入空闲状态",
                "  会覆写 <dir>/data/saves/ 里的同名档",
                "",
                "  描述符继承：内置 realistic 预设 → <dir>/data/" + OrchestratorDescriptor.DefaultNewGameFileName,
                "              → <dir>/modlists/<名单>" + RimRunnerNewGameDescriptor.ModListSuffix,
                "",
                HelpPage.OptionMarker(Switches.Modlist),
                HelpPage.OptionMarker(Switches.Force),
                HelpPage.OptionMarker(Switches.TimeoutMinutes),
                HelpPage.OptionMarker(Switches.QueueMinutes),
            ],
            OptionDocs =
            [
                (Switches.Modlist, OptionDoc.Value("<名单>",
                    "指定该 mod 名单对应的描述符",
                    "默认为描述符的 defaultModList；档名 <saveNamePrefix>-<名单>；",
                    "写有 [rimrunner]." + ModListDefinition.SaveFromKey + " 的名单不建档；",
                    "描述符缺少 defaultModList 时名单为本机当前的 ModsConfig；",
                    "名单里可写 " + ModListDefinition.ModsFromKey
                        + " = \"<名单>\" 继承被引名单的清单与指令，只加不减；",
                    ModlistBareLine)),
                (Switches.Force, OptionDoc.Flag(
                    "本树的实例正在执行批次、冷启动中或被 live 保留时也停止该实例（撤销持锁的命令）；",
                    "被分片组持有时撤销那一组（它正在执行的片停止）；机器锁、准入与产出根锁照常排队")),
                (Switches.TimeoutMinutes, TimeoutDoc("载档与各段等待")),
                (Switches.QueueMinutes, QueueDoc),
                (Switches.Tree, TreeInstanceDoc),
                (Switches.Product, ProductDoc),
            ],
        },
        new()
        {
            CommandName = "status",
            Detail =
            [
                "  报告实例状态并尝试获取当前实例的游戏内快照",
                "  本树的分片实例折成一段：正在执行的分片组的组队列格数（已认领、已完成、未认领），",
                "  其下每个分片实例一行（活性、片序号、认领与完成的格数）；",
                "  用途段写不带编号的 shard（<名>/shard）时只输出这一段，写 shardN 时输出那个分片实例的明细与游戏内快照",
            ],
            OptionDocs =
            [
                (Switches.All, OptionDoc.Flag(
                    "全机摘要，没有运行中实例的树不列：",
                    "每个产品一行（树数、实例数、运行中的实例数），其下每棵树一行（树名、产品根），树下每个实例一行，行首是类型段；",
                    "游戏进程不在的实例记得停因时输出「已停」与停因，记不得时输出「无进程」；",
                    "同一棵树里 ready 的片有两片以上时折成一行 shard ×N；",
                    "另报未登记的游戏进程（命令行带 RimRunner 实例参数而无登记的进程、命令行无法读取的进程），",
                    "只有命令行带实例参数的 RimWorld 进程属于 RimRunner；",
                    "机器锁有持有者或排队者时另输出一行摘要（持有者数与排队者数，跨产品根），明细见 rimrunner machine")),
                (Switches.Product, OptionDoc.Value("<产品名>",
                    "某产品的全部树，含没有运行中实例的树；输出形状同 --all；",
                    "与 --tree 同时传入时只在该产品的树里认 --tree 的树名，输出那棵树")),
                (Switches.Json, OptionDoc.Flag(
                    "只含产品、树与实例")),
                (Switches.Tree, TreeInstanceDoc),
                (Switches.RunId, RunIdDoc),
            ],
            UsageOverrides =
            [
                ("", "rimrunner [status] [--tree <名>[/<用途>]] [--product <产品名>] [--run-id <runId>]" + Environment.NewLine
                     + "rimrunner status --all [--json]" + Environment.NewLine
                     + "rimrunner status --product <产品名> [--json]"),
            ],
        },
        new()
        {
            CommandName = "machine",
            Detail =
            [
                "  不要求在产品根下。按段输出：",
                "  机器配置：机器级各键（同时执行的实例上限、P 核余量、两项预留、单实例内存估值、加载预算）的生效值与来源，",
                "  含配置文件路径；文件写错即报，不改用缺省值",
                "  机器锁：同时执行的实例上限与 P 核余量要求，逐行列持有者与排队者（跨产品根）",
                "  显存：（已用 + 在途）超出（整卡 − 显存预留）的部分记进内存需求",
                "  物理内存：可用 = 总 − 已用 − 在途（含在途实例的显存溢出） − 预留，另列已用里未归到 RimWorld 进程的部分",
                "  RimWorld 进程：显存读数、私有字节，以及加载中或复用中距预期峰值的差值",
                $"  准入排队者；新实例预期峰值的实测统计与占整机的百分比，缺省只列占比最高的 {MachineReport.EstimatesShown} 条",
                HelpPage.OptionMarker(Switches.Estimates),
            ],
            OptionDocs =
            [
                (Switches.Estimates, OptionDoc.Flag("峰值统计逐条全部展开")),
            ],
        },
        new()
        {
            CommandName = "cache",
            Detail =
            [
                "  不要求在产品根下。全机实例共用一份缓存，不会自动删除",
                "  正在使用该缓存的实例按实例名与 PID 列出",
            ],
        },
        new()
        {
            CommandName = "cache",
            Verb = "clear",
            Detail =
            [
                "  不要求在产品根下。有实例正在使用时一格不删",
            ],
        },
        new()
        {
            CommandName = "vram",
            Detail =
            [
                "  未指定名单即体检全部名单；每份：各贴图档位的模型估算、本名单的批次档位、历史实测、贴图最重的几项",
                "  准入的账在 rimrunner machine",
            ],
            OptionDocs =
            [
                (Switches.Tree, TreeOnlyDoc),
                (Switches.Product, ProductDoc),
            ],
        },
        new()
        {
            CommandName = "unmount",
            Detail =
            [
                "  卸载树下全部实例的登记，整树删除各实例的 savedata，本树的载入目录随之删除",
                "  挂载没有单独的命令：worktree 里第一条拉起游戏的命令（run、start、build-save、showcase <展台Id>、film）自动挂载",
                "  挂载期间本树 <dir>/output 的内容落在主树 <dir>/output/worktrees/<树名>-<哈希>/，删树不丢产出",
                "  停止实例之后把本树 <dir>/output 的 artifacts/、读数库、报告归档复制进主树的 <dir>/output，",
                "  报告归档写入 reports/worktrees/<树名>-<哈希>/；台账不复制。同名而内容不匹配时不覆盖，",
                "  树不卸载、留一份登记作下次 unmount 的入口",
                "  导出档（artifacts/saves/）除外：原名保留修改时间较新的一份（相同时保留主树那份），另一份改名为 <名>.<修改时刻>.rws 留存，",
                $"  每个名字轮换保留 {OutputRetention.SupersededSavesKeep} 份，超过 {RimRunnerArchiveName.MaxAgeDays} 天的删除",
                "  树已删除而未卸载的登记，由之后的 run / start / build-save / showcase / film 自动卸载并归集",
                "  卸载前先停止实例：空闲的直接停止；正在执行批次、冷启动中、被分片组持有或被 live 保留的拒绝",
                "  卸载后已没有登记的 worktree，从该 worktree 的 RimRunner 安装目录启动的编排进程没有别的命令与实例时随之退出；",
                "  本命令等这些编排进程退出再返回，返回后该树可直接删除",
                "",
                HelpPage.OptionMarker(Switches.Tree),
                HelpPage.OptionMarker(Switches.All),
                HelpPage.OptionMarker(Switches.Force),
            ],
            OptionDocs =
            [
                (Switches.Tree, OptionDoc.Value("<名>",
                    "卸载那棵树，可在任何目录执行；不收用途段；",
                    "<名> 是清单里的树名（已删的树仍认），也收路径；",
                    "主树的登记只在它的产品根已不在盘上时卸载")),
                (Switches.All, OptionDoc.Flag("跨产品卸载主树以外的全部树")),
                (Switches.Force, OptionDoc.Flag(
                    "正在执行批次、冷启动中、被分片组持有或被 live 保留的实例也停止（冷启动中的撤销持锁的命令）；",
                    "命令行带本实例参数而归属无法确认的进程不停止，有这样的进程时不卸载")),
                (Switches.Product, ProductDoc),
            ],
        },
        new()
        {
            CommandName = "start",
            Detail =
            [
                "  复用判据与 run 同源：实例进程内的 DLL 与内容指纹均匹配、名单相同则复用空闲实例，",
                "  复用时不回载基线档；复用条件不满足则冷启动",
                "  冷启动时基线档不存在即失败，不自动建档",
                "  空闲满各自的自动停时限后自行退出（自动实例 " + IdleExit.BatchDefault + " 分钟），"
                    + "时限在 " + "机器级 " + RimRunnerConfig.MachineFileName + " 的 [" + RimRunnerConfig.InstancesTable + "] 里指定，"
                    + "0 = 不自动停",
                "",
                HelpPage.OptionMarker(Switches.Modlist),
                HelpPage.OptionMarker(Switches.Force),
                HelpPage.OptionMarker(Switches.TimeoutMinutes),
                HelpPage.OptionMarker(Switches.QueueMinutes),
            ],
            OptionDocs =
            [
                (Switches.Modlist, OptionDoc.Value("<名单>",
                    "指定 mod 名单替代描述符的 defaultModList（写法同 run）；", ModlistBareLine)),
                (Switches.Force, OptionDoc.Flag(
                    "本树的实例正在执行批次、冷启动中或被 live 保留时也停止该实例（撤销持锁的命令），改为冷启动")),
                (Switches.TimeoutMinutes, TimeoutDoc("载档与各段等待")),
                (Switches.QueueMinutes, QueueDoc),
                (Switches.Tree, TreeInstanceDoc),
                (Switches.Product, ProductDoc),
            ],
        },
        new()
        {
            CommandName = "stop",
            Detail =
            [
                "  本树的自动实例、展台实例、拍摄实例与分片实例都包括；空闲或无应答的立即停止；正在执行批次、冷启动中、被分片组持有或被 live 保留的拒绝，",
                "  有被拒的其余仍停止",
                "",
                HelpPage.OptionMarker(Switches.Tree),
                HelpPage.OptionMarker(Switches.RunId),
                HelpPage.OptionMarker(Switches.All),
                HelpPage.OptionMarker(Switches.Force),
                "",
                "  分片实例（shard）与临时自动实例（spare）的登记随游戏进程卸载，停止即不再保留",
            ],
            OptionDocs =
            [
                (Switches.Tree, OptionDoc.Value("<名>[/<用途>]",
                    "停止那棵树的全部实例，可在任何目录执行；",
                    "带用途（batch、showcase、film、shardN）时只停止该实例")),
                (Switches.RunId, RunIdDoc),
                (Switches.All, OptionDoc.Flag(
                    "停止全机的全部实例（各 RimRunner 安装目录的编排进程名下的都在内），可在任何目录执行；",
                    "被拒的实例逐个列出、不停止，其余仍停止")),
                (Switches.Force, OptionDoc.Flag(
                    "正在执行批次、冷启动中、被分片组持有或被 live 保留的也停止（冷启动中的撤销持锁的命令）；",
                    "与 --all 同时传入时先撤销全机在途的命令（排队中、备片中、建档中的都在内），再停止全部实例")),
                (Switches.Product, ProductDoc),
            ],
        },
        new()
        {
            CommandName = "showcase",
            Detail =
            [
                "",
                "  展台实例与自动实例各占一份登记、并存：run、build-save 不操作展台实例，展台实例也不执行批次",
                "  展台实例的停止归 rimrunner stop：零参停止本树全部实例，--tree <树>/showcase 只停止该实例",
                "  展台实例空闲满 " + "机器级 " + RimRunnerConfig.MachineFileName + " 的 [" + RimRunnerConfig.InstancesTable + "] 的 "
                    + RimRunnerConfig.ShowcaseIdleKey + "（缺省 " + IdleExit.ShowcaseDefault + " 分钟）后自行退出，0 = 不自动停",
                "  RimRunner 安装目录换成其他构建后，已启动的展台实例仍保留，退出时机不变",
                "  展台实例的 Prefs 取开发机的真实值",
                "  展台实例显示窗口",
                "  复用判据与 run 同源：DLL、内容指纹、mod 名单或贴图档位变化时自动冷启动，无需先 teardown",
                "  指定 <展台Id> 即立场：世界脏时先回载基线档，立场后暂停",
                "  未指定 <展台Id> 时只进档、不立场：留给人工布置，之后仍可用 <展台Id> 立场",
                ShowcaseVerdictNote,
                "",
                HelpPage.OptionMarker(Switches.Modlist),
                HelpPage.OptionMarker(Switches.Force),
                HelpPage.OptionMarker(Switches.TimeoutMinutes),
                HelpPage.OptionMarker(Switches.QueueMinutes),
            ],
            OptionDocs =
            [
                (Switches.Modlist, OptionDoc.Value("<名单>", "指定 mod 名单（启动实例时）；", ModlistBareLine)),
                (Switches.Force, OptionDoc.Flag(
                    "启动实例：展台实例正在执行或冷启动中时也停止该实例（撤销持锁的命令）；",
                    "机器锁、准入与产出根锁照常排队")),
                (Switches.TimeoutMinutes, TimeoutDoc("载档与立场")),
                (Switches.QueueMinutes, QueueDoc),
                (Switches.Tree, TreeInstanceDoc),
                (Switches.Product, ProductDoc),
            ],
        },
        new()
        {
            CommandName = "showcase",
            Verb = "list",
            Detail =
            [
                "  读产出根 index/ 下的展台名册，不启动实例",
            ],
            OptionDocs =
            [
                (Switches.Tree, TreeOnlyDoc),
                (Switches.Product, ProductDoc),
            ],
        },
        new()
        {
            CommandName = "showcase",
            Verb = "step",
            Detail =
            [
                "  作用于已启动的展台实例；步骤名含空格时加引号",
                "  步骤不撤销，换分支看干净场面须重新立场",
                ShowcaseVerdictNote,
            ],
            OptionDocs =
            [
                (Switches.TimeoutMinutes, TimeoutSingleDoc),
                (Switches.Tree, TreeInstanceDoc),
                (Switches.Product, ProductDoc),
            ],
        },
        new()
        {
            CommandName = "showcase",
            Verb = "teardown",
            Detail =
            [
                "  作用于已启动的展台实例：执行清理栈重置世界后回载基线档",
                ShowcaseVerdictNote,
            ],
            OptionDocs =
            [
                (Switches.TimeoutMinutes, TimeoutSingleDoc),
                (Switches.Tree, TreeInstanceDoc),
                (Switches.Product, ProductDoc),
            ],
        },
        new()
        {
            CommandName = "film",
            Detail =
            [
                "",
                "  在拍摄实例上逐个镜头拍摄（与自动实例各占一份登记、可并存，不执行批次）；",
                "  全部产出后实例回到载档时的世界并进入空闲状态；否则停止实例",
                "  拍摄实例空闲满 " + "机器级 " + RimRunnerConfig.MachineFileName + " 的 [" + RimRunnerConfig.InstancesTable + "] 的 "
                    + RimRunnerConfig.FilmIdleKey + "（缺省 " + IdleExit.FilmDefault + " 分钟）后自行退出，0 = 不自动停",
                "  未指定选择器 = 全部声明镜头的场面；<场面Id> = 该场面的全部镜头",
                "  产出：每次拍摄进产出根的 film/<场面Id>/runs/<时间>-<runId>[-<tag>]/，不覆盖此前的拍摄目录，",
                "  目录里的 film-run.json 记提交、有无未提交改动、DLL 的 MVID 与各条记录",
                "  镜头 <镜头名>.{png,gif,mp4}；路径录像 paths/<k>-<叶>.mp4 与同名逐步静帧目录",
                "  最新副本：每次拍摄后复制到 film/<场面Id>/<镜头名>.latest.<格式> 与 paths/<k>-<叶>.latest.*，",
                "  下一次拍摄即覆盖；发布、对比用 runs/ 下的路径",
                "  画面左下角标识 run <runId> · <提交前 7 位>[*] · <tag>，* = 产品工作树有未提交改动（被 .gitignore 忽略的不算）；无来源身份时写「来源不明」",
                "  记录逐条并入 film/film.json（每个镜头、每条路径录像只保留最近一次，runDir 指向该次拍摄的目录）",
                "  修剪：拍完按场面修剪 runs/，保留最近 keepRecent 份 ∪ 带 tag 的最近 keepTagged 份，",
                "  逐帧只保留最近 keepFramesRuns 份（机器级 config.toml 的 [film]，缺省 "
                    + $"{FilmRetention.Quota.DefaultKeepRecent} / {FilmRetention.Quota.DefaultKeepTagged} / "
                    + $"{FilmRetention.Quota.DefaultKeepFramesRuns}）",
                "  由 ffmpeg 编码：机器级 config.toml 的 ffmpegPath（绝对路径），缺省在 PATH 上找",
                "",
                HelpPage.OptionMarker(Switches.Test),
                HelpPage.OptionMarker(Switches.Paths),
                HelpPage.OptionMarker(Switches.Fps),
                HelpPage.OptionMarker(Switches.Roll),
                HelpPage.OptionMarker(Switches.KeepFrames),
                HelpPage.OptionMarker(Switches.Tag),
                HelpPage.OptionMarker(Switches.Clean),
                HelpPage.OptionMarker(Switches.Mute),
                HelpPage.OptionMarker(Switches.Modlist),
                HelpPage.OptionMarker(Switches.Force),
                HelpPage.OptionMarker(Switches.TimeoutMinutes),
                HelpPage.OptionMarker(Switches.QueueMinutes),
            ],
            OptionDocs =
            [
                (Switches.Test, OptionDoc.Flag(
                    "不出帧、不编码、不需要 ffmpeg：逐场面按每条叶步骤立场并执行到该步，",
                    "只收 <场面Id>")),
                (Switches.Paths, OptionDoc.Flag(
                    "按场面的每条叶路径拍路径录像：逐步执行到各步的完成条件，再按观察时长推进，",
                    "出 MP4 与逐步静帧到拍摄目录的 paths/；不带场面 Id 即全部场面；不能与 --test 同时传入")),
                (Switches.Fps, OptionDoc.Value("N",
                    "路径录像的帧率，须是 60 的约数；缺省 60，即 1 倍速下每帧 1 tick")),
                (Switches.Roll, OptionDoc.Value("N",
                    "只收一个 <场面Id>/<镜头名>（片段）：用 N 个不同的镜头种子各拍一条，出到拍摄目录的 <镜头名>.<种子>.<格式>；",
                    "第 1 条用镜头当前的种子，其余种子由镜头当前的种子与条号推出；不更新最新副本；",
                    "记录进 film.json 的 rolls，再掷整组换掉；场面种子不变")),
                (Switches.KeepFrames, OptionDoc.Flag(
                    "另出逐帧 PNG 到拍摄目录的 <镜头名>.frames/（--roll 时为 <镜头名>.<种子>.frames/），不复制进最新副本；",
                    "mp4 片段的音轨原料留在 <镜头名>.audio/（--roll 时为 <镜头名>.<种子>.audio/）")),
                (Switches.Tag, OptionDoc.Value("<名>",
                    "指定本次拍摄的名字：进拍摄目录名、记录与画面标识，修剪时带 tag 的另有一档保留；",
                    $"英文字母、数字、. _ - 与汉字，至多 {FilmRuns.TagMaxLength} 个字符")),
                (Switches.Clean, OptionDoc.Flag(
                    "不画画面标识与路径录像的步骤角标（宣传素材、逐帧复现核对）；仍更新最新副本")),
                (Switches.Mute, OptionDoc.Flag("mp4 片段不带音轨")),
                (Switches.Modlist, OptionDoc.Value("<名单>",
                    "指定 mod 名单，缺省为描述符的 defaultModList；", ModlistBareLine)),
                (Switches.Force, OptionDoc.Flag(
                    "本树的实例正在执行批次或冷启动中时也停止该实例（撤销持锁的命令）")),
                (Switches.TimeoutMinutes, TimeoutDoc("载档、每个镜头（或每次立场）与收尾")),
                (Switches.QueueMinutes, QueueDoc),
                (Switches.Tree, TreeInstanceDoc),
                (Switches.Product, ProductDoc),
            ],
        },
        new()
        {
            CommandName = "film",
            Verb = Program.FilmCompareWord,
            Detail =
            [
                "  拼接的是两次拍摄里的同一镜头，左 A 右 B；不启动实例，只需 ffmpeg",
                "  片段出 MP4，时长不等时短的一侧停在末帧；静帧出 PNG；高度不同时无法拼接",
                "  只指定场面 Id 时拼接两次共有的全部镜头",
                "  产出 film/<场面Id>/compare/<A 的 runId>-vs-<B 的 runId>/，按 keepRecent 修剪",
                "",
                HelpPage.OptionMarker(Switches.A),
                HelpPage.OptionMarker(Switches.B),
                HelpPage.OptionMarker(Switches.Paths),
            ],
            OptionDocs =
            [
                (Switches.A, OptionDoc.Value("<引用>",
                    "左侧：runId、tag、latest（最近一次拍摄）或 prev（latest 之前的一次）；缺省 prev；",
                    "latest / prev 只计有可比内容的拍摄，tag 重名取最新")),
                (Switches.B, OptionDoc.Value("<引用>",
                    "右侧，写法同 --a；缺省 latest")),
                (Switches.Paths, OptionDoc.Flag("拼接路径录像与逐步静帧")),
                (Switches.Tree, TreeOnlyDoc),
                (Switches.Product, ProductDoc),
            ],
        },
        new()
        {
            CommandName = "cases",
            Verb = "find",
            Detail =
            [
                "  查产品测试工程、随包的用例；子串、大小写不敏感、多词取交（同一条用例上都命中）",
                "  每项输出 Id、档（回归 / 读数用例）、摘要；真实帧用例、环境体检、排除名单里的用例在档后标出",
                "  范围只在含查询词时输出，其余输出条数；参数化用例最多列 6 个 Id，含查询词的在前",
                "  产品用例取自 rimrunner.toml 的 testMod.csproj 所在目录，每次现扫",
                "  在产品根下执行",
                "",
                HelpPage.OptionMarker(Switches.Json),
                HelpPage.OptionMarker(Switches.Full),
            ],
            OptionDocs =
            [
                (Switches.Json, OptionDoc.Flag("输出 JSON：命中项的全部字段，与名册扫描的问题、警告")),
                (Switches.Full, OptionDoc.Flag("命中项的范围与 Id 全部输出")),
                (Switches.Tree, TreeOnlyDoc),
                (Switches.Product, ProductDoc),
            ],
        },
        new()
        {
            CommandName = "cases",
            Verb = CasesCommand.GroupsVerb,
            Detail =
            [
                "  只有一个用例类声明该组时标 [WARN]",
                "  按测试程序集判（产物陈旧时先编译 builds 与 testMod，同 run），与按组选批同一份判据；",
                "  测试工程目录外链进来的用例仍列出，不带位置",
                "  在产品根下执行",
                "",
                HelpPage.OptionMarker(Switches.Json),
            ],
            OptionDocs =
            [
                (Switches.Json, OptionDoc.Flag("输出 JSON：各组与带该组的用例类")),
                (Switches.Tree, TreeOnlyDoc),
                (Switches.Product, ProductDoc),
            ],
        },
        new()
        {
            CommandName = "config",
            Detail =
            [
                "  capture                         本轮名单里各 mod 的设置抄进 <dir>/data/config/mods/<packageId>/<Mod 类名>.xml；",
                "                                  Prefs 全抄，引擎锁定的、随显示设备变化的与列表字段除外：",
                "                                  产品根层追加为 runtime.toml 的 [config.prefs]，名单层写 <dir>/modlists/<名单>.prefs.toml",
                "                                  目标已在且内容不同时一份都不写",
                "                                  本机 Config 里无法归入 mod 的条目只列出，--file 指定的才抄进 files/",
                HelpPage.OptionMarker(Switches.Modlist),
                HelpPage.OptionMarker(Switches.Layer),
                HelpPage.OptionMarker(Switches.File),
                "",
                "  在产品根下执行",
            ],
            OptionDocs =
            [
                (Switches.Modlist, OptionDoc.Value("<名单>",
                    "按这份名单的 mod 抄，缺省为描述符的 defaultModList；都没有时按本机游戏当前启用的 mod；",
                    ModlistBareLine)),
                (Switches.Layer, OptionDoc.Value("root|modlist",
                    "抄进产品根层或名单层（<dir>/data/config/modlists/<名单>/），缺省 root")),
                (Switches.File, OptionDoc.Value("<条目名>[,<条目名>…]",
                    "本机 Config 根下无法归入 mod 的文件或目录，抄进 files/")),
            ],
        },
        new()
        {
            CommandName = "engine",
            Verb = "find",
            Detail =
            [
                "  形如标识符的词各自查类型名与成员名（「类型.成员」两段各取子串，「类型.」列该类型全部成员），",
                "  名字上无匹配时改为查签名与注释全文；其余的词合成一组按全文多词取交；子串、大小写不敏感",
                "  标识符全文也无匹配时按驼峰词前缀列出相近的名字，按所属类型分行",
                "  每项一行：签名、摘要、安装目录 engine/src 下的 文件:行",
                "  类型命中输出成员数（枚举列全部值），另列别的类型里收它、产出它的成员",
                "  命中成员的同一枚举的值与同名重载一并列出，标「同组」",
                "  每项输出摘要与「用于」；范围、参数、返回、异常只输出条数，其中含查询词的照常输出",
                "",
                HelpPage.OptionMarker(Switches.Full),
            ],
            OptionDocs =
            [
                (Switches.Full, OptionDoc.Flag("范围、参数、返回、异常与收它、产出它的成员全部输出")),
            ],
        },
        new()
        {
            CommandName = "engine",
            Verb = "fixtures",
            OptionDocs =
            [
                (Switches.Json, OptionDoc.Flag("输出完整名册")),
            ],
        },
        new()
        {
            CommandName = "reports",
            Detail =
            [
                "  递归合并顶层与 single/ 下的归档",
                "",
                HelpPage.OptionMarker(Switches.Failed),
                HelpPage.OptionMarker(Switches.Limit),
                "",
                $"  标记分三种：PASS；FAIL，已执行且有待处理的，带 {RimRunnerArchiveName.FormatCounts(3, 0, 1, 0, 2, 0)} 计数格；",
                "  REJECTED，开始执行前被拒，无计数格",
                "",
                "  计数格依次为 " + RimRunnerArchiveName.CountsLegend + "，每段一个数加一个字母",
                "",
                "  某条用例的全量批轮次与单条用例归档：history <用例Id>",
            ],
            OptionDocs =
            [
                (Switches.Failed, OptionDoc.Flag("只列判红的（FAIL 与 REJECTED）")),
                (Switches.Limit, OptionDoc.Value("N", "只列命中的最近 N 条（缺省 " + Program.DefaultReportsLimit + "）")),
                (Switches.Tree, TreeOnlyDoc),
                (Switches.Product, ProductDoc),
            ],
        },
        new()
        {
            CommandName = "samples",
            Detail =
            [
                "  转本树产出根下 samples/ 的全部用例。runtime.toml 的 samples.store 选格式，改值之前先转换：",
                "  盘上的格式与描述符不匹配时，批前即拒",
                "  转换先写新文件，逐行还原与原文件比对一致才替换；原文件改名为 *.migrated 保留，核对无误后由人删",
                "  原 jsonl 里逐字重复的行（读取方只认首见的那行）与无法解析的行不转，条数仍输出",
                "  要求本树没有正在执行的实例",
            ],
            OptionDocs =
            [
                (Switches.To, OptionDoc.Value("sqlite|jsonl", "转成哪种格式，须指定")),
                (Switches.Tree, TreeOnlyDoc),
                (Switches.Product, ProductDoc),
            ],
            UsageOverrides =
            [
                ("convert", "rimrunner samples convert --to sqlite|jsonl [--tree <名>] [--product <产品名>]"),
            ],
        },
        new()
        {
            CommandName = "history",
            Detail =
            [
                "  一格一字符；缺省只显示最近 " + Program.StripWindow + " 格，更早的折成行首 …",
                "  指定用例 Id 时逐条显示完整历史，末尾另附该用例的单条用例归档路径",
                "  o 通过；n 执行完但没有断言（NO_ASSERTIONS，不计通过）；X 失败；U 无主故障；? 无法识别的 outcome；| 纪元标记",
                "  ¦ 换档标记：此后的格在另一份基线档上执行；不计样本，n= 只数样本",
                "",
                HelpPage.OptionMarker(Switches.Modlist),
                HelpPage.OptionMarker(Switches.Save),
            ],
            OptionDocs =
            [
                (Switches.Modlist, OptionDoc.Value("<名单>",
                    "指定 mod 名单，查询该名单的台账，缺省为描述符的 defaultModList；", ModlistBareLine)),
                (Switches.Save, OptionDoc.Value("<档名或路径>",
                    "run --save 各批的台账（按名单 + 档另记），写法与 run 相同；档已不在也仍查询")),
                (Switches.Tree, TreeOnlyDoc),
                (Switches.Product, ProductDoc),
            ],
        },
        new()
        {
            CommandName = "history",
            Verb = Program.HistoryMarkWord,
            Detail =
            [
                "  纪元标记在台账里占一格（|）；通过率只算标记之后的样本",
                "  <用例Id> 可逗号分隔、可多个，重复的当即拒绝；全部校验通过才写入文件",
                "",
                HelpPage.OptionMarker(Switches.Modlist),
                HelpPage.OptionMarker(Switches.Save),
            ],
            OptionDocs =
            [
                (Switches.Modlist, OptionDoc.Value("<名单>",
                    "指定 mod 名单，标记写进该名单的台账，缺省为描述符的 defaultModList；", ModlistBareLine)),
                (Switches.Save, OptionDoc.Value("<档名或路径>",
                    "run --save 各批的台账（按名单 + 档另记），写法与 run 相同")),
                (Switches.Tree, TreeOnlyDoc),
                (Switches.Product, ProductDoc),
            ],
            UsageOverrides =
            [
                ("mark", "rimrunner history mark <用例Id>[,<用例Id>…] [<用例Id>…] [--modlist <名单>] [--save <档名或路径>] [--tree <名>] [--product <产品名>]"),
            ],
        },
        new()
        {
            CommandName = "live",
            Detail =
            [
                "  每条 live 命令之后，实例在没有用例或驱动执行时新增的错误级日志按首行计数上屏（[错误级日志] 行），"
                    + "每帧都有新增时另标 [持续]；--json 时只进日志",
                "  do、until 与 eval 可能推进游戏：执行、转后台与保留的规则见 rimrunner live do --help",
            ],
        },
        new()
        {
            CommandName = "live",
            Verb = "pawn",
            Detail =
            [
                "  不指定 pawn 时列出当前地图上全部 spawned pawn（含存档自带的，不限于用例生成的）",
                "",
                HelpPage.OptionMarker(Switches.Stat),
                HelpPage.OptionMarker(Switches.Hediff),
            ],
            OptionDocs =
            [
                (Switches.Stat, OptionDoc.Value("<Def>", "该 pawn 的一项 stat")),
                (Switches.Hediff, OptionDoc.Flag("该 pawn 的 hediff 表")),
                .. LiveCommonDocs,
            ],
        },
        new()
        {
            CommandName = "live",
            Verb = "screenshot",
            OptionDocs =
            [
                (Switches.Label, OptionDoc.Value("<名>", "截图文件名里的标签（缺省 capture）")),
                (Switches.Rect, OptionDoc.Value("x,y,w,h", "截图区域（GUI 坐标；缺省整屏）")),
                .. LiveCommonDocs,
            ],
        },
        new()
        {
            CommandName = "live",
            Verb = "save",
            Detail =
            [
                "  导出的是现场档；基线档由 build-save 建",
            ],
            OptionDocs = [.. LiveCommonDocs],
        },
        new()
        {
            CommandName = "live",
            Verb = "perf",
            Detail =
            [
                Column("  不带子动作", "探针态"),
                Column("  arm | reset | compare | dump", "arm 装探针并启用、compare 另装对照组，"),
                Column("", "这两组探针都留在实例上直到实例退出；reset 只清累积序列"),
                "",
                HelpPage.OptionMarker(Switches.Prefix),
            ],
            OptionDocs =
            [
                (Switches.Prefix, OptionDoc.Value("<键前缀>", "dump 可加：只导出该键前缀下的条目")),
                .. LiveCommonDocs,
            ],
        },
        new()
        {
            CommandName = "live",
            Verb = "dev",
            Detail =
            [
                "  列原版 dev 菜单一个节点的子项（缺省三个根），路径以 \\ 分隔，写法同原版",
                "  生成子菜单不展开、只标未展开，展开用 live do dev-expand；执行用 live do dev",
                "",
                HelpPage.OptionMarker(Switches.Find),
            ],
            OptionDocs =
            [
                (Switches.Find, OptionDoc.Value("<文本>",
                    "在已展开的部分里按路径搜（不分大小写），不能与路径同时传入")),
                .. LiveCommonDocs,
            ],
        },
        new()
        {
            CommandName = "live",
            Verb = "menu",
            Detail =
            [
                "  每个选项一行：序号、文字、禁用、是否画在可见区；序号供 live do menu-pick 用",
            ],
            OptionDocs = [.. LiveCommonDocs],
        },
        new()
        {
            CommandName = "live",
            Verb = "windows",
            Detail =
            [
                "  窗口栈从下到上：序号、类型、标题、层、是否强制暂停、Esc 是否关闭该窗口；序号供 live do window-close 用",
            ],
            OptionDocs = [.. LiveCommonDocs],
        },
        new()
        {
            CommandName = "live",
            Verb = "gizmos",
            Detail =
            [
                "  每个按钮一行：序号、文字、类型、开关状态、隐藏与禁用；序号供 live do gizmo 用",
            ],
            OptionDocs = [.. LiveCommonDocs],
        },
        new()
        {
            CommandName = "live",
            Verb = "progress",
            OptionDocs = [.. LiveCommonDocs],
        },
        new()
        {
            CommandName = "live",
            Verb = "do",
            OptionDocs =
            [
                (Switches.LongEventSeconds, LongEventDoc),
                .. LiveCommonDocs,
            ],
            UsageOverrides =
            [
                ("do", "rimrunner live do [<名> [--<参数>=<值> …]] [--long-event-seconds <秒>] [--json] [--tree <名>[/<用途>]] [--product <产品名>] [--run-id <runId>]"),
            ],
        },
        new()
        {
            CommandName = "live",
            Verb = "eval",
            Detail =
            [
                "  回执名 eval；可能推进游戏，执行、转后台与保留的规则同 live do，见 rimrunner live do --help",
                "  整段是表达式时输出它的值；语句里用 return 值 或 Show(值, 标签) 输出，等帧写 yield return",
                "  可用局部名 ctx（RimRunnerRegressionContext）与 map；可访问引擎、游戏与测试程序集的非公开成员",
                "  using：测试工程的 global using，加 System、System.Collections.Generic、System.Linq、Verse、RimWorld、RimRunner",
                "",
                HelpPage.OptionMarker(Switches.EvalFile),
                HelpPage.OptionMarker(Switches.Using),
                HelpPage.OptionMarker(Switches.LongEventSeconds),
            ],
            OptionDocs =
            [
                (Switches.EvalFile, OptionDoc.Value("<路径>",
                    "读这个文件当 eval 代码编译，不能与代码同时传入")),
                (Switches.Using, OptionDoc.Value("<命名空间>",
                    "eval 代码追加的 using，可重复指定")),
                (Switches.LongEventSeconds, LongEventDoc),
                .. LiveCommonDocs,
            ],
            UsageOverrides =
            [
                ("eval", "rimrunner live eval (<代码> | --file <路径>) [--using <命名空间>]… [--long-event-seconds <秒>] [--json] [--tree <名>[/<用途>]] [--product <产品名>] [--run-id <runId>]"),
            ],
        },
        new()
        {
            CommandName = "live",
            Verb = "until",
            OptionDocs =
            [
                (Switches.LongEventSeconds, LongEventDoc),
                .. LiveCommonDocs,
            ],
            UsageOverrides =
            [
                ("until", "rimrunner live until [<条件> --within=<时长> [--<参数>=<值> …]] [--long-event-seconds <秒>] [--json] [--tree <名>[/<用途>]] [--product <产品名>] [--run-id <runId>]"),
            ],
        },
        new()
        {
            CommandName = "live",
            Verb = "status",
            Detail =
            [
                "  已结束的，回执与返回同前台执行完；仍在执行的按未完成返回",
                "  须指向原命令的那个实例（同样的 --tree）",
            ],
            OptionDocs = [.. LiveCommonDocs],
        },
        new()
        {
            CommandName = "live",
            Verb = "cancel",
            Detail =
            [
                "  等该操作在两帧之间中止；已结束的输出该操作的结果",
                "  须指向原命令的那个实例（同样的 --tree）",
            ],
            OptionDocs = [.. LiveCommonDocs],
        },
    ];

    internal static IReadOnlyList<string> CommonOverviewLines(IEnumerable<string> refusingTree) =>
    [
        "  --tree <名>[/<用途>]            在那棵树里执行、不看 cwd，带用途（batch、showcase、film、shardN）时只对该实例；",
        "  --tree <名>                     在那棵树里执行、不看 cwd，作用于整棵树，不收用途段；",
        "                                  <名> 是清单里的树名，匹配到多棵时列出候选；也收路径。缺省按 cwd 向上找产品根",
        "  " + ProductLineContent,
        "                                  " + string.Join("、", refusingTree) + " 不接受 --tree 与 --product",
        "  --version                       输出版本后退出",
        "  --help / -h                     输出本帮助；跟在命令后则只输出该命令的帮助",
    ];

    private const string ProductLineContent =
        "--product <产品名>              与 --tree 同时传入时只在该产品的树里认树名；"
        + "不带 --tree 时核 cwd 所在的产品，不匹配即拒";

    private static string ProductLine => "  " + ProductLineContent;

    public static bool IsCommand(string token) => CommandTable.Find(token) != null;

    internal static IEnumerable<string> PageNames => Pages.Select(page => page.CommandName);

    internal static IEnumerable<(string Topic, IReadOnlyList<CliOperation> Operations)> PageTargets
    {
        get
        {
            foreach (CliCommand command in CommandTable.All)
            {
                yield return (command.Name, [.. command.Operations.Where(operation => !HasOwnPage(command, operation))]);
                foreach (CliOperation operation in command.Operations.Where(operation => HasOwnPage(command, operation)))
                    yield return (command.Name + " " + operation.Verb, [operation]);
            }
        }
    }

    private static bool HasOwnPage(CliCommand command, CliOperation operation) =>
        operation.Verb != null && Pages.Any(page => page.CommandName == command.Name && page.Verb == operation.Verb);

    internal static string SummaryOf(string command) => Declared(command).Summary;

    internal static string DisplaySummary(string command)
    {
        CliCommand declared = Declared(command);
        return declared.AllReadOnly
            ? declared.Summary + CommandTable.Registry.Messages.ReadOnlySuffix
            : declared.Summary;
    }

    internal static IReadOnlyList<string> ExitNoteLines(string topic)
    {
        IReadOnlyList<CliOperation> operations = PageTargets.First(target => target.Topic == topic).Operations;
        List<(string Name, IReadOnlyList<CliExitNote> Notes)> entries =
        [
            .. operations
                .Select(operation => (operation.DisplayLabel, operation.ExitNotes))
                .Where(entry => entry.Item2.Count > 0),
        ];
        if (entries.Count == 0) return [];
        if (operations.Count == 1)
            return ["退出码  " + FormatNotes(entries[0].Item2)];

        int width = entries.Max(entry => TerminalWidth.Of(entry.Name));
        return
        [
            "退出码",
            .. entries.Select(entry =>
                "  " + TerminalWidth.Pad(entry.Name, width + 2, minGap: 2)
                + FormatNotes(entry.Item2)),
        ];
    }

    private static string FormatNotes(IReadOnlyList<CliExitNote> notes) =>
        string.Join(CommandTable.Registry.Messages.NoteSeparator,
            notes.Select(note => CommandTable.Registry.Messages.ExitCodeEntry(note.Code, note.Meaning)));

    internal static IReadOnlyList<(CliOperation Operation, string Line)> UsageLines(string command)
    {
        CliCommand declared = Declared(command);
        return [.. declared.Operations.Select(operation =>
            (operation, UsageLine(declared, operation, PageOf(declared, operation))))];
    }

    internal static bool HasUsageOverride(string command, CliOperation operation) =>
        PageOf(Declared(command), operation).UsageOverrides.Any(entry => entry.Verb == (operation.Verb ?? ""));

    internal static IReadOnlyList<CliOption> DocumentedSwitches(string topic)
    {
        (string command, string? verb) = SplitTopic(topic);
        return [.. Pages.Where(page => page.CommandName == command && page.Verb == verb)
            .SelectMany(page => page.OptionDocs.Select(entry => entry.Option))];
    }

    private static (string Command, string? Verb) SplitTopic(string topic)
    {
        string[] words = topic.Split(' ', 3);
        return (words[0], words.Length > 1 ? words[1] : null);
    }

    public static bool Requested(IReadOnlyList<string> args, out string? topic)
    {
        if (CliParser.Parse(CommandTable.Registry, [.. args]) is ParseResult.Help help)
        {
            topic = help.Topic ?? (help.Verb != null ? help.CommandName + " " + help.Verb : help.CommandName);
            return true;
        }

        topic = null;
        return false;
    }

    internal static int Print(ParseResult.Help help)
    {
        Console.Write(HelpRenderer.Render(
            CommandTable.Registry,
            help,
            Pages,
            topic => RenderDriveTopic(topic, includeExitNotes: help.ExitCode == ExitCodes.Ok)));
        return help.ExitCode;
    }

    public static int Print(string? topic, bool exitNotes = true)
    {
        if (topic == null)
        {
            PrintOverview();
            return ExitCodes.Ok;
        }

        if (topic == DoTopic || topic == UntilTopic || topic.StartsWith(DoTopic + " ", StringComparison.Ordinal))
        {
            Console.Write(RenderDriveTopic(topic, exitNotes)!);
            return ExitCodes.Ok;
        }

        (string command, string? verb) = SplitTopic(topic);
        CliCommand? declared = CommandTable.Find(command);
        if (declared == null || verb != null && declared.Operations.All(operation => operation.Verb != verb))
        {
            Console.WriteLine($"[FAIL] 未知命令：{topic}");
            Console.WriteLine();
            PrintOverview();
            return ExitCodes.Usage;
        }

        Console.Write(HelpRenderer.RenderCommand(CommandTable.Registry, declared, Pages, verb, includeExitNotes: exitNotes));
        return ExitCodes.Ok;
    }

    public static void PrintOverview() =>
        Console.Write(HelpRenderer.RenderOverview(CommandTable.Registry, Pages));

    internal static string? ResolveTopic(IReadOnlyList<string> words)
    {
        if (words.Count < 2 || words[0] != "live") return null;
        if (words[1] == "do") return words.Count >= 3 ? DoTopic + " " + words[2] : DoTopic;
        if (words[1] == "until") return UntilTopic;
        return null;
    }

    private static string UsageLine(CliCommand command, CliOperation operation, HelpPage page)
    {
        var single = new CliCommand
        {
            Name = command.Name,
            Group = command.Group,
            Layer = command.Layer,
            Summary = command.Summary,
            Operations = [operation],
        };
        var usageOnlyPage = new HelpPage
        {
            CommandName = page.CommandName,
            UsageOverrides = page.UsageOverrides,
            OptionDocs = page.OptionDocs,
        };
        string rendered = HelpRenderer.RenderCommand(CommandTable.Registry, single, usageOnlyPage, includeExitNotes: false);
        string[] lines = rendered.TrimEnd('\n').Split('\n');
        return string.Join(TerminalWidth.Newline, lines.TakeWhile(line => !line.StartsWith("  ", StringComparison.Ordinal)));
    }

    private const string DoSummary =
        "执行一个内置原语或 <dir>/live/<名>.cs 里的脚本动作，只报执行结果；不带名字时列出原语、脚本动作与有错的脚本";

    private const string UntilSummary = "判一个内置条件或 <dir>/live/<名>.cs 里的脚本条件；不带名字时列出条件与有错的脚本";

    private static IEnumerable<string> ScriptRuleLines() =>
    [
        "  <dir>/live/<名>.cs 一个文件一个动作或条件：恰好一个派生 RimRunnerLiveAction 或 RimRunnerLiveCondition 的非抽象类，",
        "    名字即文件名，不扫子目录",
        "  每次调用前按实例已装载的程序集编译；编译失败或无法装载即拒，零执行",
        "  脚本带测试工程的 global using，可访问引擎、游戏与测试程序集的非公开成员；脚本之间不互相引用，共用的代码放测试工程",
        "  文件没改时多次调用共用同一个程序集与静态字段，改过即换新程序集",
        "  参数表写在脚本里，实例上执行 rimrunner live do 不带名字时列出",
    ];

    private static IEnumerable<string> DriveRuleLines() =>
    [
        "执行",
        "  live do 与 live until 可能推进游戏",
        "  自动实例、分片实例、兼容关闭实例、展台实例与拍摄实例都受理",
        "  执行期间实例报忙（runState=driving）：另一次 live do 或 live until、执行批次、展台的立场、步骤、拆场，",
        "    以及拍摄请求，都被拒；live status 与 live cancel 照常应答",
        "  以下情形下驱动被拒：实例正在冷启动或被分片组持有；展台步骤进行中；立场或拆场未完成；",
        "    拍摄实例未就绪或正在收尾；拍摄请求在途",
        "  Ctrl+C 在两帧之间中止，已执行的帧不撤回",
        "  驱动改过的进程状态不还原：受过驱动的实例不再被 run、showcase、film 复用，启动实例时停止该实例再冷启动",
        "  受过驱动的实例的显存与内存读数不记进准入的历史",
        "",
        "等待与时限",
        "  前台等满 " + RimRunnerConfig.LiveWaitKey + "（缺省 " + LiveDriveClient.WaitMinutesDefault
            + " 分钟，0 = 等到结束）仍未结束时，按 " + RimRunnerConfig.LiveAfterWaitKey + " 处置：",
        "    " + LiveDriveClient.AfterWaitBackground + "（缺省）转后台：输出操作 id 后命令结束、按未完成返回，动作在实例上继续执行",
        "    " + LiveDriveClient.AfterWaitCancel + " 请求取消，等该操作在两帧之间中止",
        "  从开始执行起满 " + RimRunnerConfig.LiveLimitKey + "（缺省 " + LiveDriveClient.LimitMinutesDefault
            + " 分钟，0 = 不设上限）即中止，转后台的也一样",
        "  " + RimRunnerConfig.LiveWaitKey + "、" + RimRunnerConfig.LiveAfterWaitKey + " 与 " + RimRunnerConfig.LiveLimitKey + " 都在 " + "机器级 " + RimRunnerConfig.MachineFileName + " 的 [" + RimRunnerConfig.InstancesTable + "] 里",
        Column("  status <id>", "查一次转后台的操作：已结束的，回执与返回同前台执行完；仍在执行的按未完成返回"),
        Column("  cancel <id>", "取消转后台的操作，等该操作在两帧之间中止；已结束的输出该操作的结果"),
        Column("", "status 与 cancel 须指向原命令的那个实例（同样的 --tree）"),
        "",
        "保留",
        "  自动实例、分片实例与兼容关闭实例上，live do 与 live until 执行完把实例的保留延到至少「此刻 + "
            + RimRunnerConfig.LiveGraceKey + "」：",
        "    缺省 " + LiveHolds.GraceDefault
            + " 分钟，0 = 不延长，被拒的不延长；键在 " + "机器级 " + RimRunnerConfig.MachineFileName + " 的 [" + RimRunnerConfig.InstancesTable + "] 里",
        "  保留期内 run、start、build-save 不接手该实例，stop、unmount 不停止该实例，分片组挑片与借片都跳过该实例，",
        "    内存准入驱逐与排空也都跳过该实例",
        "  空闲时间在保留期内一直从零重新计算，空闲停止的时限从保留结束起算",
        "  run、start、build-save、stop、unmount 带 --force 时停止该实例，启动实例的命令随后冷启动",
        "  展台实例与拍摄实例不保留：驱动结束后空闲时间从零重新计算",
        "  保留随实例进程退出而结束",
    ];

    internal const string DoTopic = "live do";
    internal const string UntilTopic = "live until";

    internal static string? RenderDriveTopic(string topic, bool includeExitNotes = true)
    {
        if (topic != DoTopic && topic != UntilTopic && !topic.StartsWith(DoTopic + " ", StringComparison.Ordinal))
            return null;

        CliCommand live = Declared("live");
        bool until = topic == UntilTopic;
        CliOperation operation = live.Operations.First(candidate => candidate.Verb == (until ? "until" : "do"));
        HelpPage page = PageOf(live, operation);

        var sb = new StringBuilder();
        if (topic.StartsWith(DoTopic + " ", StringComparison.Ordinal))
        {
            string name = topic[(DoTopic.Length + 1)..];
            RimRunnerDriveTable.Verb? verb = RimRunnerDriveTable.Find(name);
            if (verb != null)
                return TerminalWidth.Finish(BuildVerbPage(verb));

            sb.Append($"{name} 不是内置原语；脚本动作从 <dir>/live/<名>.cs 装载，参数表在实例上执行 rimrunner live do 不带名字时列出")
              .AppendLine().AppendLine();
        }

        sb.AppendLine(UsageLine(live, operation, page));
        sb.AppendLine("  " + (until ? UntilSummary : DoSummary));
        sb.AppendLine();

        if (until)
        {
            foreach (RimRunnerDriveTable.Param param in RimRunnerDriveTable.Until.Params)
                sb.AppendLine(ParamLine(param, "  "));
            sb.AppendLine();
            sb.AppendLine("  " + RimRunnerDriveTable.UntilLetter + " 是内置条件（" + RimRunnerDriveTable.UntilLetterTitle
                              + "），不收 --within=0；其余条件是 <dir>/live/<名>.cs 里的脚本，参数名不得是 within 与 speed；");
            sb.AppendLine("  条件与参数表在实例上执行 rimrunner live until 不带名字时列出；" + ArgSyntaxNote);
            sb.AppendLine();
            sb.AppendLine("执行与保留的规则同 live do，见 rimrunner live do --help");
        }
        else
        {
            sb.AppendLine("内置原语（各自的参数见 rimrunner live do <原语> --help）");
            foreach (RimRunnerDriveTable.VerbGroup group in RimRunnerDriveTable.Groups)
            {
                sb.AppendLine("  " + group.Title);
                foreach (RimRunnerDriveTable.Verb verb in group.Verbs)
                    sb.AppendLine(Column("    " + verb.Name, verb.Summary));
            }

            sb.AppendLine();
            sb.AppendLine("脚本动作");
            foreach (string line in ScriptRuleLines()) sb.AppendLine(line);
            sb.AppendLine("  " + ArgSyntaxNote);
            sb.AppendLine();
            foreach (string line in DriveRuleLines()) sb.AppendLine(line);
        }

        sb.AppendLine();
        foreach (CliOption option in operation.Options.OrderBy(option =>
                     option == Switches.Product ? 2 : option == Switches.Tree ? 1 : 0))
        {
            sb.AppendLine(option == Switches.Product && FindDoc(page, option) == null
                ? ProductLine
                : SwitchLine(page, option));
        }

        if (includeExitNotes && operation.ExitNotes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("退出码  " + FormatNotes(operation.ExitNotes));
        }

        return TerminalWidth.Finish(sb.ToString());
    }

    private static string BuildVerbPage(RimRunnerDriveTable.Verb verb)
    {
        IReadOnlyList<string> oneOf = verb.OneOf ?? [];
        string required = string.Concat(verb.Params.Where(param => param.Required)
            .Select(param => " --" + param.Name + "=" + param.Placeholder));
        string group = oneOf.Count > 0
            ? " (" + string.Join(" | ", oneOf.Select(name => "--" + name + "=…")) + ")"
            : "";
        string optional = verb.Params.Any(param => !param.Required && !oneOf.Contains(param.Name))
            ? " [--<参数>=<值> …]"
            : "";
        var sb = new StringBuilder();
        sb.AppendLine("rimrunner live do " + verb.Name + required + group + optional);
        sb.AppendLine("  " + verb.Summary);
        if (verb.Params.Count > 0)
        {
            sb.AppendLine();
            foreach (RimRunnerDriveTable.Param param in verb.Params)
                sb.AppendLine(ParamLine(param, "  "));
        }

        sb.AppendLine();
        sb.AppendLine("选项、执行规则、保留规则与退出码都同 live do，见 rimrunner live do --help");
        return sb.ToString();
    }

    private const string ArgSyntaxNote = "方法参数一律写 --名=值；CLI 选项与取值用空格分开";

    private static string ParamLine(RimRunnerDriveTable.Param param, string indent) =>
        indent + "--" + param.Name + "=" + param.Placeholder + (param.Required ? "（必填）" : "") + "  " + param.Meaning;

    private static string Column(string prefix, string text) =>
        prefix + new string(' ', Math.Max(1, DescriptionColumn - TerminalWidth.Of(prefix))) + text;

    private static string SwitchLine(HelpPage page, CliOption option)
    {
        OptionDoc doc = FindDoc(page, option)
                        ?? throw new InvalidOperationException($"命令 {page.CommandName} 的选项 {option.FlagName} 没有说明");

        string prefix = "  " + option.FlagName
                        + (doc.Placeholder.Length > 0 ? " " + doc.Placeholder : string.Empty);
        if (doc.Lines.Count == 0) return prefix;

        int pad = Math.Max(1, DescriptionColumn - TerminalWidth.Of(prefix));
        return prefix + new string(' ', pad) + doc.Lines[0]
               + string.Concat(doc.Lines.Skip(1).Select(line =>
                   Environment.NewLine + new string(' ', DescriptionColumn) + line));
    }

    private static OptionDoc? FindDoc(HelpPage page, CliOption option) =>
        page.OptionDocs.Where(entry => ReferenceEquals(entry.Option, option)).Select(entry => entry.Doc).FirstOrDefault();

    private static HelpPage PageOf(CliCommand command, CliOperation operation) =>
        Pages.FirstOrDefault(page => page.CommandName == command.Name && page.Verb == operation.Verb && operation.Verb != null)
        ?? Pages.FirstOrDefault(page => page.CommandName == command.Name && page.Verb == null)
        ?? throw new InvalidOperationException($"操作 {operation.Name} 没有帮助页");

    private static CliCommand Declared(string command) =>
        CommandTable.Find(command) ?? throw new InvalidOperationException($"命令 {command} 不在声明表里");
}
