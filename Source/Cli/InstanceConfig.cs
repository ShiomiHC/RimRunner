using System.Globalization;
using System.Text.Json;
using System.Xml;
using RimRunner;
using RimTooling.IO;

namespace RimRunnerCli;

public sealed class InstanceConfig(OrchestratorDescriptor descriptor)
{
    public const string PrefsBaseDefault = "default";
    public const string PrefsBasePlayer = "player";

    public const string IgnoreSystemScalingPref = "ignoreSystemScaling";
    public const string ModSettingsBaseNone = "none";
    public const string ModSettingsBasePlayer = "player";

    public const string ModsDirName = "mods";
    public const string FilesDirName = "files";
    public const string ModListsDirName = "modlists";

    public const string PrefsLayerSuffix = ".prefs.toml";

    internal static readonly string[] GameOwnedFiles =
        ["ModsConfig.xml", "Prefs.xml", "KeyPrefs.xml", "Knowledge.xml", "LastPlayedVersion.txt"];

    private static readonly string[] PlayerPrefsCompanions = ["KeyPrefs.xml", "Knowledge.xml"];

    private const string CorePackageId = "ludeon.rimworld";

    public sealed record Inputs(
        string ProductRoot,
        string? ModList,
        IReadOnlyList<string> ActiveModIds,
        bool Showcase,
        Func<string> PlayerConfigDir,
        IReadOnlyDictionary<string, string> UnitFolders,
        string ModsFolder);

    public sealed class Outcome
    {
        public string PrefsBase { get; init; } = "";
        public string ModSettingsBase { get; init; } = "";
        public List<(string Key, string Layer)> PrefsOverrides { get; } = [];
        public List<RepoSetting> RepoSettings { get; } = [];
        public int PlayerSettingFiles { get; set; }
        public int PlayerLooseEntries { get; set; }
        public int RepoLooseEntries { get; set; }
        public List<string> UnusedRepoPackageIds { get; } = [];
        public List<string> UninstalledRepoPackageIds { get; } = [];

        public bool IsDefault => PrefsBase == PrefsBaseDefault && ModSettingsBase == ModSettingsBaseNone
                                 && PrefsOverrides.Count == 0 && RepoSettings.Count == 0 && RepoLooseEntries == 0
                                 && UnusedRepoPackageIds.Count == 0 && UninstalledRepoPackageIds.Count == 0;

        public string Announcement()
        {
            int fromRoot = PrefsOverrides.Count(item => item.Layer == LayerRoot);
            string prefs = $"Prefs 基底 {PrefsBase}，覆盖 {PrefsOverrides.Count} 项"
                           + (PrefsOverrides.Count > 0 ? $"（产品根层 {fromRoot}、名单层 {PrefsOverrides.Count - fromRoot}）" : "");
            string mods = $"mod 设置基底 {ModSettingsBase}，设置目录 {RepoSettings.Count} 份"
                          + (RepoLooseEntries > 0 ? $"、files {RepoLooseEntries} 项" : "")
                          + (ModSettingsBase == ModSettingsBasePlayer
                              ? $"，玩家本机 {PlayerSettingFiles} 份、其他条目 {PlayerLooseEntries} 项" : "")
                          + (UnusedRepoPackageIds.Count > 0
                              ? $"；本轮名单未含、未写入：{string.Join("、", UnusedRepoPackageIds)}" : "")
                          + (UninstalledRepoPackageIds.Count > 0
                              ? $"；本机未找到装载目录、未写入：{string.Join("、", UninstalledRepoPackageIds)}" : "");
            return $"实例 Config：{prefs}；{mods}";
        }
    }

    public sealed record RepoSetting(string PackageId, string ModClass, IReadOnlyList<string> Folders, string Layer);

    public const string LayerRoot = "root";
    public const string LayerModList = "modlist";

    private readonly OrchestratorDescriptor descriptor = descriptor;

