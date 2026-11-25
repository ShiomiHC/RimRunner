using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using RimRunner;

namespace RimRunnerCli;

public sealed class OrchestratorDescriptor
{
    public string Product { get; set; } = "";
    public string GameExe { get; set; } = "";
    public string WorkspaceDir { get; set; } = "";
    public string DataDir { get; set; } = "";
    public string ModListsDir { get; set; } = "";
    public string SaveNamePrefix { get; set; } = "";

    public string PrefsBase { get; set; } = "";
    public string ModSettingsBase { get; set; } = "";
    public Dictionary<string, JsonElement> Prefs { get; set; } = [];

    public List<string> RequiredPackageIds { get; set; } = [];
    public List<UnitDescriptor> Units { get; set; } = [];

    public List<UnitDescriptor> DeployedUnits(IReadOnlyList<string>? modListIds) =>
    [
        .. Units.Where(unit => modListIds == null || modListIds.Contains(unit.PackageId, StringComparer.OrdinalIgnoreCase))
    ];

    public TestModDescriptor TestMod { get; set; } = new();

    public bool EngineSelfTests { get; set; }

    public List<string> BuildOrder { get; set; } = [];

    public bool AutoBuild { get; set; } = true;

    public List<PreflightCommandDescriptor> ColdPreflightCommands { get; set; } = [];
    public List<PreflightCommandDescriptor> MountPreflightCommands { get; set; } = [];

    public Dictionary<string, string> FreshnessAssemblies { get; set; } = [];

    public string DefaultModList { get; set; } = "";

    public SamplesDescriptor Samples { get; set; } = new();

    public List<ExcludedCaseDescriptor> DefaultExcluded { get; set; } = [];

    public BreakerDescriptor? Breaker { get; set; }

    public WindowSizeDescriptor WindowSize { get; set; } = new();

    public bool Trace { get; set; } = true;
    public bool ForceMainMap { get; set; }

    public sealed class UnitDescriptor
    {
        public string Name { get; set; } = "";
        public string SourceRel { get; set; } = "";
        public string PackageId { get; set; } = "";
        public bool Optional { get; set; }
    }

    public sealed class TestModDescriptor
    {
        public string IdPrefix { get; set; } = "";
        public string AssemblyName { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Project { get; set; } = "";
        public string OutputDll { get; set; } = "";
        public string SupportedVersion { get; set; } = "";
        public string? ContentDir { get; set; }
    }

    public sealed class ExcludedCaseDescriptor
    {
        [DescriptorKey("用例 Id", Required = true)]
        public string Id { get; set; } = "";
        [DescriptorKey("为什么不执行。只留在这份文件里，不进报告", Required = true)]
        public string Reason { get; set; } = "";
    }

    public sealed class BreakerDescriptor
    {
        [DescriptorKey("引擎主循环连续多少次读数都见到新增错误级日志即熔断，≥ 1。不写 = 20")]
        public int? Pumps { get; set; }
        [DescriptorKey("单个用例窗口内累计多少条错误级日志即熔断，≥ 1。不写 = 40")]
        public int? Errors { get; set; }
    }

    public sealed class SamplesDescriptor
    {
        [DescriptorKey("\"jsonl\"：每条用例一份 <用例Id>.jsonl，一行一条样本；\"sqlite\"：全部用例进一份 samples.db。改值之前先用 rimrunner samples convert 转换现有的库。不写 = jsonl")]
        public string Store { get; set; } = SampleStore.JsonlName;
    }

    public sealed class WindowSizeDescriptor
    {
        [DescriptorKey("宽，像素，≥ 1。不写 = 1920")]
        public int Width { get; set; } = 1920;
        [DescriptorKey("高，像素，≥ 1。不写 = 1080")]
        public int Height { get; set; } = 1080;
    }

    public sealed class PreflightCommandDescriptor
    {
        [DescriptorKey("失败时用于指称的名字，同一面内不重复", Required = true)]
        public string Name { get; set; } = "";
        [DescriptorKey("可执行文件：路径或 PATH 上的裸名", Required = true)]
        public string Exe { get; set; } = "";
        [DescriptorKey("参数数组，一项一个参数")]
        public List<string> Args { get; set; } = [];
    }

    public const string FileName = ManifestDocument.FileName;

