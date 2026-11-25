using System.Text;
using System.Xml;

namespace RimRunnerCli;

internal static class ConfigCaptureCommand
{
    internal const string LayerRoot = "root";
    internal const string LayerModList = "modlist";

    private static readonly string[] DeviceBoundPrefs =
        ["windowPositionX", "windowPositionY", "preferredMonitor", "preferredGpuName", "preferredGpuUuid", "exclusiveFullscreen"];

    internal static int Execute(string searchDir, string? modListSwitch, string? layer, IReadOnlyList<string> files,
        string? playerConfigOverride = null)
    {
        string? productRoot = OrchestratorDescriptor.FindProductRoot(searchDir);
        if (productRoot == null)
        {
            Console.WriteLine($"[FAIL] 在 {searchDir} 及其上级目录未找到 {OrchestratorDescriptor.FileName}");
            return ExitCodes.Incomplete;
        }

        (_, OrchestratorDescriptor descriptor) = OrchestratorDescriptor.Load(productRoot);
        string? modList = descriptor.ResolveModList(modListSwitch);
        string layerName = layer ?? LayerRoot;
        if (layerName == LayerModList && modList == null)
        {
            Console.WriteLine("[FAIL] --layer modlist 须指定名单：命令行 --modlist 或描述符 defaultModList");
            return ExitCodes.Incomplete;
        }

        string player = playerConfigOverride ?? SaveDataInitializer.PlayerConfigDirOf();
        if (!Directory.Exists(player))
        {
            Console.WriteLine($"[FAIL] 本机游戏的 Config 目录不存在：{player}");
            return ExitCodes.Incomplete;
        }

        IReadOnlyList<string> active = modList != null
            ? ModListDefinition.Load(Path.Combine(productRoot, descriptor.ModListsDir), modList).ModIds
            : PlayerActiveMods(player);
        string repo = descriptor.ResolveConfigDir(productRoot);
        string target = layerName == LayerRoot ? repo : Path.Combine(repo, InstanceConfig.ModListsDirName, modList!);

        var writes = new List<(string Path, byte[] Content, string Origin)>();
        CollectModSettings(descriptor, player, active, target, writes);
        var problems = new List<string>();
        foreach (string file in files)
        {
            CollectLooseEntry(player, file, target, writes, problems);
        }

        (string prefsPath, Dictionary<string, string> prefs, List<string> prefsSkipped) =
            PrefsCapture(descriptor, productRoot, player, layerName, modList);
        bool prefsPresent = layerName == LayerModList ? File.Exists(prefsPath) : RuntimeHasPrefs(prefsPath);
        IReadOnlyDictionary<string, string> existing = layerName == LayerModList
            ? new InstanceConfig(descriptor).ModListPrefs(productRoot, modList!)
            : descriptor.PrefsOverrides();
        bool prefsSame = prefsPresent && existing.Count == prefs.Count
                         && prefs.All(pair => existing.TryGetValue(pair.Key, out string? value) && value == pair.Value);
        if (prefs.Count > 0 && prefsPresent && !prefsSame)
        {
            problems.Add(layerName == LayerModList
                ? $"名单层 Prefs 覆盖已存在且内容不同：{prefsPath}"
                : $"{prefsPath} 的 [config.prefs] 表已存在且内容不同");
        }

        foreach ((string path, byte[] content, _) in writes)
        {
            if (File.Exists(path) && !File.ReadAllBytes(path).AsSpan().SequenceEqual(content))
            {
                problems.Add($"目标已存在且内容不同：{path}");
            }
        }

        if (problems.Count > 0)
        {
            foreach (string problem in problems)
            {
                Console.WriteLine(problem);
            }

            Console.WriteLine("未写入任何文件");
            return 1;
        }

        foreach ((string path, byte[] content, string origin) in writes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
            Console.WriteLine($"[capture] {origin} → {path}");
        }

        if (prefs.Count > 0 && !prefsPresent)
        {
            string text = (layerName == LayerRoot ? "\n[config.prefs]\n" : "")
                          + string.Concat(prefs.Select(pair => pair.Key + " = " + Quote(pair.Value) + "\n"));
            if (layerName == LayerRoot)
            {
                File.AppendAllText(prefsPath, text);
            }
            else
            {
                File.WriteAllText(prefsPath, text);
            }

            Console.WriteLine($"[capture] Prefs → {prefsPath}");
        }

        if (prefsSkipped.Count > 0)
        {
            Console.WriteLine($"[capture] Prefs 未抄：{string.Join("、", prefsSkipped)}");
        }

        List<string> loose = [.. Directory.EnumerateFileSystemEntries(player).Select(Path.GetFileName).OfType<string>()
            .Where(InstanceConfig.IsLooseEntry).Where(name => !files.Contains(name, StringComparer.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)];
        if (loose.Count > 0)
        {
            Console.WriteLine($"[capture] 本机 Config 里无法归入 mod 而未抄的条目 {loose.Count} 项：{string.Join("、", loose)}");
        }

        return 0;
    }

    private static IReadOnlyList<string> PlayerActiveMods(string player) =>
        RimTooling.RimWorld.RimWorldModsConfig.Read(
            CommandContext.FullPath(Path.Combine(player, "ModsConfig.xml"))).ActiveModIds;

    private static void CollectModSettings(OrchestratorDescriptor descriptor, string player, IReadOnlyList<string> active,
        string target, List<(string, byte[], string)> writes)
    {
        var units = descriptor.Units.ToDictionary(unit => unit.PackageId.ToLowerInvariant(), StringComparer.Ordinal);
        var wanted = active.Select(id => id.ToLowerInvariant()).Distinct().ToList();
        Dictionary<string, List<string>> installed =
            InstanceConfig.InstalledFolders(descriptor, descriptor.ModsFolder, wanted.Where(id => !units.ContainsKey(id)));
        string[] names = [.. Directory.EnumerateFiles(player, "Mod_*.xml").Select(Path.GetFileName).OfType<string>()];
        foreach (string id in wanted)
        {
            IEnumerable<string> folders = units.TryGetValue(id, out OrchestratorDescriptor.UnitDescriptor? unit)
                ? InstanceConfig.PlayerUnitFolders(unit, descriptor.ModsFolder)
                : installed.GetValueOrDefault(id) ?? [];
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string folder in folders)
            {
                string prefix = $"Mod_{folder}_";
                foreach (string name in names.Where(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    string modClass = name[prefix.Length..];
                    if (taken.Add(modClass))
                    {
                        writes.Add((Path.Combine(target, InstanceConfig.ModsDirName, id, modClass),
                            File.ReadAllBytes(Path.Combine(player, name)), name));
                    }
                }
            }
        }
    }

    private static void CollectLooseEntry(string player, string relative, string target,
        List<(string, byte[], string)> writes, List<string> problems)
    {
        string name = relative.Replace('\\', '/').Trim('/');
        if (name.Contains('/') || !InstanceConfig.IsLooseEntry(name))
        {
            problems.Add($"--file {relative} 须是本机 Config 根下无法归入 mod 的条目名");
            return;
        }

        string source = Path.Combine(player, name);
        string destination = Path.Combine(target, InstanceConfig.FilesDirName, name);
        if (File.Exists(source))
        {
            writes.Add((destination, File.ReadAllBytes(source), name));
        }
        else if (Directory.Exists(source))
        {
            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(source, file);
                writes.Add((Path.Combine(destination, rel), File.ReadAllBytes(file), Path.Combine(name, rel)));
            }
        }
        else
        {
            problems.Add($"--file {relative}：本机 Config 里没有 {source}");
        }
    }

