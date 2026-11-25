using System.Text.Json;

namespace RimRunnerCli;

public sealed class CaseManifest
{
    private static class Keys
    {
        internal const string GeneratedAt = RimRunner.RimRunnerDataLayout.ManifestKeys.GeneratedAt;
        internal const string Cases = RimRunner.RimRunnerDataLayout.ManifestKeys.Cases;
        internal const string AssemblyMvids = RimRunner.RimRunnerDataLayout.ManifestKeys.AssemblyMvids;
        internal const string CaseId = RimRunner.RimRunnerDataLayout.ManifestKeys.CaseId;
        internal const string CaseKind = RimRunner.RimRunnerDataLayout.ManifestKeys.CaseKind;
        internal const string CaseMinTextures = RimRunner.RimRunnerDataLayout.ManifestKeys.CaseMinTextures;
        internal const string CaseQuietMachine = RimRunner.RimRunnerDataLayout.ManifestKeys.CaseQuietMachine;
        internal const string CaseShardable = RimRunner.RimRunnerDataLayout.ManifestKeys.CaseShardable;
        internal const string CaseEnvCheck = RimRunner.RimRunnerDataLayout.ManifestKeys.CaseEnvCheck;
        internal const string CaseType = RimRunner.RimRunnerDataLayout.ManifestKeys.CaseType;
        internal const string CaseRequiresPackages = RimRunner.RimRunnerDataLayout.ManifestKeys.CaseRequiresPackages;
        internal const string CaseGroups = RimRunner.RimRunnerDataLayout.ManifestKeys.CaseGroups;
        internal const string CaseEngineOwned = RimRunner.RimRunnerDataLayout.ManifestKeys.CaseEngineOwned;
    }

    public sealed class CaseEntry
    {
        public string Id { get; init; } = "";
        public string Kind { get; init; } = "";
        public string MinTextures { get; init; } = RimRunner.RimRunnerEngineArgs.TextureTierPlaceholder;
        public bool QuietMachine { get; init; }
        public bool Shardable { get; init; }
        public bool EnvCheck { get; init; }
        public string Type { get; init; } = "";
        public IReadOnlyList<string> RequiresPackages { get; init; } = [];
        public IReadOnlyList<string> Groups { get; init; } = [];
        public bool EngineOwned { get; init; }

        public bool InGroups(IReadOnlyCollection<string> groups) =>
            groups.Count == 0 || EngineOwned || Groups.Any(groups.Contains);
    }

    public string Path { get; private init; } = "";
    public string GeneratedAt { get; private init; } = "";
    public IReadOnlyList<CaseEntry> Cases { get; private init; } = [];
    public IReadOnlyDictionary<string, string>? AssemblyMvids { get; private init; }

    public static string PathUnder(string productRoot, OrchestratorDescriptor descriptor) =>
        System.IO.Path.Combine(descriptor.ResolveOutputDir(productRoot),
            RimRunner.RimRunnerDataLayout.IndexDirName, RimRunner.RimRunnerDataLayout.CaseManifestFileName);

    public static CaseManifest? TryLoad(string productRoot, OrchestratorDescriptor descriptor) =>
        TryLoadFile(PathUnder(productRoot, descriptor));

