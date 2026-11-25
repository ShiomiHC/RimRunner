using System.IO;

namespace RimRunner;

internal static class RimRunnerRosterCheck
{
    internal sealed class Entry
    {
        public required string Id;
        public required string Kind;
        public required string MinTextures;
        public bool QuietMachine;
        public bool Shardable;
        public bool EnvCheck;
        public string? Type;
        public string[] Groups = [];
        public bool EngineOwned;
    }

    internal sealed class Result
    {
        internal readonly List<string> Faults = [];
        internal readonly List<string> Warnings = [];
    }

    internal static Result Compare(
        IReadOnlyList<Entry> registered,
        IEnumerable<string> absentTypes,
        IEnumerable<string> loadedTypes,
        string? rosterText,
        IReadOnlyDictionary<string, string> loadedMvids)
    {
        Result result = new();
        if (rosterText == null)
        {
            result.Faults.Add("无法读取用例名册");
            return result;
        }

        Dictionary<string, object> root;
        try
        {
            root = RimRunnerJson.Parse(rosterText) as Dictionary<string, object>
                   ?? throw new FormatException("顶层不是对象");
        }
        catch (FormatException exception)
        {
            result.Faults.Add("用例名册不成形：" + exception.Message);
            return result;
        }

        if (!root.TryGetValue(RimRunnerDataLayout.ManifestKeys.AssemblyMvids, out object? mvidValue)
            || mvidValue is not Dictionary<string, object> rosterMvids)
        {
            result.Faults.Add("用例名册缺 " + RimRunnerDataLayout.ManifestKeys.AssemblyMvids);
            return result;
        }

        List<string> mismatchedMvids = [];
        foreach (KeyValuePair<string, object> pair in rosterMvids.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            string recorded = pair.Value as string ?? "<非字符串>";
            string instanceMvid = loadedMvids.TryGetValue(pair.Key, out string? mvid) ? mvid : "未装载";
            if (!string.Equals(recorded, instanceMvid, StringComparison.Ordinal))
                mismatchedMvids.Add(pair.Key + "（名册 " + recorded + "、实例 " + instanceMvid + "）");
        }

        if (mismatchedMvids.Count > 0)
        {
            result.Warnings.Add("名册记录的程序集与本实例装载的不匹配，跳过逐字段核对："
                                + string.Join("、", mismatchedMvids));
            return result;
        }

        if (!root.TryGetValue(RimRunnerDataLayout.ManifestKeys.Cases, out object? casesValue)
            || casesValue is not List<object> caseList)
        {
            result.Faults.Add("用例名册缺 " + RimRunnerDataLayout.ManifestKeys.Cases + " 数组");
            return result;
        }

        var rosterById = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        foreach (Dictionary<string, object> fields in caseList.OfType<Dictionary<string, object>>())
        {
            string id = StringOf(fields, RimRunnerDataLayout.ManifestKeys.CaseId);
            if (id.Length > 0)
                rosterById.TryAdd(id, fields);
        }

        HashSet<string> instantiated = new(StringComparer.Ordinal);
        foreach (Entry entry in registered)
        {
            instantiated.Add(entry.Id);
            if (!rosterById.TryGetValue(entry.Id, out Dictionary<string, object>? fields))
            {
                result.Faults.Add("用例 " + entry.Id + "（" + (entry.Type ?? "?") + "）不在名册里");
                continue;
            }

            List<string> mismatched = [];
            CompareField("kind", entry.Kind,
                StringOf(fields, RimRunnerDataLayout.ManifestKeys.CaseKind), mismatched);
            CompareField("minTextures", entry.MinTextures,
                StringOf(fields, RimRunnerDataLayout.ManifestKeys.CaseMinTextures), mismatched);
            CompareField("quietMachine", BoolText(entry.QuietMachine),
                BoolText(BoolOf(fields, RimRunnerDataLayout.ManifestKeys.CaseQuietMachine)), mismatched);
            CompareField("shardable", BoolText(entry.Shardable),
                BoolText(BoolOf(fields, RimRunnerDataLayout.ManifestKeys.CaseShardable)), mismatched);
            CompareField("envCheck", BoolText(entry.EnvCheck),
                BoolText(BoolOf(fields, RimRunnerDataLayout.ManifestKeys.CaseEnvCheck)), mismatched);
            CompareField("groups", string.Join(",", entry.Groups),
                string.Join(",", StringsOf(fields, RimRunnerDataLayout.ManifestKeys.CaseGroups)), mismatched);
            CompareField("engineOwned", BoolText(entry.EngineOwned),
                BoolText(BoolOf(fields, RimRunnerDataLayout.ManifestKeys.CaseEngineOwned)), mismatched);
            if (mismatched.Count > 0)
                result.Faults.Add("用例 " + entry.Id + " 与名册不匹配：" + string.Join("；", mismatched));
        }

        var loaded = new HashSet<string>(loadedTypes, StringComparer.Ordinal);
        var absent = new HashSet<string>(absentTypes, StringComparer.Ordinal);
        HashSet<string> reported = new(StringComparer.Ordinal);
        foreach (Dictionary<string, object> fields in caseList.OfType<Dictionary<string, object>>())
        {
            string id = StringOf(fields, RimRunnerDataLayout.ManifestKeys.CaseId);
            string type = StringOf(fields, RimRunnerDataLayout.ManifestKeys.CaseType);
            if (id.Length == 0 || type.Length == 0 || !loaded.Contains(type) || absent.Contains(type))
            {
                continue;
            }

            if (instantiated.Contains(id) || !reported.Add(id))
            {
                continue;
            }

            result.Faults.Add("名册条目 " + id + "（" + type + "）的类已装载、未因包缺席而跳过实例化，本实例却没有实例化出这个 Id");
        }

        return result;
    }

