using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RimRunnerCli;

internal static class CasesCommand
{
    private const int Schema = 1;
    internal const string ProductSource = "product";
    internal const string ChecksSource = "checks";
    internal const string SelfTestsSource = "selftests";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal sealed record CaseItem(
        string Source, IReadOnlyList<string> Ids, bool Parameterized, string Class, string File, int Line,
        string Kind, bool RealFrame, bool EnvCheck, string? Summary, string? Remarks)
    {
        internal bool Excluded { get; init; }
        internal IReadOnlyList<string> Groups { get; init; } = [];
    }

    internal sealed record ProductScan(List<CaseItem> Cases, List<string> Problems, List<string> Warnings);

    internal static int Execute(
        IReadOnlyList<string> words, string searchDir,
        bool json, bool full)
    {
        string verb = words.Count > 0 ? words[0] : "";
        string[] terms = [.. words.Skip(1).Select(word => word.ToLowerInvariant())];

        string productRoot = OrchestratorDescriptor.FindProductRoot(searchDir) ?? throw new StoreException(
            $"在 {searchDir} 及其上级目录未找到 {OrchestratorDescriptor.FileName}："
            + $"cases {verb} 查产品的用例");
        OrchestratorDescriptor descriptor = OrchestratorDescriptor.Load(productRoot).Descriptor;
        EnginePackage package = EnginePackage.Locate().AssertComplete();
        if (verb == GroupsVerb)
        {
            ArtifactGate.Output artifacts = new ArtifactGate(descriptor, productRoot)
                .Check(json ? line => CliLog.Info(line) : line => Console.WriteLine(line), writeSnapshot: false);
            CaseManifest roster = Roster.Ensure(descriptor, productRoot, artifacts, write: false).Cases;
            ProductScan product = ScanWithTool(package, TestDir(productRoot, descriptor));
            return RunGroups(json, productRoot, descriptor, roster, product, Console.Out);
        }

        return Run(terms, json, full, productRoot, descriptor, package, ScanWithTool, Console.Out);
    }

    private static string TestDir(string productRoot, OrchestratorDescriptor descriptor)
    {
        string testProject = CommandContext.FullPath(Path.Combine(productRoot, descriptor.TestMod.Project));
        string testDir = Path.GetDirectoryName(testProject)!;
        return Directory.Exists(testDir)
            ? testDir
            : throw new StoreException($"测试工程所在目录不存在：{testDir}（rimrunner.toml 的 testMod.csproj）");
    }

    internal const string GroupsVerb = "groups";

    internal static int RunGroups(
        bool json, string productRoot, OrchestratorDescriptor descriptor,
        CaseManifest roster, ProductScan product, TextWriter output)
    {
        string testDir = Path.GetDirectoryName(CommandContext.FullPath(Path.Combine(productRoot, descriptor.TestMod.Project)))!;
        var byId = new Dictionary<string, CaseItem>(StringComparer.Ordinal);
        foreach (CaseItem item in product.Cases)
        {
            foreach (string id in item.Ids)
                byId.TryAdd(id, item with { File = RelativeToRoot(productRoot, testDir, item.File) });
        }

        List<CaseManifest.CaseEntry> productCases = [.. roster.Cases.Where(entry => !entry.EngineOwned)];
        List<(string Group, List<RosterClass> Classes)> groups =
        [
            .. roster.KnownGroups().Select(group =>
                (group, Classes(productCases.Where(entry => entry.Groups.Contains(group, StringComparer.Ordinal)), byId)))
        ];

        if (json)
        {
            CliLog.Machine(() => output.WriteLine(GroupsJson(groups)));
            return 0;
        }

        foreach ((string group, List<RosterClass> classes) in groups)
        {
            string[] ids = [.. classes.SelectMany(item => item.Ids)];
            output.WriteLine($"{group}  {ids.Length} 条：{string.Join("、", ids)}");
            if (classes.Count == 1)
                output.WriteLine($"[WARN] 组 {group} 只有用例类 {classes[0].Name} 声明该组（{classes[0].Where}）");
        }

        int classCount = Classes(productCases.Where(entry => !entry.EnvCheck), byId).Count;
        output.WriteLine($"产品用例类 {classCount} 个、组 {groups.Count} 个");
        return 0;
    }

    private sealed record RosterClass(string Type, IReadOnlyList<string> Ids, CaseItem? Source)
    {
        internal string Name => Source?.Class ?? Type;
        internal string Where => Source != null ? $"{Source.File}:{Source.Line}" : "源码不在测试工程目录下，无位置";
    }