    public static CaseManifest? TryLoadFile(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return Parse(File.ReadAllText(path), path);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static CaseManifest? TryParse(string text, string path)
    {
        try
        {
            return Parse(text, path);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static CaseManifest Parse(string text, string path)
    {
        using JsonDocument document = StrictJson.ParseObject(text);
        JsonElement root = document.RootElement;
        return new CaseManifest
        {
            Path = path,
            GeneratedAt = StringOf(root, Keys.GeneratedAt),
            Cases = FirstPerId(path, CasesOf(root)),
            AssemblyMvids = MvidsOf(root),
        };
    }

    public IEnumerable<CaseEntry> SelectedBy(StructuredRunRequest request)
    {
        if (request.IsNamed)
        {
            var named = new HashSet<string>(request.CaseIds, System.StringComparer.Ordinal);
            return Cases.Where(entry => named.Contains(entry.Id));
        }

        var excluded = new HashSet<string>(request.ExcludeIds, System.StringComparer.Ordinal);
        return Cases.Where(entry => !excluded.Contains(entry.Id)
            && TextureTierPolicy.InBatch(request, entry)
            && (entry.EnvCheck || entry.InGroups(request.Groups)));
    }

    public IReadOnlyList<string> KnownGroups() =>
    [
        .. Cases.Where(entry => !entry.EngineOwned).SelectMany(entry => entry.Groups)
            .Distinct(System.StringComparer.Ordinal).OrderBy(group => group, System.StringComparer.Ordinal)
    ];

    private static string StringOf(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";

    private static IReadOnlyList<string> StringsOf(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString() ?? "")
                    .ToList()
                : System.Array.Empty<string>();

    private static IReadOnlyList<CaseEntry> CasesOf(JsonElement root)
    {
        if (!root.TryGetProperty(Keys.Cases, out JsonElement cases)
            || cases.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return
        [
            .. cases.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object)
                .Select(item => new CaseEntry
                {
                    Id = StringOf(item, Keys.CaseId),
                    Kind = StringOf(item, Keys.CaseKind),
                    MinTextures = StringOf(item, Keys.CaseMinTextures) is { Length: > 0 } floor
                        ? floor
                        : RimRunner.RimRunnerEngineArgs.TextureTierPlaceholder,
                    QuietMachine = BoolOf(item, Keys.CaseQuietMachine),
                    Shardable = BoolOf(item, Keys.CaseShardable),
                    EnvCheck = BoolOf(item, Keys.CaseEnvCheck),
                    Type = StringOf(item, Keys.CaseType),
                    RequiresPackages = StringsOf(item, Keys.CaseRequiresPackages),
                    Groups = StringsOf(item, Keys.CaseGroups),
                    EngineOwned = BoolOf(item, Keys.CaseEngineOwned),
                })
        ];
    }

    private static HashSet<string> ProcessWarnedDuplicates { get; } = new(System.StringComparer.Ordinal);

    private static HashSet<string> WarnedDuplicates => CommandContext.Current?.NotedOnce ?? ProcessWarnedDuplicates;

    private static IReadOnlyList<CaseEntry> FirstPerId(string path, IReadOnlyList<CaseEntry> cases)
    {
        CliBatchWarnings.Checked(RimRunner.RimRunnerWarningKey.RosterDuplicateId);
        string[] duplicated =
        [
            .. cases
                .GroupBy(entry => entry.Id, System.StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
        ];
        if (duplicated.Length == 0)
            return cases;

        lock (WarnedDuplicates)
        {
            if (WarnedDuplicates.Add("roster-duplicate:" + path.ToUpperInvariant()))
            {
                string text = $"用例名册里 Id 重复：{string.Join("、", duplicated)}，只取首条：{path}";
                System.Console.WriteLine("[WARN] " + text);
                CliBatchWarnings.Add(RimRunner.RimRunnerWarningKey.RosterDuplicateId, text);
                System.Console.WriteLine("       名册在冷启动前由编译产物生成");
            }
        }

        return
        [
            .. cases
                .GroupBy(entry => entry.Id, System.StringComparer.Ordinal)
                .Select(group => group.First())
        ];
    }

    private static IReadOnlyDictionary<string, string>? MvidsOf(JsonElement root)
    {
        if (!root.TryGetProperty(Keys.AssemblyMvids, out JsonElement value)
            || value.ValueKind != JsonValueKind.Object)
            return null;
        return value.EnumerateObject()
            .Where(property => property.Value.ValueKind == JsonValueKind.String)
            .ToDictionary(property => property.Name, property => property.Value.GetString() ?? "",
                System.StringComparer.Ordinal);
    }

    private static bool BoolOf(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.True;
}