    internal static HashSet<string>? IdsOfTypes(string? rosterText, IEnumerable<string> types)
    {
        if (rosterText == null)
        {
            return null;
        }

        Dictionary<string, object> root;
        try
        {
            root = RimRunnerJson.Parse(rosterText) as Dictionary<string, object>
                   ?? throw new FormatException("顶层不是对象");
        }
        catch (FormatException)
        {
            return null;
        }

        if (!root.TryGetValue(RimRunnerDataLayout.ManifestKeys.Cases, out object? casesValue)
            || casesValue is not List<object> caseList)
        {
            return null;
        }

        var wanted = new HashSet<string>(types, StringComparer.Ordinal);
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (Dictionary<string, object> fields in caseList.OfType<Dictionary<string, object>>())
        {
            string id = StringOf(fields, RimRunnerDataLayout.ManifestKeys.CaseId);
            string type = StringOf(fields, RimRunnerDataLayout.ManifestKeys.CaseType);
            if (id.Length > 0 && wanted.Contains(type))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    internal static HashSet<string>? IdsOfTypes(IEnumerable<string> types)
    {
        string? text = ReadRoster();
        return text == null ? null : IdsOfTypes(text, types);
    }

    internal static HashSet<string>? GroupsOfTypes(string? rosterText, IEnumerable<string> types)
    {
        if (CaseListOf(rosterText) is not { } caseList)
        {
            return null;
        }

        var wanted = new HashSet<string>(types, StringComparer.Ordinal);
        HashSet<string> groups = new(StringComparer.Ordinal);
        foreach (Dictionary<string, object> fields in caseList.OfType<Dictionary<string, object>>())
        {
            if (wanted.Contains(StringOf(fields, RimRunnerDataLayout.ManifestKeys.CaseType))
                && !BoolOf(fields, RimRunnerDataLayout.ManifestKeys.CaseEngineOwned))
            {
                groups.UnionWith(StringsOf(fields, RimRunnerDataLayout.ManifestKeys.CaseGroups));
            }
        }

        return groups;
    }

    internal static HashSet<string>? GroupsOfTypes(IEnumerable<string> types)
    {
        string? text = ReadRoster();
        return text == null ? null : GroupsOfTypes(text, types);
    }

    private static string? ReadRoster()
    {
        try
        {
            return File.ReadAllText(RimRunnerPaths.CaseManifestFile);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static List<object>? CaseListOf(string? rosterText)
    {
        if (rosterText == null)
        {
            return null;
        }

        try
        {
            return RimRunnerJson.Parse(rosterText) is Dictionary<string, object> root
                   && root.TryGetValue(RimRunnerDataLayout.ManifestKeys.Cases, out object? casesValue)
                ? casesValue as List<object>
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    internal static Result Resolve(
        IReadOnlyList<RimRunnerTestCase> cases,
        IEnumerable<Type> candidateTypes,
        IEnumerable<string> absentTypes)
    {
        List<Entry> registered = cases.Select(testCase => new Entry
        {
            Id = testCase.Id,
            Kind = testCase.Kind.ToString(),
            MinTextures = RimRunnerTextureTier.Name(testCase.MinTextures),
            QuietMachine = testCase.DeclaresQuietMachine,
            Shardable = testCase is IRimRunnerShardable,
            EnvCheck = RimRunnerEnvChecks.IsEnvCheck(testCase),
            Type = testCase.GetType().FullName,
            Groups = RimRunnerCaseSelector.GroupsOf(testCase),
            EngineOwned = !RimRunnerCaseSelector.GroupFiltered(testCase),
        }).ToList();

        string path = RimRunnerPaths.CaseManifestFile;
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Result unreadable = new();
            unreadable.Faults.Add("无法读取用例名册：" + path + "（" + exception.Message + "）");
            return unreadable;
        }

        Result compared = Compare(registered, absentTypes,
            candidateTypes.Select(type => type.FullName!), text, RimRunnerDefScope.OurAssemblyMvids());
        Result result = new();
        result.Faults.AddRange(compared.Faults.Select(fault => "用例名册 " + path + "：" + fault));
        result.Warnings.AddRange(compared.Warnings.Select(warning => "用例名册 " + path + "：" + warning));
        return result;
    }

    private static void CompareField(string name, string actual, string recorded, List<string> mismatched)
    {
        if (string.Equals(actual, recorded, StringComparison.Ordinal))
        {
            return;
        }

        mismatched.Add(name + " 名册=" + Shown(recorded) + "、实例=" + Shown(actual));
    }

    private static string Shown(string value) => value.Length == 0 ? "<缺>" : value;

    private static string BoolText(bool flag) => flag ? "true" : "false";

    private static string StringOf(Dictionary<string, object> fields, string key) =>
        fields.TryGetValue(key, out object? value) && value is string text ? text : "";

    private static bool BoolOf(Dictionary<string, object> fields, string key) =>
        fields.TryGetValue(key, out object? value) && value is true;

    private static IEnumerable<string> StringsOf(Dictionary<string, object> fields, string key) =>
        fields.TryGetValue(key, out object? value) && value is List<object> items ? items.OfType<string>() : [];
}
