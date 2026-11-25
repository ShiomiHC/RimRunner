using System.Text.Json;
using System.Text.Json.Nodes;
using RimRunner;
using RimTooling.IO;

namespace RimRunnerCli;

public sealed class ManifestDocument
{
    public const string FileName = "rimrunner.toml";
    public const string DefaultDir = "RimRunner";

    [DescriptorKey("产品名，进报告与终端", Required = true)]
    public string Product { get; set; } = "";
    [DescriptorKey("工作区目录，相对产品根、不含 ..。名单在 <dir>/modlists/，测试数据在 <dir>/data/（基线档 saves/、设置 config/、建档描述符 newgame.toml），产出在 <dir>/output/，live do / live until 调的脚本在 <dir>/live/（一个文件一个动作或条件，文件名即名字）；runtime.toml、preflight.toml、excluded.toml 也放这里。未指定 = RimRunner")]
    public string Dir { get; set; } = DefaultDir;
    [DescriptorKey("基线档名的前缀，相对名、不含 ..。档名是 <saveNamePrefix>-<名单名>；命令行与 defaultModList 都未指定名单时，档名即该前缀。建档的 seed 缺省也取该前缀", Required = true)]
    public string SaveNamePrefix { get; set; } = "";
    [DescriptorKey("未指定 --modlist 时用的名单名。未指定 = 用本机游戏当前启用的 mod，不读名单文件")]
    public string DefaultModList { get; set; } = "";
    [DescriptorKey("units 之外每轮须装上的 packageId（Harmony、DLC、前置框架）。非可选 unit 自动算必需，不在这里重复写。生效 mod 名单缺必需项时拒绝执行；名单有意不带某项时，在名单文件 [rimrunner] 写 omit_required = [\"<id>\"]")]
    public List<string> RequiredMods { get; set; } = [];
    [DescriptorKey("产物陈旧或缺失时是否先按 builds、testMod 的顺序编译。未指定 = true。false 时陈旧只警告、照常执行，缺产物仍拒绝执行")]
    public bool AutoBuild { get; set; } = true;
    [DescriptorKey("合成测试 mod 时带上引擎自测程序集。只在带自测的维护者构建里可用", MaintainerOnly = true)]
    public bool EngineSelfTests { get; set; }
    [DescriptorKey("产品的 mod，一个 mod 一项。packageId 读自该 mod 的 About/About.xml", Required = true)]
    public List<UnitEntry> Units { get; set; } = [];
    [DescriptorKey("产品与兼容程序集，按编译顺序排、被引用的在前。测试工程不写在这里，由 testMod 接在最后")]
    public List<BuildEntry> Builds { get; set; } = [];
    [DescriptorKey("测试 mod 的合成参数。CLI 在每次冷启动时把测试程序集与引擎合成为一个独立的测试 mod", Required = true)]
    public TestModEntry TestMod { get; set; } = new();

    public sealed class UnitEntry
    {
        [DescriptorKey("mod 目录（含 About/About.xml）相对产品根的路径", Required = true)]
        public string Source { get; set; } = "";
        [DescriptorKey("载入目录名的前缀，各单元不重复。未指定 = source 的末段目录名")]
        public string Name { get; set; } = "";
        [DescriptorKey("写 true 即可选单元：不算必需，名单带它才部署（扮演第三方 mod、维护者探针一类）。未指定 = false")]
        public bool Optional { get; set; }
    }

    public sealed class BuildEntry
    {
        [DescriptorKey("工程文件（.csproj），相对产品根。陈旧或缺产物时按 builds 的顺序编译。未指定 = 预编译产物，只核新鲜度、不编译")]
        public string Csproj { get; set; } = "";
        [DescriptorKey("产物 DLL，相对产品根。程序集名取文件名，写有 csproj 时须与它的 AssemblyName 一致。实例装载的程序集与磁盘上的程序集不匹配时不复用实例，rimrunner judge 判报告陈旧", Required = true)]
        public string Dll { get; set; } = "";
    }

    public sealed class TestModEntry
    {
        [DescriptorKey("合成测试 mod 的 packageId 前缀，CLI 按实例追加后缀", Required = true)]
        public string IdPrefix { get; set; } = "";
        [DescriptorKey("测试工程的 .csproj，相对产品根。编译排在 builds 之后", Required = true)]
        public string Csproj { get; set; } = "";
        [DescriptorKey("测试程序集的产出路径（相对产品根），须与测试工程的 OutputPath 一致", Required = true)]
        public string Dll { get; set; } = "";
        [DescriptorKey("测试专用的 mod 内容目录（相对产品根）：Defs / Patches / Languages 与根级文件，冷启动时整树拷进测试 mod 根。未指定 = 没有；写则须存在")]
        public string? ContentDir { get; set; }
        [DescriptorKey("合成测试 mod 的显示名。未指定 = 测试程序集名")]
        public string DisplayName { get; set; } = "";
        [DescriptorKey("合成 About.xml 的游戏版本，如 1.7。未指定 = 取游戏目录 Assembly-CSharp.dll 或 Version.txt 的主次版本")]
        public string SupportedVersion { get; set; } = "";
    }
}

public sealed class RuntimeDocument
{
    public const string FileName = "runtime.toml";