    public static string ModSettingsBaseOf(OrchestratorDescriptor descriptor, bool showcase) =>
        showcase ? ModSettingsBasePlayer : descriptor.ModSettingsBaseOrDefault;

    public Outcome Build(string configDir, Inputs inputs)
    {
        Directory.CreateDirectory(configDir);
        string prefsBase = inputs.Showcase ? PrefsBasePlayer : descriptor.PrefsBaseOrDefault;
        string modSettingsBase = ModSettingsBaseOf(descriptor, inputs.Showcase);
        var outcome = new Outcome { PrefsBase = prefsBase, ModSettingsBase = modSettingsBase };

        WriteModsConfig(configDir, inputs);
        WritePrefs(configDir, inputs, prefsBase, outcome);
        WriteModSettings(configDir, inputs, modSettingsBase, outcome);
        WriteManifest(configDir, outcome);
        return outcome;
    }

    private void WriteModsConfig(string configDir, Inputs inputs)
    {
        IReadOnlyList<string> expansions = OfficialExpansions(descriptor);
        if (inputs.ActiveModIds.Count > 0)
        {
            RimTooling.RimWorld.RimWorldModsConfig.Create(inputs.ActiveModIds, expansions)
                .Save(Path.Combine(configDir, "ModsConfig.xml"));
            return;
        }

        System.Xml.Linq.XDocument doc = RimTooling.RimWorld.RimWorldModsConfig.Create([], expansions);
        System.Xml.Linq.XElement activeEl = doc.Root!.Element("activeMods")!;
        foreach (string id in PlayerActiveMods(inputs.PlayerConfigDir()))
        {
            activeEl.Add(new System.Xml.Linq.XElement("li", id));
        }

        doc.Save(Path.Combine(configDir, "ModsConfig.xml"));
    }

    private static IReadOnlyList<string> PlayerActiveMods(string playerConfigDir)
    {
        string path = Path.Combine(playerConfigDir, "ModsConfig.xml");
        if (!File.Exists(path))
        {
            throw new StoreException(
                $"未指定名单时 activeMods 取本机游戏的 ModsConfig.xml，该文件不存在：{path}"
                + Environment.NewLine + $"  本进程的打包应用身份：{SaveDataInitializer.PackageFullName() ?? "无"}");
        }

        return RimTooling.RimWorld.RimWorldModsConfig.Read(path).ActiveModIds;
    }

