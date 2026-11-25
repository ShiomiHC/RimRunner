using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace RimRunnerCli;

public sealed class ArtifactGate(OrchestratorDescriptor descriptor, string productRoot,
    Func<string, string?>? getEnvironmentVariable = null, ArtifactGate.Compiler? compile = null)
{
    internal const string BuildInputsFileName = "build-inputs.json";

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly OrchestratorDescriptor descriptor = descriptor;
    private readonly string productRoot = productRoot;
    private readonly Func<string, string?> getEnvironmentVariable = getEnvironmentVariable ?? CommandContext.GetEnvironmentVariable;
    private readonly Compiler compile = compile ?? ProductBuild.Compile;

    public delegate void Compiler(IReadOnlyList<string> absoluteCsprojs, string productRoot, string packageRoot,
        Action<string>? onLogLine);

    public enum Mode { ColdStart, RefuseStale }

    public sealed record Output(string TestDll, string EngineDll, SelfTestParts? SelfTests);

    public sealed record SelfTestParts(string Dll, string ContentDir);

    private enum StaleKind { SourceTimestamp, Upstream, InputListChanged }

    private sealed record Stale(
        string Dll, DateTime DllUtc, string Newest, DateTime NewestUtc, string Project,
        StaleKind Kind, IReadOnlyList<string> Removed, IReadOnlyList<string> Added);

    private sealed record Product(string Csproj, string Dll);

    private sealed record Missing(string Dll, string Project)
    {
        public string Line => $"产物不存在：{Dll}（工程 {Project}）";
    }

    private sealed record ProjectSnapshot(string Key, string Dll, List<string> Inputs);

    private sealed record Scan(List<string> Projects, List<Missing> Missing, List<Stale> Stale,
        List<ProjectSnapshot> Snapshots);

    private sealed class InputManifest
    {
        [JsonPropertyName("projects")]
        public Dictionary<string, ProjectInputRecord> Projects { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ProjectInputRecord
    {
        [JsonPropertyName("dll")]
        public DllIdentity Dll { get; set; } = new();

        [JsonPropertyName("inputs")]
        public List<string> Inputs { get; set; } = [];
    }

    private sealed class DllIdentity
    {
        [JsonPropertyName("utcTicks")]
        public long UtcTicks { get; set; }

        [JsonPropertyName("length")]
        public long Length { get; set; }

        [JsonPropertyName("mvid")]
        public string? Mvid { get; set; }

        public static DllIdentity Read(string dll)
        {
            var info = new FileInfo(dll);
            return new DllIdentity
            {
                UtcTicks = info.LastWriteTimeUtc.Ticks,
                Length = info.Length,
                Mvid = DllFreshnessGate.TryReadMvid(dll, out _),
            };
        }

        public bool Matches(DllIdentity? other) =>
            other != null
            && UtcTicks == other.UtcTicks
            && Length == other.Length
            && string.Equals(Mvid, other.Mvid, StringComparison.OrdinalIgnoreCase);
    }

    public Output Check(Action<string>? onLogLine = null, bool sources = true, Mode mode = Mode.ColdStart,
        bool writeSnapshot = true)
    {
        EnginePackage package = EnginePackage.Locate(getEnvironmentVariable).AssertComplete();
        bool build = sources && mode == Mode.ColdStart && descriptor.AutoBuild;

        string testDll = CommandContext.FullPath(Path.Combine(productRoot, descriptor.TestMod.OutputDll));
        InputManifest? priorManifest = sources ? TryLoadManifest(ManifestPath()) : null;
        Scan scan = ScanProjects(package, sources, priorManifest);
        HashSet<(string Dll, DateTime DllUtc)> beforeCompile = [.. scan.Stale.Select(entry => (entry.Dll, entry.DllUtc))];
        bool compiled = false;
        string? refFound = build && File.Exists(testDll) ? RefMismatch(testDll, package) : null;
        if (build && (scan.Missing.Count > 0 || scan.Stale.Count > 0 || refFound != null))
        {
            Emit(onLogLine, "[编译] ", scan.Missing, scan.Stale);
            if (refFound != null)
            {
                string statement = EngineArtifact.RimRunnerRefMetadata(testDll) == null ? "未记录引用的引擎 ref" : "引用的引擎 ref 与 RimRunner 安装目录里的不匹配";
                onLogLine?.Invoke($"[编译] {Path.GetFileName(testDll)} {statement}");
                CliLog.Info($"[产物闸] {Path.GetFileName(testDll)} {refFound}，RimRunner 安装目录里 ref 程序集的哈希是 {RefHash(package.RefDllPath)}");
            }
            onLogLine?.Invoke($"[编译] 按 builds、testMod 的顺序编译 {scan.Projects.Count} 个工程");
            compile(scan.Projects, productRoot, package.Root, onLogLine);
            compiled = true;
            scan = ScanProjects(package, sources, priorManifest);
        }

        if (scan.Missing.Count > 0)
        {
            Emit(onLogLine, "[拒绝/产物不存在] ", scan.Missing, []);
            throw new RefusalException((compiled ? "编译后" : "") + $"产物不存在：{scan.Missing.Count} 个工程，已拒绝执行");
        }

        if (scan.Stale.Count > 0)
        {
            if (mode == Mode.RefuseStale)
            {
                Emit(onLogLine, "[拒绝/产物陈旧] ", [], scan.Stale);
                throw new RefusalException($"产物陈旧：{scan.Stale.Count} 个工程，已拒绝执行");
            }

            if (compiled)
            {
                List<Stale> listChanged = [.. scan.Stale.Where(entry => entry.Kind == StaleKind.InputListChanged)];
                List<Stale> otherStale = [.. scan.Stale.Where(entry => entry.Kind != StaleKind.InputListChanged)];
                foreach (Stale entry in listChanged)
                {
                    string detail = InputChangeLogDetail(entry);
                    ObservationMarks.Mark(RimRunner.RimRunnerObservationKeys.StaleAfterCompile,
                        $"编译后输入清单仍与快照不匹配：{entry.Project}（{detail}）");
                    CliLog.Info($"[产物闸] 编译后产物未重写，按 MSBuild 为准更新输入清单：{entry.Project}（{detail}）");
                }

                if (otherStale.Count > 0)
                {
                    ObservationMarks.Mark(RimRunner.RimRunnerObservationKeys.StaleAfterCompile,
                        $"编译后仍陈旧：{string.Join("、", otherStale.Select(entry => entry.Project + " 早于 " + entry.Newest))}");
                    string line = $"[WARN] 编译后仍有 {otherStale.Count} 个工程的产物早于下列文件{(otherStale.All(entry => beforeCompile.Contains((entry.Dll, entry.DllUtc))) ? "，MSBuild 未重写" : "")}";
                    onLogLine?.Invoke(line);
                    Emit(onLogLine, "  ", [], otherStale);
                }
            }
            else
            {
                string line = $"[WARN] 产物陈旧，未编译（描述符 autoBuild=false）：{scan.Stale.Count} 个工程";
                onLogLine?.Invoke(line);
                Emit(onLogLine, "  ", [], scan.Stale);
            }
        }

        if (!File.Exists(testDll))
        {
            throw new StoreException($"编译产物不在预期输出位置：{testDll}");
        }

        AssertEngineRef(testDll, package, onLogLine);
        EngineArtifact.AssertReferencesEngine(testDll);

        if (sources && mode == Mode.ColdStart && writeSnapshot && (scan.Stale.Count == 0 || compiled))
            SaveManifest(scan.Snapshots);

        CliLog.Info($"[产物闸] 核过 {descriptor.BuildOrder.Count} 个工程"
            + (compiled ? "（本轮编译过）" : "")
            + $"；测试 DLL {testDll}；引擎 DLL {package.DllPath}");
        return new Output(testDll, package.DllPath, descriptor.EngineSelfTests ? SelfTests(package) : null);
    }

    private void Emit(Action<string>? onLogLine, string prefix, IEnumerable<Missing> missing, IEnumerable<Stale> stale)
    {
        if (onLogLine == null) return;
        List<Missing> absent = [.. missing];
        List<Stale> old = [.. stale];
        EngineReportFinding[] findings =
        [
            .. absent.Select(entry => new EngineReportFinding { Key = "产物不存在", Subject = entry.Project, Text = "产物不存在" }),
            .. old.Select(entry => entry.Kind switch
            {
                StaleKind.Upstream => new EngineReportFinding
                    { Key = "产物早于被引用工程", Subject = entry.Project, Text = "早于被引用的 " + RelativePath(entry.Newest) },
                StaleKind.InputListChanged => new EngineReportFinding
                    { Key = "编译输入清单已改变", Subject = entry.Project, Text = InputChangeText(entry) },
                _ => new EngineReportFinding
                    { Key = "产物早于源文件", Subject = entry.Project, Text = "早于源文件 " + RelativePath(entry.Newest) },
            }),
        ];
        List<string> screen = [];
        List<string> logOnly = [];
        FindingPresentation.Facts(findings, "", "[产物闸]", screen, logOnly, indent: prefix, unit: "个工程");
        screen.ForEach(onLogLine);
        logOnly.ForEach(CliLog.Info);
        foreach (Missing entry in absent)
            CliLog.Info("[产物闸] " + entry.Line);
        foreach (Stale entry in old)
            CliLog.Info($"[产物闸] {entry.Project}：{StaleLine(entry)}");
    }

    private static string InputChangeText(Stale entry) => (entry.Removed.Count, entry.Added.Count) switch
    {
        (1, 0) => $"编译输入已移除 {entry.Removed[0]}",
        (> 1, 0) => $"编译输入已移除 {entry.Removed[0]}（其余 {entry.Removed.Count - 1} 项见 RimRunner 日志）",
        (0, 1) => $"编译输入新增 {entry.Added[0]}",
        (0, > 1) => $"编译输入新增 {entry.Added[0]}（其余 {entry.Added.Count - 1} 项见 RimRunner 日志）",
        _ => $"编译输入已改变（移除 {entry.Removed.Count} 项，新增 {entry.Added.Count} 项，全列见 RimRunner 日志）",
    };

    private static string InputChangeLogDetail(Stale entry)
    {
        var parts = new List<string>(2);
        if (entry.Removed.Count > 0) parts.Add("移除 " + string.Join("、", entry.Removed));
        if (entry.Added.Count > 0) parts.Add("新增 " + string.Join("、", entry.Added));
        return string.Join("；", parts);
    }

    private string StaleLine(Stale stale) => stale.Kind switch
    {
        StaleKind.InputListChanged =>
            $"{Path.GetFileName(stale.Dll)} 编译输入清单已改变（dll={stale.DllUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff}）：{InputChangeLogDetail(stale)}",
        StaleKind.Upstream =>
            $"{Path.GetFileName(stale.Dll)} 早于被引用的程序集："
            + $"dll={stale.DllUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff} 被引用={stale.NewestUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff} "
            + RelativePath(stale.Newest),
        _ =>
            $"{Path.GetFileName(stale.Dll)} 早于源文件："
            + $"dll={stale.DllUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff} 源={stale.NewestUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff} "
            + RelativePath(stale.Newest),
    };

    private Scan ScanProjects(EnginePackage package, bool sources, InputManifest? priorManifest)
    {
        var scan = new Scan([], [], [], []);
        var products = new List<Product>();
        foreach (string project in descriptor.BuildOrder)
        {
            string csproj = CommandContext.FullPath(Path.Combine(productRoot, project));
            if (!File.Exists(csproj))
            {
                throw new StoreException($"构建工程不存在：{csproj}");
            }

            scan.Projects.Add(csproj);
            string assemblyName = AssemblyNameOf(csproj);
            if (!descriptor.FreshnessAssemblies.TryGetValue(assemblyName, out string? dllRel))
            {
                throw new StoreException(
                    $"{project} 的程序集名是 {assemblyName}，{OrchestratorDescriptor.FileName} 里与它配对的 dll 文件名不是 {assemblyName}.dll："
                    + "builds 与 testMod 的 dll 须以工程的 AssemblyName 命名");
            }

            products.Add(new Product(csproj, CommandContext.FullPath(Path.Combine(productRoot, dllRel))));
        }

        foreach ((string project, Product product) in descriptor.BuildOrder.Zip(products))
        {
            (string csproj, string dll) = product;
            if (!File.Exists(dll))
            {
                scan.Missing.Add(new Missing(dll, project));
                continue;
            }

            if (!sources) continue;
            IReadOnlyList<string> inputFiles = InputFiles(csproj, package);
            string projectKey = NormalizeKey(csproj);
            List<string> normalizedInputs =
            [
                .. inputFiles.Select(NormalizeKey).Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            ];
            scan.Snapshots.Add(new ProjectSnapshot(projectKey, dll, normalizedInputs));

            (string? newestFile, DateTime newestUtc) = EngineArtifact.Newest(inputFiles);
            DateTime dllUtc = File.GetLastWriteTimeUtc(dll);
            if (newestFile != null && dllUtc < newestUtc)
            {
                scan.Stale.Add(new Stale(dll, dllUtc, newestFile, newestUtc, project, StaleKind.SourceTimestamp, [], []));
                continue;
            }

            if (priorManifest != null
                && priorManifest.Projects.TryGetValue(projectKey, out ProjectInputRecord? prior)
                && DllIdentity.Read(dll).Matches(prior.Dll))
            {
                var priorSet = new HashSet<string>(prior.Inputs, StringComparer.OrdinalIgnoreCase);
                var currentSet = new HashSet<string>(normalizedInputs, StringComparer.OrdinalIgnoreCase);
                List<string> removed =
                [
                    .. priorSet.Where(path => !currentSet.Contains(path))
                        .Select(DisplayPath)
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                ];
                List<string> added =
                [
                    .. currentSet.Where(path => !priorSet.Contains(path))
                        .Select(DisplayPath)
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                ];
                if (removed.Count > 0 || added.Count > 0)
                {
                    string first = removed.Count > 0 ? removed[0] : added[0];
                    scan.Stale.Add(new Stale(dll, dllUtc, first, dllUtc, project, StaleKind.InputListChanged, removed, added));
                    continue;
                }
            }

            (string? upstreamFile, DateTime upstreamUtc) = NewestUpstream(csproj, products, package.Root);
            if (upstreamFile != null && dllUtc < upstreamUtc)
                scan.Stale.Add(new Stale(dll, dllUtc, upstreamFile, upstreamUtc, project, StaleKind.Upstream, [], []));
        }

        return scan;
    }

    private void AssertEngineRef(string testDll, EnginePackage package, Action<string>? onLogLine)
    {
        if (RefMismatch(testDll, package) is not string found) return;

        string expected = RefHash(package.RefDllPath);
        string? home = getEnvironmentVariable(EnginePackage.HomeProperty);
        onLogLine?.Invoke($"[拒绝/引擎引用] {Path.GetFileName(testDll)} {found}；"
            + $"RimRunner 安装目录里 ref 程序集的哈希是 {expected}：RimRunner 安装目录 {package.Root}；"
            + $"{EnginePackage.HomeProperty}={(string.IsNullOrWhiteSpace(home) ? "未设" : home)}");
        throw new RefusalException("测试程序集与 RimRunner 安装目录里的引擎 ref 不配对，已拒绝执行");
    }

    private static string? RefMismatch(string testDll, EnginePackage package)
    {
        string? actual = EngineArtifact.RimRunnerRefMetadata(testDll);
        if (string.Equals(actual, RefHash(package.RefDllPath), StringComparison.OrdinalIgnoreCase)) return null;
        return actual == null ? "不带 RimRunnerRef 程序集元数据" : $"记录 RimRunnerRef={actual}";
    }

    private static SelfTestParts SelfTests(EnginePackage package)
    {
        if (!package.ReadManifest().SelfTests)
            throw new StoreException("描述符声明 engineSelfTests，RimRunner 安装目录不含引擎自测（package.json 的 includes.selfTests 为 false）");
        var missing = new List<string>();
        if (!File.Exists(package.SelfTestDllPath)) missing.Add(package.SelfTestDllPath);
        if (!Directory.Exists(package.SelfTestContentDir)) missing.Add(package.SelfTestContentDir + Path.DirectorySeparatorChar);
        if (missing.Count > 0)
            throw new StoreException("RimRunner 安装目录不完整：" + string.Concat(missing.Select(path => "\n  缺 " + path)));
        return new SelfTestParts(package.SelfTestDllPath, package.SelfTestContentDir);
    }

    internal static string RefHash(string refDll)
    {
        try
        {
            return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(refDll)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new StoreException($"无法读取引擎 ref 程序集：{refDll}；{exception.Message}");
        }
    }

    internal IReadOnlyList<string> InputFiles(string csproj, EnginePackage package)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { csproj };
        foreach (string imported in ImportedProjectFiles(csproj, package.Root)) files.Add(imported);
        foreach (string buildFile in DirectoryBuildFiles(csproj)) files.Add(buildFile);
        foreach (string source in SourceFiles(csproj, package.Root)) files.Add(source);
        return [.. files];
    }

    internal (string? File, DateTime Utc) NewestInput(string csproj, EnginePackage package) =>
        EngineArtifact.Newest(InputFiles(csproj, package));

    private (string? File, DateTime Utc) NewestUpstream(string csproj, IReadOnlyList<Product> products, string packageRoot)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Product upstream in ReferencedProducts(csproj, products, packageRoot))
            files.Add(SignatureFile(upstream, packageRoot));
        return EngineArtifact.Newest(files);
    }

    private IEnumerable<Product> ReferencedProducts(string csproj, IReadOnlyList<Product> products, string packageRoot)
    {
        string projectDir = Path.GetDirectoryName(csproj)!;
        var found = new List<Product>();
        foreach (string file in EvaluatedFiles(csproj, packageRoot))
        {
            foreach (XElement element in LoadXml(file).Descendants())
            {
                if (element.Name.LocalName == "ProjectReference")
                {
                    foreach (string path in ResolveItemPaths(file, projectDir, element.Attribute("Include")?.Value, packageRoot))
                        found.AddRange(products.Where(product => PathIdentity.Same(product.Csproj, path)));
                }
                else if (element.Name.LocalName == "Reference")
                {
                    string? hint = element.Attribute("HintPath")?.Value
                        ?? element.Elements().FirstOrDefault(child => child.Name.LocalName == "HintPath")?.Value;
                    foreach (string path in ResolveItemPaths(file, projectDir, hint, packageRoot))
                        found.AddRange(products.Where(product => PathIdentity.Same(product.Dll, path)));
                }
            }
        }

        return found.Where(product => !PathIdentity.Same(product.Csproj, csproj)).Distinct();
    }

    private static IEnumerable<string> ResolveItemPaths(string declaringFile, string projectDir, string? value,
        string packageRoot)
    {
        foreach (string raw in SplitPatterns(value))
        {
            string expanded = Regex.Replace(raw, @"\$\(RimRunnerHome\)", _ => packageRoot,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            expanded = Regex.Replace(expanded, @"\$\(MSBuildThisFileDirectory\)",
                _ => Path.GetDirectoryName(declaringFile)! + Path.DirectorySeparatorChar,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (expanded.Contains("$(", StringComparison.Ordinal) || expanded.Contains("@(", StringComparison.Ordinal)
                || expanded.IndexOfAny(['*', '?']) >= 0)
                continue;
            string? full;
            try
            {
                full = CommandContext.FullPath(Path.Combine(projectDir, expanded));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                full = null;
            }

            if (full != null) yield return full;
        }
    }

    private string SignatureFile(Product upstream, string packageRoot)
    {
        if (HasFalseProperty(EvaluatedFiles(upstream.Csproj, packageRoot), "ProduceReferenceAssembly"))
            return upstream.Dll;

        string obj = Path.Combine(Path.GetDirectoryName(upstream.Csproj)!, "obj", ProductBuild.Configuration);
        if (!Directory.Exists(obj)) return upstream.Dll;
        string name = Path.GetFileName(upstream.Dll);
        string[] candidates =
        [
            Path.Combine(obj, "ref", name),
            .. Directory.EnumerateDirectories(obj).Select(tfm => Path.Combine(tfm, "ref", name)),
        ];
        (string? newest, _) = EngineArtifact.Newest(candidates.Where(File.Exists));
        return newest ?? upstream.Dll;
    }

    private List<string> EvaluatedFiles(string csproj, string packageRoot)
    {
        var evaluated = new List<string> { csproj };
        evaluated.AddRange(ImportedProjectFiles(csproj, packageRoot));
        evaluated.AddRange(DirectoryBuildFiles(csproj));
        return evaluated;
    }

    internal IReadOnlyList<string> SourceFiles(string csproj, string packageRoot)
    {
        string projectDir = Path.GetDirectoryName(csproj)!;
        XDocument document = LoadXml(csproj);
        IReadOnlyList<string> imported = ImportedProjectFiles(csproj, packageRoot);
        List<string> evaluated = EvaluatedFiles(csproj, packageRoot);
        bool defaultItems = !HasFalseProperty(evaluated, "EnableDefaultItems")
            && !HasFalseProperty(evaluated, "EnableDefaultCompileItems");

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (defaultItems)
        {
            foreach (string file in EnumerateDefaultSources(projectDir)) files.Add(file);
        }

        foreach (XElement item in CompileItems(document, "Include"))
        {
            AddMatches(files, projectDir, item.Attribute("Include")?.Value);
            RemoveMatches(files, projectDir, item.Attribute("Exclude")?.Value);
        }

        foreach (XElement item in CompileItems(document, "Remove"))
        {
            RemoveMatches(files, projectDir, item.Attribute("Remove")?.Value);
        }
        foreach (string importedFile in imported)
        {
            foreach (XElement item in CompileItems(LoadXml(importedFile), "Remove"))
                RemoveMatches(files, projectDir, item.Attribute("Remove")?.Value);
        }

        return [.. files];
    }

    private static IEnumerable<XElement> CompileItems(XDocument document, string attribute) =>
        document.Descendants().Where(element =>
            element.Name.LocalName == "Compile" && element.Attribute(attribute) != null);

    private static void AddMatches(HashSet<string> files, string projectDir, string? patterns)
    {
        foreach (string pattern in SplitPatterns(patterns))
        {
            foreach (string file in GlobFiles(projectDir, pattern)) files.Add(file);
        }
    }

    private static void RemoveMatches(HashSet<string> files, string projectDir, string? patterns)
    {
        foreach (string pattern in SplitPatterns(patterns))
        {
            foreach (string file in GlobFiles(projectDir, pattern)) files.Remove(file);
        }
    }

    private static IEnumerable<string> SplitPatterns(string? patterns) =>
        string.IsNullOrWhiteSpace(patterns)
            ? []
            : patterns.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IEnumerable<string> EnumerateDefaultSources(string projectDir)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(projectDir);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            if (!visited.Add(CommandContext.FullPath(current))) continue;
            foreach (string child in Directory.EnumerateFileSystemEntries(current))
            {
                if (Directory.Exists(child))
                {
                    string name = Path.GetFileName(child);
                    if (name.Equals("bin", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("obj", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith('.')) continue;
                    if (Directory.EnumerateFiles(child, "*.csproj").Any()) continue;
                    pending.Push(child);
                }
                else if (child.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    yield return child;
                }
            }
        }
    }

    private IReadOnlyList<string> ImportedProjectFiles(string csproj, string packageRoot)
    {
        var files = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(string file)
        {
            if (!visited.Add(CommandContext.FullPath(file))) return;
            foreach (XElement import in LoadXml(file).Descendants().Where(element =>
                         element.Name.LocalName == "Import" && element.Attribute("Project") != null))
            {
                string? resolved = ResolveImport(file, import.Attribute("Project")!.Value, packageRoot);
                if (resolved == null || !File.Exists(resolved)) continue;
                string extension = Path.GetExtension(resolved);
                if (!extension.Equals(".props", StringComparison.OrdinalIgnoreCase)
                    && !extension.Equals(".targets", StringComparison.OrdinalIgnoreCase)) continue;
                files.Add(resolved);
                Visit(resolved);
            }
        }

        Visit(csproj);
        return files;
    }

    private static string? ResolveImport(string importingFile, string raw, string packageRoot)
    {
        string expanded = Regex.Replace(raw, @"\$\(RimRunnerHome\)", _ => packageRoot,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        expanded = Regex.Replace(expanded, @"\$\(MSBuildThisFileDirectory\)",
            _ => Path.GetDirectoryName(importingFile)! + Path.DirectorySeparatorChar,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (expanded.Contains("$(", StringComparison.Ordinal) || expanded.Contains("@(", StringComparison.Ordinal))
            return null;
        try
        {
            return CommandContext.FullPath(Path.Combine(Path.GetDirectoryName(importingFile)!, expanded));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private IEnumerable<string> DirectoryBuildFiles(string csproj)
    {
        string root = CommandContext.FullPath(productRoot);
        foreach (string name in new[] { "Directory.Build.props", "Directory.Build.targets" })
        {
            string? dir = Path.GetDirectoryName(csproj);
            while (dir != null && IsWithin(dir, root))
            {
                string file = Path.Combine(dir, name);
                if (File.Exists(file))
                {
                    yield return file;
                    foreach (string imported in ImportedProjectFiles(file, PackageRootForImports())) yield return imported;
                    break;
                }

                if (PathIdentity.Same(dir, root)) break;
                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
        }
    }

    private string PackageRootForImports() => EnginePackage.Locate(getEnvironmentVariable).Root;

    private static bool IsWithin(string path, string root)
    {
        string normalized = PathIdentity.Normalize(path);
        string parent = PathIdentity.Normalize(root);
        return string.Equals(normalized, parent, StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasFalseProperty(IEnumerable<string> files, string name)
    {
        foreach (string file in files)
        {
            foreach (XElement property in LoadXml(file).Descendants()
                         .Where(element => element.Name.LocalName == name))
            {
                if (string.Equals(property.Value.Trim(), "false", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private static XDocument LoadXml(string file)
    {
        try
        {
            return XDocument.Load(file, LoadOptions.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or System.Xml.XmlException)
        {
            throw new StoreException($"无法读取工程文件：{file}；{exception.Message}");
        }
    }

    private static string AssemblyNameOf(string csproj)
    {
        XElement? element = LoadXml(csproj).Descendants()
            .FirstOrDefault(candidate => candidate.Name.LocalName == "AssemblyName");
        string? value = element?.Value.Trim();
        return string.IsNullOrEmpty(value) ? Path.GetFileNameWithoutExtension(csproj) : value;
    }

    private static IEnumerable<string> GlobFiles(string baseDir, string pattern)
    {
        string normalized = pattern.Replace('\\', '/').Trim();
        if (normalized.Length == 0) return [];
        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return MatchPath(CommandContext.FullPath(baseDir), segments, 0, visited);
    }

    private static IEnumerable<string> MatchPath(string dir, string[] segments, int index,
        HashSet<string> visited)
    {
        if (index >= segments.Length || !Directory.Exists(dir)) yield break;
        string segment = segments[index];
        bool last = index == segments.Length - 1;

        if (segment is "." or "..")
        {
            string next = CommandContext.FullPath(Path.Combine(dir, segment));
            foreach (string file in MatchPath(next, segments, index + 1, visited)) yield return file;
            yield break;
        }

        if (segment == "**")
        {
            if (last)
            {
                foreach (string file in EnumerateAllFiles(dir, visited)) yield return file;
                yield break;
            }

            foreach (string file in MatchPath(dir, segments, index + 1, visited)) yield return file;
            foreach (string child in EnumerateSubdirectories(dir, index, visited))
            {
                foreach (string file in MatchPath(child, segments, index, visited)) yield return file;
            }

            yield break;
        }

        if (last)
        {
            foreach (string file in Directory.EnumerateFiles(dir))
            {
                if (MatchSegment(Path.GetFileName(file), segment)) yield return file;
            }

            yield break;
        }

        foreach (string child in Directory.EnumerateDirectories(dir))
        {
            if (MatchSegment(Path.GetFileName(child), segment))
            {
                foreach (string file in MatchPath(child, segments, index + 1, visited)) yield return file;
            }
        }
    }

    private static IEnumerable<string> EnumerateSubdirectories(string dir, int index, HashSet<string> visited)
    {
        foreach (string child in Directory.EnumerateDirectories(dir))
        {
            if (!visited.Add(CommandContext.FullPath(child) + "|" + index)) continue;
            yield return child;
        }
    }

    private static IEnumerable<string> EnumerateAllFiles(string dir, HashSet<string> visited)
    {
        if (!visited.Add(CommandContext.FullPath(dir) + "|-")) yield break;
        foreach (string file in Directory.EnumerateFiles(dir)) yield return file;
        foreach (string child in Directory.EnumerateDirectories(dir))
        {
            foreach (string file in EnumerateAllFiles(child, visited)) yield return file;
        }
    }

    private static bool MatchSegment(string name, string pattern)
    {
        if (pattern.IndexOfAny(['*', '?']) < 0)
        {
            return string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase);
        }

        string expression = "^" + Regex.Escape(pattern)
            .Replace(@"\*", ".*", StringComparison.Ordinal)
            .Replace(@"\?", ".", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(name, expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private string ManifestPath() => Path.Combine(
        descriptor.ResolveOutputDir(productRoot),
        RimRunner.RimRunnerDataLayout.IndexDirName,
        BuildInputsFileName);

    private static InputManifest? TryLoadManifest(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            InputManifest? loaded = JsonSerializer.Deserialize<InputManifest>(File.ReadAllText(path));
            if (loaded?.Projects == null) return null;
            var projects = new Dictionary<string, ProjectInputRecord>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, ProjectInputRecord> pair in loaded.Projects)
            {
                if (pair.Value?.Dll != null && pair.Value.Inputs != null)
                    projects[pair.Key] = pair.Value;
            }

            return new InputManifest { Projects = projects };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            CliLog.Info($"[产物闸] 无法读取编译输入快照，退回时间戳判定：{path}（{exception.Message}）");
            return null;
        }
    }

    private void SaveManifest(IReadOnlyList<ProjectSnapshot> snapshots)
    {
        var manifest = new InputManifest();
        foreach (ProjectSnapshot snapshot in snapshots)
        {
            if (!File.Exists(snapshot.Dll)) continue;
            manifest.Projects[snapshot.Key] = new ProjectInputRecord
            {
                Dll = DllIdentity.Read(snapshot.Dll),
                Inputs = snapshot.Inputs,
            };
        }

        string path = ManifestPath();
        string json = JsonSerializer.Serialize(manifest, ManifestJsonOptions);
        if (File.Exists(path))
        {
            try
            {
                if (string.Equals(File.ReadAllText(path), json, StringComparison.Ordinal))
                    return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        WorktreeOutputLink.Ensure(descriptor, productRoot, CliLog.Info);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        RimTooling.IO.AtomicFile.Write(path, json);
    }

    private string NormalizeKey(string path)
    {
        string full = PathIdentity.Normalize(path);
        string root = PathIdentity.Normalize(productRoot);
        return IsWithin(full, root)
            ? Path.GetRelativePath(root, full).Replace('\\', '/')
            : full.Replace('\\', '/');
    }

    private string DisplayPath(string normalizedKey)
    {
        string native = normalizedKey.Replace('/', Path.DirectorySeparatorChar);
        return Path.IsPathRooted(native) ? RelativePath(native) : native;
    }

    private string RelativePath(string file) =>
        Path.GetRelativePath(CommandContext.FullPath(productRoot), file);
}