    private static (string Path, Dictionary<string, string> Values, List<string> Skipped) PrefsCapture(
        OrchestratorDescriptor descriptor, string productRoot, string player, string layer, string? modList)
    {
        string path = layer == LayerRoot
            ? descriptor.ResolveWorkspaceFile(productRoot, RuntimeDocument.FileName)
            : Path.Combine(productRoot, descriptor.ModListsDir, modList + InstanceConfig.PrefsLayerSuffix);
        string source = Path.Combine(player, "Prefs.xml");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var skipped = new List<string>();
        if (!File.Exists(source))
        {
            return (path, values, skipped);
        }

        var doc = new XmlDocument();
        doc.Load(source);
        foreach (XmlElement element in doc.DocumentElement!.ChildNodes.OfType<XmlElement>())
        {
            string name = element.Name;
            if (SaveDataInitializer.PinnedPrefNames.Contains(name))
            {
                continue;
            }

            if (DeviceBoundPrefs.Contains(name) || element.ChildNodes.OfType<XmlElement>().Any())
            {
                skipped.Add(name);
                continue;
            }

            values[name] = element.InnerText;
        }

        return (path, values, skipped);
    }

    private static bool RuntimeHasPrefs(string path)
    {
        (_, RimTooling.IO.TomlTable table) = TomlFile.Read(path, RuntimeDocument.FileName,
            message => new DescriptorException(message));
        return table.TryGetValue("config", out object? config) && config is RimTooling.IO.TomlTable section
               && section.ContainsKey("prefs");
    }

    private static string Quote(string text) => RimTooling.IO.TomlDocument.FormatString(text);
}
