using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace RimRunnerCli;

public sealed class LoadDirPool(OrchestratorDescriptor descriptor, string productRoot, ClaimStore store)
{
    private readonly OrchestratorDescriptor descriptor = descriptor;
    private readonly string productRoot = productRoot;
    private readonly ClaimStore store = store;

    private const string LayoutVersion = "2";

    private static readonly Regex PackageIdFormat =
        new("(?=.{1,60}$)^(?!\\.)(?=.*?[.])(?!.*([.])\\1+)[a-zA-Z0-9.]{1,}[a-zA-Z0-9]{1}$");

    private static readonly string[] LoadFolderConditions = ["IfModActive", "IfModActiveAll", "IfModNotActive"];

    public sealed record Choice(int Ordinal, List<string> Dirs, List<string> PackageIds);

    public static string TreeKey(string productRoot) => MachineLock.ProductRootKey(productRoot)[..8];

    public static string DirName(OrchestratorDescriptor.UnitDescriptor unit, string treeKey, int ordinal) =>
        $"{unit.Name}_{treeKey}_{ordinal}";

    public static string Suffix(string treeKey, int ordinal) => $"{treeKey}{ordinal}";

    private static readonly string[][] PackageIdPaths =
    [
        ["packageId"],
        ["modDependencies", "li", "packageId"], ["modDependenciesByVersion", "*", "li", "packageId"],
        ["loadAfter", "li"], ["loadAfterByVersion", "*", "li"],
        ["loadBefore", "li"], ["loadBeforeByVersion", "*", "li"],
        ["forceLoadAfter", "li"], ["forceLoadBefore", "li"],
        ["incompatibleWith", "li"], ["incompatibleWithByVersion", "*", "li"],
    ];

    private static readonly string[] ProseElements = ["description", "descriptionsByVersion"];

    public static string SuffixedAbout(string content, string suffix, string ownPackageId, IReadOnlyList<string> packageIds)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(content, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException exception)
        {
            throw new StoreException($"About.xml 不是合法的 XML：{exception.Message}");
        }

        if (document.Root is not { Name.LocalName: "ModMetaData" } root)
            throw new StoreException("About.xml 的根元素不是 ModMetaData");

        List<XElement> own = [.. root.Elements("packageId")];
        if (own.Count != 1 || !string.Equals(own[0].Value.Trim(), ownPackageId, StringComparison.OrdinalIgnoreCase))
            throw new StoreException(
                $"About.xml 须恰有一个 /ModMetaData/packageId 且为 {ownPackageId}，读到 {string.Join("、", own.Select(element => element.Value.Trim()))}");

        var ids = new HashSet<string>(packageIds, StringComparer.OrdinalIgnoreCase);
        var rewritten = new HashSet<XElement>();
        foreach (string[] path in PackageIdPaths)
        {
            foreach (XElement element in Match(root, path, 0).ToList())
            {
                string value = element.Value.Trim();
                if (element.HasElements || !ids.Contains(value)) continue;
                element.Value = $"{value}.{suffix}";
                rewritten.Add(element);
            }
        }

        foreach (XElement element in root.Descendants())
        {
            if (element.HasElements || rewritten.Contains(element)) continue;
            if (element.AncestorsAndSelf().Any(node => node.Parent == root && ProseElements.Contains(node.Name.LocalName))) continue;
            if (element.Name.LocalName == "displayName") continue;
            if (ids.Contains(element.Value.Trim()))
                throw new StoreException(
                    $"About.xml 的 {XmlPath(element)} 写着本产品的 packageId {element.Value.Trim()}，"
                    + "不在改写的位置上（依赖、加载序、不兼容声明）");
        }