    public static IReadOnlyList<string> OfficialExpansions(OrchestratorDescriptor descriptor)
    {
        string dataDir = CommandContext.FullPath(Path.Combine(GameDir(descriptor), "Data"));
        if (!Directory.Exists(dataDir))
        {
            throw new StoreException($"游戏 Data 目录不存在：{dataDir}");
        }

        IReadOnlyList<ModsFolderScan.ModsEntry> entries = ModsFolderScan.Scan(dataDir);
        if (!entries.Any(entry => string.Equals(entry.PackageId, CorePackageId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new StoreException($"游戏 Data 目录里没有本体 {CorePackageId}：{dataDir}");
        }

        return RimTooling.RimWorld.RimWorldModsConfig.KnownExpansions(dataDir);
    }

    private static string GameDir(OrchestratorDescriptor descriptor) =>
        Path.GetDirectoryName(CommandContext.FullPath(descriptor.GameExe))
        ?? throw new StoreException($"gameExe 缺少父目录：{descriptor.GameExe}");

    private void WritePrefs(string configDir, Inputs inputs, string prefsBase, Outcome outcome)
    {
        string prefsPath = Path.Combine(configDir, "Prefs.xml");
        if (prefsBase == PrefsBasePlayer)
        {
            string player = inputs.PlayerConfigDir();
            string source = Path.Combine(player, "Prefs.xml");
            if (!File.Exists(source))
            {
                throw new StoreException($"Prefs 基底是 player，本机游戏的 Prefs.xml 不存在：{source}");
            }

            File.Copy(source, prefsPath, overwrite: true);
            foreach (string companion in PlayerPrefsCompanions)
            {
                string from = Path.Combine(player, companion);
                if (File.Exists(from))
                {
                    File.Copy(from, Path.Combine(configDir, companion), overwrite: true);
                }
            }
        }
        else
        {
            var empty = new XmlDocument();
            empty.AppendChild(empty.CreateXmlDeclaration("1.0", "utf-8", null));
            empty.AppendChild(empty.CreateElement("PrefsData"));
            SaveDataInitializer.SetPrefsNode(empty, IgnoreSystemScalingPref, "True");
            empty.Save(prefsPath);
        }

        var layers = new List<(IReadOnlyDictionary<string, string> Values, string Layer)>
        {
            (descriptor.PrefsOverrides(), LayerRoot),
        };
        if (inputs.ModList != null)
        {
            layers.Add((ModListPrefs(inputs.ProductRoot, inputs.ModList), LayerModList));
        }

        if (layers.All(layer => layer.Values.Count == 0))
        {
            return;
        }

        var doc = new XmlDocument();
        doc.Load(prefsPath);
        var written = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((IReadOnlyDictionary<string, string> values, string layer) in layers)
        {
            foreach ((string key, string value) in values)
            {
                SaveDataInitializer.SetPrefsNode(doc, key, value);
                written[key] = layer;
            }
        }

        doc.Save(prefsPath);
        outcome.PrefsOverrides.AddRange(written.Select(pair => (pair.Key, pair.Value)));
    }

    public IReadOnlyDictionary<string, string> ModListPrefs(string productRoot, string modList)
    {
        string path = Path.Combine(productRoot, descriptor.ModListsDir, modList + PrefsLayerSuffix);
        string file = modList + PrefsLayerSuffix;
        (bool exists, TomlTable table) = TomlFile.Read(path, file, message => new DescriptorException(message));
        if (!exists)
        {
            return new Dictionary<string, string>();
        }

        var problems = new List<string>();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string key, object value) in table)
        {
            if (PrefsScalar(value) is string text)
            {
                values[key] = text;
            }
            else
            {
                problems.Add($"{key} 的值须是字符串、布尔或数（收到 {value.GetType().Name}）");
            }
        }

        problems.AddRange(PinnedCollisions(values.Keys));
        if (problems.Count > 0)
        {
            throw new DescriptorException($"{file} 有误：{path}" + Environment.NewLine
                + string.Join(Environment.NewLine, problems.Select(problem => "  " + problem)));
        }

        return values;
    }

    public static string? PrefsScalar(object value) => value switch
    {
        string text => text,
        bool flag => flag ? "True" : "False",
        long or int => Convert.ToString(value, CultureInfo.InvariantCulture),
        double or float => Convert.ToString(value, CultureInfo.InvariantCulture),
        _ => null,
    };