    private static List<RosterClass> Classes(IEnumerable<CaseManifest.CaseEntry> entries, Dictionary<string, CaseItem> byId) =>
    [
        .. entries.GroupBy(entry => entry.Type, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                string[] ids = [.. group.Select(entry => entry.Id)];
                return new RosterClass(group.Key, ids, ids.Select(id => byId.GetValueOrDefault(id)).FirstOrDefault(item => item != null));
            })
    ];

    private static string GroupsJson(List<(string Group, List<RosterClass> Classes)> groups)
    {
        static JsonObject Class(RosterClass item) => new()
        {
            ["type"] = item.Type,
            ["class"] = item.Name,
            ["file"] = item.Source?.File,
            ["line"] = item.Source?.Line,
            ["ids"] = new JsonArray([.. item.Ids.Select(id => (JsonNode?)JsonValue.Create(id))]),
        };

        var root = new JsonObject
        {
            ["schema"] = Schema,
            ["groups"] = new JsonArray([
                .. groups.Select(group => (JsonNode?)new JsonObject
                {
                    ["group"] = group.Group,
                    ["classes"] = new JsonArray([.. group.Classes.Select(item => (JsonNode?)Class(item))]),
                })
            ]),
        };
        return root.ToJsonString(JsonOptions);
    }

    internal static int Run(
        string[] terms, bool json, bool full, string productRoot, OrchestratorDescriptor descriptor,
        EnginePackage package, Func<EnginePackage, string, ProductScan> scanProduct, TextWriter output)
    {
        string testProject = CommandContext.FullPath(Path.Combine(productRoot, descriptor.TestMod.Project));
        string testDir = Path.GetDirectoryName(testProject)!;
        if (!Directory.Exists(testDir))
            throw new StoreException($"测试工程所在目录不存在：{testDir}（rimrunner.toml 的 testMod.csproj）");

        ProductScan product = scanProduct(package, testDir);
        var all = new List<CaseItem>(product.Cases.Select(item => item with { File = RelativeToRoot(productRoot, testDir, item.File) }));
        string checksDir = Path.Combine(package.EngineDir, "src", "Checks");
        all.AddRange(Load(package.ChecksPath, ChecksSource).Select(item => item with { File = CommandContext.FullPath(Path.Combine(checksDir, item.File)) }));
        if (descriptor.EngineSelfTests)
        {
            if (!package.ReadManifest().SelfTests)
                throw new StoreException($"RimRunner 安装目录不带引擎自测名册（{EnginePackage.ManifestFileName} 的 includes.selfTests 为 false）");
            all.AddRange(Load(package.SelfTestsPath, SelfTestsSource));
        }

        var excluded = new HashSet<string>(descriptor.DefaultExcluded.Select(item => item.Id), StringComparer.Ordinal);
        all = [.. all.Select(item => item.Ids.Any(excluded.Contains) ? item with { Excluded = true } : item)];

        bool Matches(CaseItem item) => terms.All(term => SearchText(item).Contains(term, StringComparison.Ordinal));
        List<CaseItem> hits = [.. all.Where(Matches)];

        if (json)
        {
            CliLog.Machine(() => output.WriteLine(ToJson(hits, product)));
            return hits.Count > 0 ? 0 : 1;
        }

        foreach (CaseItem item in hits) Print(item, terms, full, output);
        foreach (string problem in product.Problems) output.WriteLine($"[WARN] 产品用例名册：{problem}");

        if (hits.Count > 0) return 0;
        output.WriteLine($"无匹配「{string.Join(" ", terms)}」（共 {all.Count} 条用例：{Counts(all)}；多个关键词须同时匹配）");
        return 1;
    }

    private static string Counts(List<CaseItem> all)
    {
        string Count(string source) => all.Count(item => item.Source == source).ToString();
        string text = $"产品 {Count(ProductSource)}、随包 {Count(ChecksSource)}";
        return all.Any(item => item.Source == SelfTestsSource) ? text + $"、引擎自测 {Count(SelfTestsSource)}" : text;
    }

    private static string SearchText(CaseItem item) =>
        string.Join(" ", [.. item.Ids, item.Class, item.Summary ?? "", item.Remarks ?? ""]).ToLowerInvariant();

    private static string KindLabel(string kind) => kind switch
    {
        "regression" => "回归用例",
        "readout" => "读数用例",
        _ => kind,
    };

    private static void Print(CaseItem item, string[] terms, bool full, TextWriter output)
    {
        int shown = full ? item.Ids.Count : 6;
        string ids = string.Join("、", item.Ids
            .OrderByDescending(id => terms.Count(term => id.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .Take(shown));
        if (item.Ids.Count > shown)
            ids += "（" + FindingPresentation.Omitted(item.Ids.Count - shown, "个 Id", FindingPresentation.WithFlag("--full")) + "）";
        if (item.Parameterized) ids += "（参数化）";
        if (item.Ids.Count == 0) ids = "（无法解析 Id）";

        string flags = KindLabel(item.Kind)
            + (item.RealFrame ? " · 真实帧用例" : "")
            + (item.EnvCheck ? " · 环境体检" : "")
            + (item.Groups.Count > 0 ? " · 组 " + string.Join("、", item.Groups) : "")
            + (item.Excluded ? " · 排除名单" : "");
        output.WriteLine($"{ids}  {flags}  —— {item.Summary ?? "（无摘要）"}");
        output.WriteLine($"  {item.Class}  {Location(item)}");

        if (item.Remarks == null) return;
        if (full || terms.Any(term => item.Remarks.ToLowerInvariant().Contains(term, StringComparison.Ordinal)))
            output.WriteLine($"  范围：{item.Remarks}");
        else
            output.WriteLine("  " + FindingPresentation.Omitted(1, "条范围", FindingPresentation.WithFlag("--full")));
    }

    private static string Location(CaseItem item) => item.Source switch
    {
        ProductSource => $"{item.File}:{item.Line}",
        ChecksSource => $"随包 {item.File}:{item.Line}",
        _ => $"引擎自测 {item.File}:{item.Line}",
    };

    private static string RelativeToRoot(string productRoot, string testDir, string file) =>
        Path.GetRelativePath(productRoot, Path.Combine(testDir, file)).Replace('\\', '/');

    private static string ToJson(List<CaseItem> hits, ProductScan product)
    {
        var cases = new JsonArray();
        foreach (CaseItem item in hits)
        {
            var node = new JsonObject
            {
                ["source"] = item.Source,
                ["ids"] = new JsonArray([.. item.Ids.Select(id => (JsonNode?)JsonValue.Create(id))]),
                ["parameterized"] = item.Parameterized,
                ["class"] = item.Class,
                ["file"] = item.File,
                ["line"] = item.Line,
                ["kind"] = item.Kind,
                ["realFrame"] = item.RealFrame,
                ["envCheck"] = item.EnvCheck,
                ["excluded"] = item.Excluded,
                ["groups"] = new JsonArray([.. item.Groups.Select(group => (JsonNode?)JsonValue.Create(group))]),
                ["summary"] = item.Summary,
            };
            if (item.Remarks != null) node["remarks"] = item.Remarks;
            cases.Add(node);
        }

        var root = new JsonObject
        {
            ["schema"] = Schema,
            ["cases"] = cases,
            ["problems"] = new JsonArray([.. product.Problems.Select(p => (JsonNode?)JsonValue.Create(p))]),
            ["warnings"] = new JsonArray([.. product.Warnings.Select(p => (JsonNode?)JsonValue.Create(p))]),
        };
        return root.ToJsonString(JsonOptions);
    }

    internal static List<string> CheckIds(EnginePackage package) =>
        [.. Load(package.ChecksPath, ChecksSource).SelectMany(item => item.Ids)];

    private static List<CaseItem> Load(string path, string source)
    {
        JsonElement root;
        try
        {
            using JsonDocument document = StrictJson.ParseObject(File.ReadAllText(path));
            root = document.RootElement.Clone();
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            throw new StoreException($"无法读取 {path}：{exception.Message}");
        }

        return Items(root, path, source);
    }

    private static List<CaseItem> Items(JsonElement root, string where, string source)
    {
        if (!root.TryGetProperty("schema", out JsonElement schema) || schema.ValueKind != JsonValueKind.Number
            || schema.GetInt32() != Schema)
        {
            throw new StoreException($"{where} 的 schema 不是 {Schema}");
        }

        var items = new List<CaseItem>();
        foreach (JsonElement raw in root.GetProperty("cases").EnumerateArray())
        {
            string? Text(string key) =>
                raw.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            bool Flag(string key) => raw.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.True;
            List<string> groups = raw.TryGetProperty("groups", out JsonElement groupArray) && groupArray.ValueKind == JsonValueKind.Array
                ? [.. groupArray.EnumerateArray().Select(group => group.GetString() ?? "")]
                : [];
            items.Add(new CaseItem(
                source,
                [.. raw.GetProperty("ids").EnumerateArray().Select(id => id.GetString() ?? "")],
                Flag("parameterized"), Text("class") ?? "", Text("file") ?? "", raw.GetProperty("line").GetInt32(),
                Text("kind") ?? "", Flag("realFrame"), Flag("envCheck"), Text("summary"), Text("remarks"))
            {
                Groups = groups,
            });
        }

        return items;
    }

    private static readonly TimeSpan ScanLimit = TimeSpan.FromMinutes(5);

    internal static ProductScan ScanWithTool(EnginePackage package, string dir)
    {
        string exe = package.FixtureIndexPath;
        if (!File.Exists(exe)) throw new StoreException($"RimRunner 安装目录缺少名册工具：{exe}");

        var startInfo = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string arg in new[] { dir, "--profile", "product", "--cases-only" }) startInfo.ArgumentList.Add(arg);
        CommandContext.Prepare(startInfo, dir);

        using Process process = Process.Start(startInfo) ?? throw new StoreException($"名册工具无法启动：{exe}");
        Task<string> stdoutRead = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (ChildProcess.WaitOrStop(process, ScanLimit, $"名册工具扫描 {dir}") is string timedOut)
            throw new StoreException(timedOut);
        string stdout = stdoutRead.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new StoreException($"名册工具扫描 {dir} 失败（退出码 {process.ExitCode}）：{stderr.GetAwaiter().GetResult().Trim()}");

        try
        {
            using JsonDocument document = JsonDocument.Parse(stdout);
            JsonElement root = document.RootElement;
            List<string> Strings(string key) => [.. root.GetProperty(key).EnumerateArray().Select(item => item.GetString() ?? "")];
            return new ProductScan(Items(root, "名册工具输出", ProductSource), Strings("problems"), Strings("warnings"));
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new StoreException($"无法读取名册工具输出：{exception.Message}");
        }
    }
}