    [DescriptorKey("实例 Config 的基底与产品根层 Prefs。未指定 = 各键取缺省")]
    public ConfigSection Config { get; set; } = new();
    [DescriptorKey("读数库（<dir>/output/samples/）的存储格式。未指定 = 各键取缺省")]
    public OrchestratorDescriptor.SamplesDescriptor Samples { get; set; } = new();
    [DescriptorKey("用例期间持续错误级日志的熔断阈。未指定 = 主循环连续 20 次读数见到新增错误级日志，或单个用例窗口累计 40 条")]
    public OrchestratorDescriptor.BreakerDescriptor? Breaker { get; set; }
    [DescriptorKey("自动实例与分片实例（run、build-save、分片组）的窗口尺寸，像素，启动即锁定；拍摄实例开拍前的尺寸、未指定输出尺寸的镜头与路径录像也取这一尺寸。宽高须为偶数；宽 < 1024 与高 < 768 不得同时成立。未指定 = 1920×1080")]
    public OrchestratorDescriptor.WindowSizeDescriptor WindowSize { get; set; } = new();
    [DescriptorKey("溯源档。冷启动时经启动参数交给游戏，实例携带的溯源档与本轮不同即不复用")]
    public DiagnosticsSection Diagnostics { get; set; } = new();
    [DescriptorKey("实验开关。冷启动时经启动参数交给游戏，实例携带的实验开关与本轮不同即不复用")]
    public ExperimentsSection Experiments { get; set; } = new();

    public sealed class ConfigSection
    {
        [DescriptorKey("实例 Prefs 的基底：default（各字段取游戏缺省，唯 ignoreSystemScaling 为 True：界面缩放不乘系统缩放）或 player（本机游戏的 Prefs.xml、KeyPrefs.xml、Knowledge.xml）。未指定 = default")]
        public string PrefsBase { get; set; } = "";
        [DescriptorKey("实例 mod 设置的基底：none（一份都不带）或 player（本机游戏 Config 里本轮名单 mod 的设置与无法归入 mod 的条目）。未指定 = none")]
        public string ModSettingsBase { get; set; } = "";
        [DescriptorKey("实例 Prefs 的产品根层覆盖，键名即 Prefs.xml 的元素名；名单层另写 <dir>/modlists/<名单>.prefs.toml。引擎锁定的键不能写")]
        public Dictionary<string, JsonElement> Prefs { get; set; } = [];
    }

    public sealed class DiagnosticsSection
    {
        [DescriptorKey("溯源档：判红时报告带足迹与世界的差，不必重新执行。未指定 = true")]
        public bool Trace { get; set; } = true;
    }

    public sealed class ExperimentsSection
    {
        [DescriptorKey("缺省上独立小图的用例压回主图执行。未指定 = false")]
        public bool ForceMainMap { get; set; }
    }
}

public sealed class PreflightDocument
{
    public const string FileName = "preflight.toml";

    [DescriptorKey("冷启动前执行的检查（如 lint），任一返回非零即拒绝执行；复用运行中的实例时不执行。未指定 = 不执行")]
    public List<OrchestratorDescriptor.PreflightCommandDescriptor> Cold { get; set; } = [];
    [DescriptorKey("worktree 首次挂载（建本树第一份登记）前执行的检查，形状同 cold。未指定 = 不执行")]
    public List<OrchestratorDescriptor.PreflightCommandDescriptor> Mount { get; set; } = [];
}

public sealed class ExcludedDocument
{
    public const string FileName = "excluded.toml";

    [DescriptorKey("任何批都不执行的用例，报告逐条列出 Id。命令行加 --include-excluded 时本次忽略这份名单")]
    public List<OrchestratorDescriptor.ExcludedCaseDescriptor> Cases { get; set; } = [];
}

internal static class DescriptorFile
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static T? Read<T>(string path, string kindLabel, string? workspaceDir) where T : class
    {
        string file = Path.GetFileName(path);
        (bool exists, TomlTable table) = TomlFile.Read(path, file, message => new DescriptorException(message));
        if (!exists) return null;

        JsonObject root = TomlFile.ToJson(table, path, file, message => new DescriptorException(message));
        string dir = workspaceDir
                     ?? (root.TryGetPropertyValue("dir", out JsonNode? node) && node is JsonValue value
                         && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text)
                         ? text
                         : ManifestDocument.DefaultDir);
        DescriptorKeyGate.Reject(root, path, typeof(T), kindLabel, Elsewhere(typeof(T), dir));
        try
        {
            return root.Deserialize<T>(ReadOptions) ?? throw new JsonException("根元素为 null");
        }
        catch (JsonException exception)
        {
            throw new DescriptorException(
                $"{kindLabel}的值类型不匹配：{path}\n  {DescriptorDocs.KeyOfJsonPath(exception.Path)}{exception.Message}");
        }
    }

    public static IReadOnlyDictionary<string, string> Elsewhere(Type self, string workspaceDir)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(Type type, string where)
        {
            if (type == self) return;
            foreach ((System.Reflection.PropertyInfo property, _) in DescriptorDocs.Keys(type))
                map.TryAdd(DescriptorDocs.KeyOf(property), where);
        }

        string dir = workspaceDir.TrimEnd('/');
        Add(typeof(ManifestDocument), ManifestDocument.FileName);
        Add(typeof(RuntimeDocument), dir + "/" + RuntimeDocument.FileName);
        Add(typeof(PreflightDocument), dir + "/" + PreflightDocument.FileName);
        Add(typeof(ExcludedDocument), dir + "/" + ExcludedDocument.FileName);
        foreach (string key in RimRunnerConfig.MachineKeys)
            map.TryAdd(key, "机器级 " + RimRunnerConfig.MachineFileName + "（" + RimRunnerConfig.MachinePath + "）");
        return map;
    }
}