        var output = new StringBuilder();
        var settings = new XmlWriterSettings { OmitXmlDeclaration = true, NewLineHandling = NewLineHandling.None };
        using (var writer = XmlWriter.Create(output, settings))
            document.Save(writer);
        return document.Declaration != null ? document.Declaration + output.ToString() : output.ToString();
    }

    private static IEnumerable<XElement> Match(XElement node, string[] path, int depth) =>
        depth == path.Length
            ? [node]
            : node.Elements().Where(child => path[depth] == "*" || child.Name.LocalName == path[depth])
                .SelectMany(child => Match(child, path, depth + 1));

    private static string XmlPath(XElement element) =>
        "/" + string.Join("/", element.AncestorsAndSelf().Reverse().Select(node => node.Name.LocalName));

    public Choice Acquire(ClaimRecord claim, IReadOnlyList<OrchestratorDescriptor.UnitDescriptor> units,
        Func<ClaimRecord, bool> isLive, Action<string>? log)
    {
        AssertNoSelfConditions();
        string treeKey = TreeKey(productRoot);
        List<UnitPlan> plans = [.. units.Select(unit => UnitPlan.Of(unit, SourceRoot(unit)))];
        string build = BuildFingerprint(plans);

        using (store.SweepLock())
        {
            ClearStaging();
            LegacyWrappers.SweepOrphans(descriptor, descriptor.ModsFolder, store);
            Dictionary<int, List<Present>> sets = Scan()
                .GroupBy(present => present.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToList());

            HashSet<int> referenced = [];
            HashSet<int> registered = [];
            foreach (ClaimRecord other in store.All())
            {
                if (!PathIdentity.Same(other.ProductRoot, productRoot) || other.LoadDirs is not { Count: > 0 } held)
                    continue;
                List<int> ordinals = [.. held.Select(dir => OrdinalOf(dir, treeKey)).OfType<int>()];
                registered.UnionWith(ordinals);
                if (other.Number == claim.Number || !isLive(other))
                    continue;
                referenced.UnionWith(ordinals);
                foreach ((int heldOrdinal, List<Present> members) in sets)
                    if (members.Any(member => held.Any(dir => PathIdentity.Same(dir, member.Path))))
                        referenced.Add(heldOrdinal);
            }

            bool Shareable(List<Present> members) =>
                members.All(member => member.Build == build)
                && units.All(unit => members.Any(member => member.Unit == unit.Name));

            int? shared = sets.Where(set => Shareable(set.Value)).Select(set => (int?)set.Key).Min();
            int ordinal = shared
                          ?? sets.Keys.Union(registered).Where(key => !referenced.Contains(key)).Select(key => (int?)key).Min()
                          ?? Enumerable.Range(1, int.MaxValue - 1).First(key => !sets.ContainsKey(key) && !registered.Contains(key));

            foreach ((int stale, List<Present> members) in sets)
            {
                if (stale == ordinal || referenced.Contains(stale))
                    continue;
                foreach (Present member in members)
                    Remove(member.Path);
                log?.Invoke($"[冷启动] 已删除本树不再使用的载入目录：{string.Join(" / ", members.Select(member => Path.GetFileName(member.Path)))}");
            }

            List<string> dirs = [.. units.Select(unit => Path.Combine(descriptor.ModsFolder, DirName(unit, treeKey, ordinal)))];
            List<string> packageIds = [.. units.Select(unit => $"{unit.PackageId}.{Suffix(treeKey, ordinal)}")];
            foreach (string id in packageIds)
            {
                if (!PackageIdFormat.IsMatch(id))
                    throw new StoreException(
                        $"加上实例后缀后的 packageId {id} 不符合游戏的格式（字母、数字与点，至少一个点，不以点开头结尾、不连点，总长不超过 60），"
                        + "游戏无法载入");
            }

            if (shared == null)
            {
                if (sets.TryGetValue(ordinal, out List<Present>? replaced))
                    foreach (Present member in replaced)
                        Remove(member.Path);
                for (int i = 0; i < plans.Count; i++)
                    Build(plans[i], dirs[i], treeKey, ordinal, build);
                CliLog.Info($"[冷启动] 已合成载入目录：{string.Join(" / ", dirs.Select(Path.GetFileName))}");
            }
            else
            {
                CliLog.Info($"[冷启动] 沿用同构建的载入目录：{string.Join(" / ", dirs.Select(Path.GetFileName))}");
            }

            claim.LoadDirs = dirs;
            claim.PackageIds = packageIds;
            store.Write(claim);
            return new Choice(ordinal, dirs, packageIds);
        }
    }

    private int? OrdinalOf(string dir, string treeKey)
    {
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(dir));
        foreach (OrchestratorDescriptor.UnitDescriptor unit in descriptor.Units)
        {
            string prefix = $"{unit.Name}_{treeKey}_";
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(name.AsSpan(prefix.Length), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int ordinal) && ordinal > 0)
                return ordinal;
        }

        return null;
    }

    public static List<string> ReleaseUnreferenced(ClaimStore store, string modsFolder, string productRoot,
        Func<ClaimRecord, bool> isLive)
    {
        using (store.SweepLock())
        {
            List<Present> present = [.. Scan(modsFolder, productRoot)];
            if (present.Count == 0)
                return [];
            List<string> held =
            [
                .. store.All()
                    .Where(other => PathIdentity.Same(other.ProductRoot, productRoot) && other.LoadDirs is { Count: > 0 } && isLive(other))
                    .SelectMany(other => other.LoadDirs!)
            ];
            HashSet<int> referenced =
            [
                .. present.Where(member => held.Any(dir => PathIdentity.Same(dir, member.Path))).Select(member => member.Ordinal)
            ];
            List<string> removed = [];
            foreach (Present member in present.Where(member => !referenced.Contains(member.Ordinal)))
            {
                Remove(member.Path);
                removed.Add(member.Path);
            }

            return removed;
        }
    }

    public static List<string> KeptRoots(ClaimStore store) =>
    [
        .. store.All()
            .SelectMany(claim => claim.LoadDirs ?? [])
            .Select(dir => Path.TrimEndingDirectorySeparator(CommandContext.FullPath(dir)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
    ];

    public static void RemoveTree(string modsFolder, string productRoot, Action<string>? log)
    {
        List<string> removed = [];
        foreach (Present present in Scan(modsFolder, productRoot))
        {
            Remove(present.Path);
            removed.Add(present.Path);
        }

        if (removed.Count == 1)
            log?.Invoke($"[unmount] 载入目录已删除：{removed[0]}");
        else if (removed.Count > 1)
            log?.Invoke($"[unmount] 载入目录已删除 {removed.Count} 个，在 {modsFolder}：{string.Join("、", removed.Select(Path.GetFileName))}");
    }

    private void AssertNoSelfConditions()
    {
        HashSet<string> own = new(descriptor.Units.Select(unit => unit.PackageId), StringComparer.OrdinalIgnoreCase);
        foreach (OrchestratorDescriptor.UnitDescriptor unit in descriptor.Units)
        {
            string file = Path.Combine(SourceRoot(unit), "LoadFolders.xml");
            if (!File.Exists(file))
                continue;
            var doc = new XmlDocument();
            try
            {
                doc.Load(file);
            }
            catch (XmlException)
            {
                continue;
            }

            foreach (XmlElement li in doc.SelectNodes("//li")!.OfType<XmlElement>())
            {
                foreach (string attribute in LoadFolderConditions)
                {
                    string[] hits =
                    [
                        .. li.GetAttribute(attribute).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                            .Where(own.Contains)
                    ];
                    if (hits.Length > 0)
                        throw new StoreException(
                            $"{file} 的 {attribute} 条件列出本产品的 packageId {string.Join("、", hits)}：实例里产品单元的 packageId 带后缀，"
                            + $"这一条恒判{(attribute == "IfModNotActive" ? "真" : "假")}，「{li.InnerText.Trim()}」是否装载与名单无关");
                }
            }
        }
    }

    private string SourceRoot(OrchestratorDescriptor.UnitDescriptor unit)
    {
        string root = CommandContext.FullPath(Path.Combine(productRoot, unit.SourceRel.Replace('/', Path.DirectorySeparatorChar)));
        if (!Directory.Exists(root))
            throw new StoreException($"源 mod 根不存在：{root}");
        if (!File.Exists(Path.Combine(root, "About", "About.xml")))
            throw new StoreException($"源 About.xml 不存在：{Path.Combine(root, "About", "About.xml")}");
        return root;
    }

    private string BuildFingerprint(List<UnitPlan> plans)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string text) => hash.AppendData(Encoding.UTF8.GetBytes(text + "\n"));
        Add(LayoutVersion);
        foreach (UnitPlan plan in plans)
        {
            Add($"unit {plan.Unit.Name} {plan.Unit.PackageId}");
            foreach (Step step in plan.Steps)
            {
                Add($"{step.Kind} {step.Rel}");
                if (step.Kind == StepKind.Junction)
                    Add(PathIdentity.Normalize(step.Source));
                else if (step.Kind == StepKind.File)
                {
                    byte[] bytes = File.ReadAllBytes(step.Source);
                    Add(bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    hash.AppendData(bytes);
                }
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private void Build(UnitPlan plan, string target, string treeKey, int ordinal, string build)
    {
        string staged = Path.Combine(StagingRoot(descriptor.ModsFolder), Path.GetFileName(target));
        Directory.CreateDirectory(staged);
        foreach (Step step in plan.Steps)
        {
            string at = Path.Combine(staged, step.Rel);
            switch (step.Kind)
            {
                case StepKind.Dir:
                    Directory.CreateDirectory(at);
                    break;
                case StepKind.Junction:
                    Junction.Create(at, step.Source);
                    break;
                case StepKind.File:
                    Directory.CreateDirectory(Path.GetDirectoryName(at)!);
                    File.Copy(step.Source, at, overwrite: true);
                    break;
            }
        }

        string about = Path.Combine(staged, "About", "About.xml");
        string rewritten = SuffixedAbout(File.ReadAllText(about, Encoding.UTF8), Suffix(treeKey, ordinal),
            plan.Unit.PackageId, [.. descriptor.Units.Select(unit => unit.PackageId)]);
        File.WriteAllText(about, rewritten, new UTF8Encoding(false));
        SynthMarker.WriteLoadDir(staged, productRoot, plan.Unit.Name, ordinal, build);
        Directory.Move(staged, target);
    }

    internal static string StagingRoot(string modsFolder) =>
        Path.Combine(Path.GetDirectoryName(CommandContext.FullPath(modsFolder))!, "RimRunnerStaging");

    private void ClearStaging()
    {
        string root = StagingRoot(descriptor.ModsFolder);
        if (!Directory.Exists(root))
            return;
        foreach (string dir in Directory.EnumerateDirectories(root))
            FileSystemSafety.DeleteTreeJunctionSafe(dir);
    }

    private IEnumerable<Present> Scan() => Scan(descriptor.ModsFolder, productRoot);

    private static IEnumerable<Present> Scan(string modsFolder, string productRoot)
    {
        if (!Directory.Exists(modsFolder))
            yield break;
        foreach (string dir in Directory.EnumerateDirectories(modsFolder))
        {
            if (File.GetAttributes(dir).HasFlag(FileAttributes.ReparsePoint))
                continue;
            if (SynthMarker.ReadLoadDir(dir) is { } marker && PathIdentity.Same(marker.ProductRoot, productRoot))
                yield return new Present(dir, marker.Unit, marker.Ordinal, marker.Build);
        }
    }

    internal static void Remove(string dir)
    {
        if (!Directory.Exists(dir))
            return;
        SynthMarker.RetireLoadDir(dir);
        SynthMarker.DeleteWithMarkerLast(dir, DeleteContents);
    }

    private static void DeleteContents(string dir)
    {
        foreach (string child in Directory.EnumerateFileSystemEntries(dir))
        {
            if (Path.GetFileName(child) == SynthMarker.FileName)
                continue;
            if (File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint))
            {
                if (Junction.GetLinkInfo(child).Kind != RimTooling.Windows.ReparseLinkKind.Junction)
                    throw new StoreException($"拒绝删除：载入目录内出现非 junction 的 reparse point：{child}");
                Junction.Remove(child);
            }
            else if (Directory.Exists(child))
                FileSystemSafety.DeleteTreeJunctionSafe(child);
            else
                File.Delete(child);
        }
    }

    private sealed record Present(string Path, string Unit, int Ordinal, string? Build);

    private enum StepKind { Dir, Junction, File }

    private sealed record Step(StepKind Kind, string Rel, string Source);

    private sealed class UnitPlan
    {
        public required OrchestratorDescriptor.UnitDescriptor Unit { get; init; }
        public List<Step> Steps { get; } = [];

        public static UnitPlan Of(OrchestratorDescriptor.UnitDescriptor unit, string sourceRoot)
        {
            var plan = new UnitPlan { Unit = unit };
            HashSet<string> ancestors = new(StringComparer.OrdinalIgnoreCase);
            foreach (string assemblies in FindAssemblies(sourceRoot))
            {
                for (string? rel = Path.GetDirectoryName(Path.GetRelativePath(sourceRoot, assemblies));
                     !string.IsNullOrEmpty(rel);
                     rel = Path.GetDirectoryName(rel))
                    ancestors.Add(rel);
            }

            plan.Walk(sourceRoot, "", ancestors);
            return plan;
        }

        private void Walk(string sourceRoot, string rel, HashSet<string> ancestors)
        {
            string current = Path.Combine(sourceRoot, rel);
            foreach (string child in Directory.EnumerateFileSystemEntries(current).Order(StringComparer.Ordinal))
            {
                string name = Path.GetFileName(child);
                string childRel = rel.Length == 0 ? name : Path.Combine(rel, name);
                bool linked = File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint);
                if (!Directory.Exists(child))
                {
                    Steps.Add(new Step(StepKind.File, childRel, child));
                }
                else if (!linked && ((rel.Length == 0 && name == "About") || IsAssemblies(name)))
                {
                    Copy(child, childRel);
                }
                else if (!linked && ancestors.Contains(childRel))
                {
                    Steps.Add(new Step(StepKind.Dir, childRel, child));
                    Walk(sourceRoot, childRel, ancestors);
                }
                else
                {
                    Steps.Add(new Step(StepKind.Junction, childRel, child));
                }
            }
        }

        private void Copy(string source, string rel)
        {
            FileSystemSafety.AssertNoReparseInside(source, "拷贝");
            Steps.Add(new Step(StepKind.Dir, rel, source));
            foreach (string file in Directory.EnumerateFiles(source).Order(StringComparer.Ordinal))
                Steps.Add(new Step(StepKind.File, Path.Combine(rel, Path.GetFileName(file)), file));
            foreach (string dir in Directory.EnumerateDirectories(source).Order(StringComparer.Ordinal))
                Copy(dir, Path.Combine(rel, Path.GetFileName(dir)));
        }

        private static bool IsAssemblies(string name) => string.Equals(name, "Assemblies", StringComparison.OrdinalIgnoreCase);

        private static IEnumerable<string> FindAssemblies(string root)
        {
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                foreach (string dir in Directory.EnumerateDirectories(pending.Pop()))
                {
                    string name = Path.GetFileName(dir);
                    if (name.StartsWith('.') || File.GetAttributes(dir).HasFlag(FileAttributes.ReparsePoint))
                        continue;
                    if (IsAssemblies(name))
                        yield return dir;
                    else
                        pending.Push(dir);
                }
            }
        }
    }
}