    public static string? FindProductRoot(string startDir)
    {
        string? dir = CommandContext.FullPath(startDir);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, FileName)))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        return null;
    }

    internal static string WorkspaceDirOf(ManifestDocument manifest) => Normalize(manifest.Dir);

    internal static string WorkspaceFile(string productRoot, string workspaceDir, string fileName) =>
        CommandContext.FullPath(Path.Combine(productRoot, workspaceDir.Replace('/', Path.DirectorySeparatorChar), fileName));

    internal static Func<string, string?>? GameExeOverride;

    public static (string ProductRoot, OrchestratorDescriptor Descriptor) Load(string productRoot)
    {
        string path = Path.Combine(productRoot, FileName);
        ManifestDocument manifest = DescriptorFile.Read<ManifestDocument>(path, KindLabel(typeof(ManifestDocument)), null)
            ?? throw new FileNotFoundException($"找不到 {FileName}：{path}", path);

        string dir = WorkspaceDirOf(manifest);
        if (dir.Length == 0 || Path.IsPathRooted(manifest.Dir) || dir.Contains(".."))
        {
            Fail(path, typeof(ManifestDocument), [$"dir 须相对产品根、不含 .. 且不为空（收到 {manifest.Dir}）"]);
        }

        string runtimePath = WorkspaceFile(productRoot, dir, RuntimeDocument.FileName);
        string preflightPath = WorkspaceFile(productRoot, dir, PreflightDocument.FileName);
        string excludedPath = WorkspaceFile(productRoot, dir, ExcludedDocument.FileName);
        RuntimeDocument runtime = DescriptorFile.Read<RuntimeDocument>(runtimePath, KindLabel(typeof(RuntimeDocument)), dir) ?? new();
        PreflightDocument preflight = DescriptorFile.Read<PreflightDocument>(preflightPath, KindLabel(typeof(PreflightDocument)), dir) ?? new();
        ExcludedDocument excluded = DescriptorFile.Read<ExcludedDocument>(excludedPath, KindLabel(typeof(ExcludedDocument)), dir) ?? new();
        RimRunnerConfig.MachineConfig machine = RimRunnerConfig.Machine();

        var descriptor = new OrchestratorDescriptor();
        descriptor.ComposeManifest(productRoot, path, manifest, dir);
        descriptor.ComposeRuntime(runtimePath, runtime);
        descriptor.ComposePreflight(preflightPath, preflight);
        descriptor.ComposeExcluded(excludedPath, excluded);

        string? gameExe = GameExeOverride?.Invoke(productRoot) ?? machine.ResolvedGameExe;
        if (string.IsNullOrWhiteSpace(gameExe))
        {
            throw new DescriptorException(
                $"找不到 RimWorld.exe：机器级 {RimRunnerConfig.MachineFileName} 没有 {RimRunnerConfig.GameExeKey}，{SteamGame.Source}里也没有 RimWorld；文件 {RimRunnerConfig.MachinePath}");
        }

        descriptor.GameExe = gameExe;
        return (productRoot, descriptor);
    }

    private void ComposeManifest(string productRoot, string path, ManifestDocument manifest, string dir)
    {
        var problems = new List<string>();
        Product = manifest.Product;
        if (string.IsNullOrWhiteSpace(Product)) problems.Add("product 缺失");

        SaveNamePrefix = manifest.SaveNamePrefix;
        if (string.IsNullOrWhiteSpace(SaveNamePrefix)) problems.Add("saveNamePrefix 缺失");
        else if (Path.IsPathRooted(SaveNamePrefix) || SaveNamePrefix.Contains(".."))
            problems.Add($"saveNamePrefix 须为相对名且不含 ..（收到 {SaveNamePrefix}）");

        WorkspaceDir = dir;
        DataDir = dir + "/data";
        ModListsDir = dir + "/modlists";
        DefaultModList = manifest.DefaultModList;
        AutoBuild = manifest.AutoBuild;
        EngineSelfTests = manifest.EngineSelfTests;

        ComposeUnits(productRoot, manifest, problems);
        ComposeBuilds(manifest, problems);

        if (problems.Count > 0) Fail(path, typeof(ManifestDocument), problems);
    }

    private void ComposeUnits(string productRoot, ManifestDocument manifest, List<string> problems)
    {
        if (manifest.Units.Count == 0) problems.Add("units 为空");

        var names = new HashSet<string>(StringComparer.Ordinal);
        var packageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < manifest.Units.Count; i++)
        {
            ManifestDocument.UnitEntry entry = manifest.Units[i] ?? new();
            string source = Normalize(entry.Source ?? "");
            string label = source.Length > 0 ? source : i.ToString();
            if (source.Length == 0)
            {
                problems.Add($"units[{i}].source 缺失");
                continue;
            }

            if (Path.IsPathRooted(entry.Source!) || source.Contains(".."))
            {
                problems.Add($"units[{label}].source 须相对产品根且不含 ..（收到 {entry.Source}）");
                continue;
            }

            string name = string.IsNullOrWhiteSpace(entry.Name) ? source[(source.LastIndexOf('/') + 1)..] : entry.Name;
            if (!names.Add(name)) problems.Add($"units[{label}].name 重复：{name}");

            string? packageId = UnitPackageId(productRoot, source, out string? fault);
            if (packageId == null)
            {
                problems.Add($"units[{label}].source 的 About.xml 无法读取 packageId：{fault}");
                continue;
            }

            if (!packageIds.Add(packageId)) problems.Add($"units[{label}].source 的 packageId 与另一单元相同：{packageId}");

            Units.Add(new UnitDescriptor { Name = name, SourceRel = source, PackageId = packageId, Optional = entry.Optional });
        }

        var required = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (UnitDescriptor unit in Units.Where(unit => !unit.Optional))
        {
            if (seen.Add(unit.PackageId)) required.Add(unit.PackageId);
        }

        foreach (string raw in manifest.RequiredMods ?? [])
        {
            string id = raw?.Trim() ?? "";
            if (id.Length == 0)
            {
                problems.Add("requiredMods 含空项");
                continue;
            }

            if (Units.FirstOrDefault(unit => string.Equals(unit.PackageId, id, StringComparison.OrdinalIgnoreCase)) is { } unit)
            {
                problems.Add(unit.Optional
                    ? $"requiredMods 含可选单元 {unit.Name} 的 {id}：单元是否必需只由它的 optional 决定"
                    : $"requiredMods 含单元 {unit.Name} 的 {id}：非可选单元已自动算必需");
                continue;
            }

            if (!seen.Add(id))
            {
                problems.Add($"requiredMods 重复：{id}");
                continue;
            }

            required.Add(id);
        }

        if (required.Count == 0 && Units.Count > 0)
            problems.Add("必需集合为空：units 全是 optional，requiredMods 也没写");
        RequiredPackageIds = required;
    }

    internal static string? UnitPackageId(string productRoot, string sourceRel, out string? fault)
    {
        string about = CommandContext.FullPath(Path.Combine(productRoot,
            sourceRel.Replace('/', Path.DirectorySeparatorChar), "About", "About.xml"));
        XDocument document;
        try
        {
            document = XDocument.Load(about);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException)
        {
            fault = $"{exception.Message}（{about}）";
            return null;
        }

        List<XElement> ids = document.Root is { Name.LocalName: "ModMetaData" } root
            ? [.. root.Elements("packageId")]
            : [];
        if (ids.Count != 1 || string.IsNullOrWhiteSpace(ids[0].Value))
        {
            fault = $"须恰有一个非空的 /ModMetaData/packageId，读到 {ids.Count} 个（{about}）";
            return null;
        }

        fault = null;
        return ids[0].Value.Trim();
    }

    private void ComposeBuilds(ManifestDocument manifest, List<string> problems)
    {
        ManifestDocument.TestModEntry test = manifest.TestMod ?? new();
        foreach ((string value, string field) in new[] { (test.IdPrefix, "testMod.idPrefix"), (test.Csproj, "testMod.csproj"), (test.Dll, "testMod.dll") })
        {
            if (string.IsNullOrWhiteSpace(value)) problems.Add($"{field} 缺失");
            else if (field != "testMod.idPrefix" && (Path.IsPathRooted(value) || value.Contains("..")))
                problems.Add($"{field} 须相对产品根且不含 ..（收到 {value}）");
        }

        if (!string.IsNullOrWhiteSpace(test.Dll) && !test.Dll.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            problems.Add($"testMod.dll 须指向 .dll（收到 {test.Dll}）");

        if (test.ContentDir != null && string.IsNullOrWhiteSpace(test.ContentDir))
            problems.Add("testMod.contentDir 为空白");
        else if (!string.IsNullOrWhiteSpace(test.ContentDir) && (Path.IsPathRooted(test.ContentDir) || test.ContentDir.Contains("..")))
            problems.Add($"testMod.contentDir 须相对产品根且不含 ..（收到 {test.ContentDir}）");

        string testDll = Normalize(test.Dll ?? "");
        string testCsproj = Normalize(test.Csproj ?? "");
        string testAssembly = Path.GetFileNameWithoutExtension(testDll);
        TestMod = new TestModDescriptor
        {
            IdPrefix = test.IdPrefix ?? "",
            AssemblyName = Path.GetFileName(testDll),
            DisplayName = string.IsNullOrWhiteSpace(test.DisplayName) ? testAssembly : test.DisplayName,
            Project = testCsproj,
            OutputDll = testDll,
            SupportedVersion = test.SupportedVersion ?? "",
            ContentDir = test.ContentDir,
        };

        var assemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { EngineArtifact.AssemblyName, EngineArtifact.SelfTestAssemblyName };
        if (testAssembly.Length > 0 && !assemblies.Add(testAssembly))
            problems.Add($"testMod.dll 的程序集名 {testAssembly} 与引擎重名");

        for (int i = 0; i < manifest.Builds.Count; i++)
        {
            ManifestDocument.BuildEntry entry = manifest.Builds[i] ?? new();
            string dll = Normalize(entry.Dll ?? "");
            string csproj = Normalize(entry.Csproj ?? "");
            string label = dll.Length > 0 ? dll : i.ToString();
            if (dll.Length == 0)
            {
                problems.Add($"builds[{i}].dll 缺失");
                continue;
            }

            if (Path.IsPathRooted(entry.Dll!) || dll.Contains("..") || !dll.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"builds[{label}].dll 须相对产品根、不含 .. 且指向 .dll（收到 {entry.Dll}）");
                continue;
            }

            if (csproj.Length > 0 && (Path.IsPathRooted(entry.Csproj!) || csproj.Contains("..")))
            {
                problems.Add($"builds[{label}].csproj 须相对产品根且不含 ..（收到 {entry.Csproj}）");
                continue;
            }

            if (string.Equals(dll, testDll, StringComparison.OrdinalIgnoreCase)
                || (csproj.Length > 0 && string.Equals(csproj, testCsproj, StringComparison.OrdinalIgnoreCase)))
            {
                problems.Add($"builds[{label}] 是测试工程：测试工程只写在 testMod，由它接在构建序最后");
                continue;
            }

            string assembly = Path.GetFileNameWithoutExtension(dll);
            if (!assemblies.Add(assembly))
            {
                problems.Add($"builds[{label}].dll 的程序集名 {assembly} 与另一项或引擎重名");
                continue;
            }

            if (csproj.Length > 0) BuildOrder.Add(csproj);
            FreshnessAssemblies[assembly] = dll;
        }

        if (testCsproj.Length > 0) BuildOrder.Add(testCsproj);
        if (testAssembly.Length > 0) FreshnessAssemblies[testAssembly] = testDll;
    }

    private void ComposeRuntime(string path, RuntimeDocument runtime)
    {
        var problems = new List<string>();
        RuntimeDocument.ConfigSection config = runtime.Config ?? new();
        PrefsBase = config.PrefsBase ?? "";
        ModSettingsBase = config.ModSettingsBase ?? "";
        Prefs = config.Prefs ?? [];
        if (!string.IsNullOrWhiteSpace(PrefsBase)
            && PrefsBase is not (InstanceConfig.PrefsBaseDefault or InstanceConfig.PrefsBasePlayer))
            problems.Add($"config.prefsBase 须是 {InstanceConfig.PrefsBaseDefault} 或 {InstanceConfig.PrefsBasePlayer}（收到 {PrefsBase}）");

        if (!string.IsNullOrWhiteSpace(ModSettingsBase)
            && ModSettingsBase is not (InstanceConfig.ModSettingsBaseNone or InstanceConfig.ModSettingsBasePlayer))
            problems.Add($"config.modSettingsBase 须是 {InstanceConfig.ModSettingsBaseNone} 或 {InstanceConfig.ModSettingsBasePlayer}（收到 {ModSettingsBase}）");

        foreach ((string key, JsonElement value) in Prefs)
        {
            if (InstanceConfig.PrefsScalar(value) == null) problems.Add($"config.prefs.{key} 的值须是字符串、布尔或数");
        }

        problems.AddRange(InstanceConfig.PinnedCollisions(Prefs.Keys).Select(problem => "config.prefs." + problem));

        Breaker = runtime.Breaker;
        if (Breaker != null)
        {
            if (Breaker.Pumps is int pumps && pumps < 1) problems.Add("breaker.pumps 须 ≥ 1");
            if (Breaker.Errors is int errors && errors < 1) problems.Add("breaker.errors 须 ≥ 1");
        }

        Samples = runtime.Samples ?? new();
        if (SampleStore.ParseKind(Samples.Store) == null)
            problems.Add($"samples.store 须是 \"{SampleStore.JsonlName}\" 或 \"{SampleStore.SqliteName}\"（收到 \"{Samples.Store}\"）");

        WindowSize = runtime.WindowSize ?? new();
        if (WindowSize.Width < 1) problems.Add($"windowSize.width 须 ≥ 1（收到 {WindowSize.Width}）");
        if (WindowSize.Height < 1) problems.Add($"windowSize.height 须 ≥ 1（收到 {WindowSize.Height}）");

        if (WindowSize.Width >= 1 && WindowSize.Height >= 1 && WindowSize.Width < 1024 && WindowSize.Height < 768)
            problems.Add($"windowSize 宽 < 1024 与高 < 768 不得同时成立（收到 {WindowSize.Width}×{WindowSize.Height}）");

        if (WindowSize.Width % 2 != 0 || WindowSize.Height % 2 != 0)
            problems.Add($"windowSize 宽高须为偶数（收到 {WindowSize.Width}×{WindowSize.Height}）：镜头不写输出尺寸时取它，Mp4 要偶数");

        Trace = (runtime.Diagnostics ?? new()).Trace;
        ForceMainMap = (runtime.Experiments ?? new()).ForceMainMap;

        if (problems.Count > 0) Fail(path, typeof(RuntimeDocument), problems);
    }

    private void ComposePreflight(string path, PreflightDocument preflight)
    {
        var problems = new List<string>();
        ColdPreflightCommands = preflight.Cold ?? [];
        MountPreflightCommands = preflight.Mount ?? [];
        ValidatePreflightList(ColdPreflightCommands, "cold", problems);
        ValidatePreflightList(MountPreflightCommands, "mount", problems);
        if (problems.Count > 0) Fail(path, typeof(PreflightDocument), problems);
    }

    private void ComposeExcluded(string path, ExcludedDocument excluded)
    {
        var problems = new List<string>();
        DefaultExcluded = excluded.Cases ?? [];
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < DefaultExcluded.Count; i++)
        {
            ExcludedCaseDescriptor item = DefaultExcluded[i] ?? new ExcludedCaseDescriptor();
            string label = i.ToString();
            if (string.IsNullOrWhiteSpace(item.Id))
            {
                problems.Add($"cases[{i}].id 缺失");
            }
            else
            {
                label = item.Id;
                if (!ids.Add(item.Id)) problems.Add($"cases[].id 重复：{item.Id}");
            }

            if (string.IsNullOrWhiteSpace(item.Reason)) problems.Add($"cases[{label}].reason 缺失");
        }

        if (problems.Count > 0) Fail(path, typeof(ExcludedDocument), problems);
    }

    private static void ValidatePreflightList(
        List<PreflightCommandDescriptor> commands, string field, List<string> problems)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < commands.Count; i++)
        {
            PreflightCommandDescriptor item = commands[i] ?? new PreflightCommandDescriptor();
            string label = i.ToString();
            if (string.IsNullOrWhiteSpace(item.Name))
            {
                problems.Add($"{field}[{i}].name 缺失");
            }
            else
            {
                label = item.Name;
                if (!names.Add(item.Name))
                {
                    problems.Add($"{field}[].name 重复：{item.Name}");
                }
            }

            if (string.IsNullOrWhiteSpace(item.Exe))
            {
                problems.Add($"{field}[{label}].exe 缺失");
            }
        }
    }

    private static void Fail(string path, Type documentType, IEnumerable<string> problems) =>
        throw new DescriptorException(
            $"{KindLabel(documentType)}字段不合法：{path}\n  " + string.Join("\n  ", DescriptorDocs.Annotate(documentType, problems)));

    internal static string KindLabel(Type documentType) => documentType.Name switch
    {
        nameof(RuntimeDocument) => "运行参数",
        nameof(PreflightDocument) => "前置检查",
        nameof(ExcludedDocument) => "排除名单",
        _ => "编排描述符",
    };

    public string ResolveSupportedVersion() =>
        !string.IsNullOrWhiteSpace(TestMod.SupportedVersion)
            ? TestMod.SupportedVersion
            : VramModel.GameVersion(GameExe)
              ?? throw new StoreException(
                  $"无法读取游戏版本（{GameExe} 同目录的 Assembly-CSharp.dll 或 Version.txt）：在 {FileName} 的 [testMod] 写 supportedVersion");

    internal OrchestratorDescriptor WithGameExe(string gameExe)
    {
        var copy = (OrchestratorDescriptor)MemberwiseClone();
        copy.GameExe = gameExe;
        return copy;
    }

    public string ModsFolder => Path.Combine(
        Path.GetDirectoryName(CommandContext.FullPath(GameExe)) ?? "", "Mods");

    public string ResolveSavesDir(string productRoot) => CommandContext.FullPath(Path.Combine(productRoot,
        (Normalize(DataDir) + "/saves").Replace('/', Path.DirectorySeparatorChar)));

    public const string DefaultNewGameFileName = "newgame.toml";

    public string ResolveNewGameFile(string productRoot) => CommandContext.FullPath(Path.Combine(productRoot,
        (Normalize(DataDir) + "/" + DefaultNewGameFileName).Replace('/', Path.DirectorySeparatorChar)));

    internal string OutputDirRelative
    {
        get
        {
            string data = Normalize(DataDir);
            int slash = data.LastIndexOf('/');
            return slash < 0 ? "output" : data[..slash] + "/output";
        }
    }

    public string ResolveWorkspaceFile(string productRoot, string fileName)
    {
        string data = Normalize(DataDir);
        int slash = data.LastIndexOf('/');
        return WorkspaceFile(productRoot, slash < 0 ? "" : data[..slash], fileName);
    }

    private static string Normalize(string relative) => relative.Replace('\\', '/').Trim().Trim('/');

    public string PrefsBaseOrDefault =>
        string.IsNullOrWhiteSpace(PrefsBase) ? InstanceConfig.PrefsBaseDefault : PrefsBase;

    public string ModSettingsBaseOrDefault =>
        string.IsNullOrWhiteSpace(ModSettingsBase) ? InstanceConfig.ModSettingsBaseNone : ModSettingsBase;

    public IReadOnlyDictionary<string, string> PrefsOverrides() =>
        Prefs.ToDictionary(pair => pair.Key, pair => InstanceConfig.PrefsScalar(pair.Value)!, StringComparer.Ordinal);

    public string ResolveConfigDir(string productRoot) => CommandContext.FullPath(Path.Combine(productRoot,
        (Normalize(DataDir) + "/config").Replace('/', Path.DirectorySeparatorChar)));

    public string ResolveOutputDir(string productRoot) => CommandContext.FullPath(Path.Combine(productRoot,
        OutputDirRelative.Replace('/', Path.DirectorySeparatorChar)));

    public string ResolveReportsDir(string productRoot) =>
        Path.Combine(ResolveOutputDir(productRoot), RimRunner.RimRunnerDataLayout.ReportsDirName);

    public string ResolveArtifactsDir(string productRoot) =>
        Path.Combine(ResolveOutputDir(productRoot), RimRunner.RimRunnerDataLayout.ArtifactsDirName);

    public string? ResolveTestModContentDir(string productRoot) =>
        string.IsNullOrWhiteSpace(TestMod.ContentDir) ? null
            : CommandContext.FullPath(Path.Combine(productRoot,
                TestMod.ContentDir!.Replace('/', Path.DirectorySeparatorChar)));

    public string? ResolveModList(string? fromCommandLine) =>
        fromCommandLine ?? (string.IsNullOrWhiteSpace(DefaultModList) ? null : DefaultModList);

    public string BaselineSaveNameFor(string? modList) =>
        string.IsNullOrWhiteSpace(modList) ? SaveNamePrefix : $"{SaveNamePrefix}-{modList}";
}

public sealed class DescriptorException(string message) : Exception(message)
{
}