    public static string? PrefsScalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => "True",
        JsonValueKind.False => "False",
        JsonValueKind.Number => value.GetRawText(),
        _ => null,
    };

    public static IEnumerable<string> PinnedCollisions(IEnumerable<string> keys) =>
        keys.Where(SaveDataInitializer.PinnedPrefNames.Contains)
            .Select(key => $"{key} 由引擎锁定，不能覆盖");

    private void WriteModSettings(string configDir, Inputs inputs, string modSettingsBase, Outcome outcome)
    {
        IReadOnlyList<string> active = inputs.ActiveModIds.Count > 0
            ? inputs.ActiveModIds
            : ReadActiveIds(Path.Combine(configDir, "ModsConfig.xml"));
        Dictionary<string, List<string>> folders = FoldersOf(active, inputs);

        if (modSettingsBase == ModSettingsBasePlayer)
        {
            CopyPlayerSettings(inputs.PlayerConfigDir(), configDir, PlayerPrefixes(folders, inputs), outcome);
        }

        string repo = descriptor.ResolveConfigDir(inputs.ProductRoot);
        if (!Directory.Exists(repo))
        {
            return;
        }

        CheckRepoLayout(repo, inputs.ProductRoot);
        var unused = new SortedSet<string>(StringComparer.Ordinal);
        var uninstalled = new SortedSet<string>(StringComparer.Ordinal);
        var settings = new Dictionary<(string Id, string ModClass), RepoSetting>();
        ApplyRepoLayer(repo, LayerRoot, configDir, folders, settings, unused, uninstalled, outcome);
        if (inputs.ModList != null)
        {
            string layer = Path.Combine(repo, ModListsDirName, inputs.ModList);
            if (Directory.Exists(layer))
            {
                ApplyRepoLayer(layer, LayerModList, configDir, folders, settings, unused, uninstalled, outcome);
            }
        }

        outcome.RepoSettings.AddRange(settings.Values);
        outcome.UnusedRepoPackageIds.AddRange(unused);
        outcome.UninstalledRepoPackageIds.AddRange(uninstalled);
    }

    private static IReadOnlyList<string> ReadActiveIds(string modsConfig) =>
        RimTooling.RimWorld.RimWorldModsConfig.Read(CommandContext.FullPath(modsConfig)).ActiveModIds;

    private Dictionary<string, List<string>> FoldersOf(IReadOnlyList<string> active, Inputs inputs)
    {
        var wanted = new HashSet<string>(active.Select(id => id.ToLowerInvariant()), StringComparer.Ordinal);
        var units = new HashSet<string>(descriptor.Units.Select(unit => unit.PackageId.ToLowerInvariant()), StringComparer.Ordinal);
        var folders = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (string key in wanted)
        {
            folders[key] = [];
        }

        foreach ((string id, string folder) in inputs.UnitFolders)
        {
            string key = id.ToLowerInvariant();
            if (wanted.Contains(key))
            {
                folders[key] = [folder];
            }
        }

        foreach ((string key, List<string> found) in InstalledFolders(descriptor, inputs.ModsFolder, wanted.Where(id => !units.Contains(id))))
        {
            folders[key] = found;
        }

        return folders;
    }

    public static Dictionary<string, List<string>> InstalledFolders(OrchestratorDescriptor descriptor, string modsFolder,
        IEnumerable<string> ids)
    {
        var folders = ids.Select(id => id.ToLowerInvariant()).Distinct()
            .ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
        IReadOnlyList<string> roots = RimTooling.RimWorld.SteamWorkshop.BuildModSearchRoots(
            GameDir(descriptor),
            RimTooling.RimWorld.ModSearchOrder.ModsFirst,
            includeData: false,
            includeWorkshop: true,
            customModsDir: CommandContext.FullPath(modsFolder));

        foreach (string root in roots)
        {
            foreach (ModsFolderScan.ModsEntry entry in ModsFolderScan.Scan(root))
            {
                string? key = entry.PackageId?.ToLowerInvariant();
                if (key == null || !folders.TryGetValue(key, out List<string>? list))
                {
                    continue;
                }

                string name = Path.GetFileName(entry.Path);
                if (!list.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(name);
                }
            }
        }

        return folders;
    }

    public static IEnumerable<string> PlayerUnitFolders(OrchestratorDescriptor.UnitDescriptor unit, string modsFolder) =>
        ModsFolderScan.Scan(modsFolder)
            .Where(entry => string.Equals(entry.PackageId, unit.PackageId, StringComparison.OrdinalIgnoreCase))
            .Select(entry => Path.GetFileName(entry.Path))
            .Prepend(unit.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private List<(string From, string To)> PlayerPrefixes(Dictionary<string, List<string>> folders, Inputs inputs)
    {
        var pairs = new List<(string From, string To)>();
        var units = descriptor.Units.ToDictionary(unit => unit.PackageId.ToLowerInvariant(), StringComparer.Ordinal);
        foreach ((string id, List<string> targets) in folders)
        {
            if (!units.TryGetValue(id, out OrchestratorDescriptor.UnitDescriptor? unit))
            {
                pairs.AddRange(targets.Select(folder => ($"Mod_{folder}_", $"Mod_{folder}_")));
                continue;
            }

            IEnumerable<string> sources = PlayerUnitFolders(unit, inputs.ModsFolder);
            foreach (string source in sources)
            {
                pairs.AddRange(targets.Select(folder => ($"Mod_{source}_", $"Mod_{folder}_")));
            }
        }

        return pairs;
    }

    private static void CopyPlayerSettings(string playerConfigDir, string configDir,
        List<(string From, string To)> prefixes, Outcome outcome)
    {
        if (!Directory.Exists(playerConfigDir))
        {
            throw new StoreException($"mod 设置基底是 player，本机游戏的 Config 目录不存在：{playerConfigDir}");
        }

        foreach (string entry in Directory.EnumerateFileSystemEntries(playerConfigDir))
        {
            string name = Path.GetFileName(entry);
            bool modEntry = name.StartsWith("Mod_", StringComparison.Ordinal);
            if (modEntry)
            {
                foreach ((string from, string to) in prefixes)
                {
                    if (name.StartsWith(from, StringComparison.OrdinalIgnoreCase))
                    {
                        CopyEntry(entry, Path.Combine(configDir, to + name[from.Length..]));
                        outcome.PlayerSettingFiles++;
                    }
                }

                continue;
            }

            if (IsLooseEntry(name))
            {
                outcome.PlayerLooseEntries++;
                CopyEntry(entry, Path.Combine(configDir, name));
            }
        }
    }

    public static bool IsLooseEntry(string name) =>
        !name.StartsWith("Mod_", StringComparison.Ordinal)
        && !GameOwnedFiles.Contains(name, StringComparer.OrdinalIgnoreCase)
        && !name.StartsWith("ModsConfig", StringComparison.OrdinalIgnoreCase)
        && !name.Contains(".bak", StringComparison.OrdinalIgnoreCase);

    internal static void CopyEntry(string from, string to)
    {
        if (Directory.Exists(from))
        {
            FileSystemSafety.CopyDirectory(from, to, "拷贝设置");
        }
        else
        {
            File.Copy(from, to, overwrite: true);
        }
    }

    private void CheckRepoLayout(string repo, string productRoot)
    {
        var problems = new List<string>();
        CheckLayer(repo, allowModLists: true, problems);
        string modLists = Path.Combine(repo, ModListsDirName);
        if (Directory.Exists(modLists))
        {
            string modListsDir = Path.Combine(productRoot, descriptor.ModListsDir);
            foreach (string entry in Directory.EnumerateFileSystemEntries(modLists))
            {
                string name = Path.GetFileName(entry);
                if (!Directory.Exists(entry))
                {
                    problems.Add($"{ModListsDirName}/{name} 须是以名单名命名的目录");
                    continue;
                }

                string modListPath = Path.Combine(modListsDir, name + RimTooling.RimWorld.RimWorldModList.Extension);
                if (!File.Exists(modListPath))
                {
                    problems.Add($"{ModListsDirName}/{name} 没有同名名单（{modListPath}）");
                }

                CheckLayer(entry, allowModLists: false, problems, $"{ModListsDirName}/{name}/");
            }
        }

        if (problems.Count > 0)
        {
            throw new DescriptorException($"设置目录（<dir>/data/config）有误：{repo}" + Environment.NewLine
                + string.Join(Environment.NewLine, problems.Select(problem => "  " + problem)));
        }
    }

    private static void CheckLayer(string layer, bool allowModLists, List<string> problems, string prefix = "")
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(layer))
        {
            string name = Path.GetFileName(entry);
            bool known = name == ModsDirName || name == FilesDirName || (allowModLists && name == ModListsDirName);
            if (!known || !Directory.Exists(entry))
            {
                problems.Add($"无法识别 {prefix}{name}：只能有 {ModsDirName}/、{FilesDirName}/"
                             + (allowModLists ? $"、{ModListsDirName}/" : ""));
            }
        }

        string mods = Path.Combine(layer, ModsDirName);
        if (Directory.Exists(mods))
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(mods))
            {
                string id = Path.GetFileName(entry);
                if (!Directory.Exists(entry))
                {
                    problems.Add($"{prefix}{ModsDirName}/{id} 须是 packageId 目录");
                    continue;
                }

                foreach (string file in Directory.EnumerateFileSystemEntries(entry))
                {
                    string fileName = Path.GetFileName(file);
                    if (Directory.Exists(file) || !fileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    {
                        problems.Add($"{prefix}{ModsDirName}/{id}/{fileName} 须是 <Mod 类名>.xml");
                    }
                }
            }
        }

        string files = Path.Combine(layer, FilesDirName);
        if (Directory.Exists(files))
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(files))
            {
                string name = Path.GetFileName(entry);
                if (GameOwnedFiles.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    problems.Add($"{prefix}{FilesDirName}/{name} 是游戏本体的文件，由 CLI 生成");
                }
                else if (name.StartsWith("Mod_", StringComparison.Ordinal))
                {
                    problems.Add($"{prefix}{FilesDirName}/{name} 是 mod 设置的文件名形态");
                }
            }
        }
    }

    private static void ApplyRepoLayer(string layer, string layerName, string configDir,
        Dictionary<string, List<string>> folders, Dictionary<(string, string), RepoSetting> settings,
        SortedSet<string> unused, SortedSet<string> uninstalled, Outcome outcome)
    {
        string mods = Path.Combine(layer, ModsDirName);
        if (Directory.Exists(mods))
        {
            foreach (string dir in Directory.EnumerateDirectories(mods))
            {
                string id = Path.GetFileName(dir).ToLowerInvariant();
                if (!folders.TryGetValue(id, out List<string>? targets))
                {
                    unused.Add(id);
                    continue;
                }

                if (targets.Count == 0)
                {
                    uninstalled.Add(id);
                    continue;
                }

                foreach (string file in Directory.EnumerateFiles(dir, "*.xml"))
                {
                    string modClass = Path.GetFileNameWithoutExtension(file);
                    foreach (string folder in targets)
                    {
                        File.Copy(file, Path.Combine(configDir, $"Mod_{folder}_{modClass}.xml"), overwrite: true);
                    }

                    settings[(id, modClass)] = new RepoSetting(id, modClass, targets, layerName);
                }
            }
        }

        string files = Path.Combine(layer, FilesDirName);
        if (Directory.Exists(files))
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(files))
            {
                CopyEntry(entry, Path.Combine(configDir, Path.GetFileName(entry)));
                outcome.RepoLooseEntries++;
            }
        }
    }

    private static void WriteManifest(string configDir, Outcome outcome)
    {
        using var stream = File.Create(Path.Combine(configDir, RimRunnerWireKeys.ConfigManifest.FileName));
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteStartArray(RimRunnerWireKeys.ConfigManifest.PrefsKeys);
        foreach ((string key, string layer) in outcome.PrefsOverrides)
        {
            writer.WriteStartObject();
            writer.WriteString(RimRunnerWireKeys.ConfigManifest.Key, key);
            writer.WriteString(RimRunnerWireKeys.ConfigManifest.Layer, layer);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray(RimRunnerWireKeys.ConfigManifest.ModSettings);
        foreach (RepoSetting setting in outcome.RepoSettings)
        {
            writer.WriteStartObject();
            writer.WriteString(RimRunnerWireKeys.ConfigManifest.PackageId, setting.PackageId);
            writer.WriteString(RimRunnerWireKeys.ConfigManifest.ModClass, setting.ModClass);
            writer.WriteStartArray(RimRunnerWireKeys.ConfigManifest.Folders);
            foreach (string folder in setting.Folders)
            {
                writer.WriteStringValue(folder);
            }

            writer.WriteEndArray();
            writer.WriteString(RimRunnerWireKeys.ConfigManifest.Layer, setting.Layer);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
