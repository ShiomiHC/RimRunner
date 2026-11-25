namespace RimRunner;

public sealed class RimRunnerCaseSelector
{
    public string[] CaseIds { get; }

    public bool IsNamed => CaseIds.Length > 0;

    public string[] Groups { get; }

    public string[] ExcludeIds { get; }

    public EnvCheckMode EnvChecks { get; }

    public enum EnvCheckMode
    {
        Off,

        Allowed,

        Only,
    }

    private RimRunnerCaseSelector(
        string[] caseIds, string[] groups, string[] excludeIds, EnvCheckMode envChecks)
    {
        CaseIds = caseIds;
        Groups = groups;
        ExcludeIds = excludeIds;
        EnvChecks = envChecks;
    }

    internal static EnvCheckMode? EnvCheckModeOf(bool checks, bool checksOnly)
    {
        return (checks, checksOnly) switch
        {
            (true, true) => null,
            (true, false) => EnvCheckMode.Allowed,
            (false, true) => EnvCheckMode.Only,
            _ => EnvCheckMode.Off,
        };
    }

    internal static RimRunnerCaseSelector? Create(
        bool batch, IEnumerable<string>? caseIds, IEnumerable<string>? groups,
        IEnumerable<string>? excludeIds, EnvCheckMode envChecks, out string? error)
    {
        string[] resolvedCases = NormalizeIds(caseIds);
        string[] resolvedGroups = RimRunnerCaseGroups.Normalize(groups);
        string[] resolvedExclude = NormalizeIds(excludeIds);
        error = null;

        if (resolvedCases.Any(string.IsNullOrEmpty))
        {
            error = "指定的用例 Id 不得为空";
        }
        else if (!batch && resolvedCases.Length == 0)
        {
            error = "既未指定用例，也未选批";
        }
        else if (batch && resolvedCases.Length > 0)
        {
            error = "case 与 batch 不能同时传入";
        }
        else if (!batch && (resolvedGroups.Length > 0 || resolvedExclude.Length > 0 || envChecks == EnvCheckMode.Only))
        {
            error = "group、exclude 与 checksOnly 只作用于批部分，只指定用例时没有批部分";
        }
        else if (envChecks == EnvCheckMode.Only && resolvedGroups.Length > 0)
        {
            error = "checksOnly 的批部分只有环境体检，环境体检不受组过滤，group 不起作用";
        }
        else if (resolvedGroups.Select(RimRunnerCaseGroups.ShapeProblem).FirstOrDefault(problem => problem != null)
                 is string shape)
        {
            error = shape;
        }

        return error != null
            ? null
            : new RimRunnerCaseSelector(resolvedCases, resolvedGroups, resolvedExclude, envChecks);
    }

    internal static RimRunnerCaseSelector ForBatch(
        string[]? excludeIds = null, EnvCheckMode envChecks = EnvCheckMode.Off, string[]? groups = null)
    {
        return Create(true, null, groups, excludeIds, envChecks, out string? error)
               ?? throw new ArgumentException(error);
    }

    internal static RimRunnerCaseSelector ForCase(IEnumerable<string> caseIds, bool envChecks = false)
    {
        string[] resolved = NormalizeIds(caseIds);
        if (resolved.Length == 0)
        {
            throw new ArgumentException("指定的用例 Id 不得为空", nameof(caseIds));
        }

        return Create(false, resolved, null, null, envChecks ? EnvCheckMode.Allowed : EnvCheckMode.Off, out string? error)
               ?? throw new ArgumentException(error, nameof(caseIds));
    }

    internal static RimRunnerCaseSelector ForCase(string caseId)
    {
        return ForCase([caseId]);
    }

    private const string LabelPrefix = "select:";
    private const string BatchSegment = "batch";
    private const string EnvChecksSegment = "checks";
    private const string EnvChecksOnlySegment = "checks-only";

    internal static RimRunnerCaseSelector? FromCommandString(string? command)
    {
        if (string.IsNullOrEmpty(command))
        {
            return null;
        }

        return command.StartsWith(LabelPrefix, StringComparison.Ordinal)
            ? FromCanonicalLabel(command[LabelPrefix.Length..])
            : null;
    }

    private static RimRunnerCaseSelector? FromCanonicalLabel(string body)
    {
        if (body.Length == 0)
        {
            return null;
        }

        bool batch = false;
        string[]? excludeIds = null;
        string[]? caseIds = null;
        string[]? groups = null;
        bool envChecks = false;
        bool envChecksOnly = false;

        foreach (string segment in body.Split(';'))
        {
            if (segment.Length == 0)
            {
                return null;
            }

            if (string.Equals(segment, BatchSegment, StringComparison.Ordinal))
            {
                if (batch) return null;
                batch = true;
                continue;
            }

            if (string.Equals(segment, EnvChecksSegment, StringComparison.Ordinal))
            {
                if (envChecks) return null;
                envChecks = true;
                continue;
            }

            if (string.Equals(segment, EnvChecksOnlySegment, StringComparison.Ordinal))
            {
                if (envChecksOnly) return null;
                envChecksOnly = true;
                continue;
            }

            int split = segment.IndexOf('=');
            if (split <= 0 || split == segment.Length - 1)
            {
                return null;
            }

            string key = segment[..split];
            string value = segment[(split + 1)..];
            switch (key)
            {
                case "case":
                    if (caseIds != null) return null;
                    caseIds = ParseIdList(value);
                    if (caseIds == null) return null;
                    break;
                case "exclude":
                    if (excludeIds != null) return null;
                    excludeIds = ParseIdList(value);
                    if (excludeIds == null) return null;
                    break;
                case "group":
                    if (groups != null) return null;
                    groups = ParseIdList(value);
                    if (groups == null) return null;
                    break;
                default:
                    return null;
            }
        }

        if (excludeIds != null && UnknownExcludeId(excludeIds) != null)
        {
            return null;
        }

        if (groups != null && UnknownGroup(groups) != null)
        {
            return null;
        }

        return EnvCheckModeOf(envChecks, envChecksOnly) is EnvCheckMode mode
            ? Create(batch, caseIds, groups, excludeIds, mode, out _)
            : null;
    }

    private static string[]? ParseIdList(string value)
    {
        var parsed = new List<string>();
        foreach (string token in value.Split(','))
        {
            string id = token.Trim();
            if (id.Length == 0)
            {
                return null;
            }

            parsed.Add(id);
        }

        return parsed.Count == 0 ? null : NormalizeIds(parsed);
    }

    private static string[] NormalizeIds(IEnumerable<string>? ids)
    {
        return ids == null
            ? []
            : ids.Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
    }

    private static string? UnknownExcludeId(IEnumerable<string> ids)
    {
        HashSet<string>? absentIds = null;
        if (RimRunnerRegistry.AbsentTypes.Count > 0)
        {
            absentIds = RimRunnerRosterCheck.IdsOfTypes(RimRunnerRegistry.UninstantiatedTypes);
            if (absentIds == null)
            {
                return null;
            }
        }

        foreach (string id in ids)
        {
            if (RimRunnerRegistry.FindCase(id) == null && !(absentIds?.Contains(id) ?? false))
            {
                return id;
            }
        }

        return null;
    }

    internal static string? UnknownGroup(IEnumerable<string> groups)
    {
        HashSet<string> known = KnownGroups(out bool rosterUnreadable);
        if (rosterUnreadable)
        {
            return null;
        }

        return groups.FirstOrDefault(group => !known.Contains(group));
    }

    internal static HashSet<string> KnownGroups(out bool rosterUnreadable)
    {
        rosterUnreadable = false;
        HashSet<string> known = new(StringComparer.Ordinal);
        foreach (RimRunnerTestCase testCase in RimRunnerRegistry.All.Where(GroupFiltered))
        {
            known.UnionWith(GroupsOf(testCase));
        }

        if (RimRunnerRegistry.AbsentTypes.Count > 0)
        {
            HashSet<string>? absentGroups = RimRunnerRosterCheck.GroupsOfTypes(RimRunnerRegistry.UninstantiatedTypes);
            if (absentGroups == null)
            {
                rosterUnreadable = true;
            }
            else
            {
                known.UnionWith(absentGroups);
            }
        }

        return known;
    }

    internal static string GroupUnknownError(string unknownGroup)
    {
        HashSet<string> known = KnownGroups(out _);
        string? near = known.FirstOrDefault(group => string.Equals(group, unknownGroup, StringComparison.OrdinalIgnoreCase));
        return "group 指定的组无用例声明：" + unknownGroup + (near != null ? "（只差大小写：" + near + "）" : "")
               + "。已有的组：" + (known.Count == 0 ? "（无用例声明组）" : string.Join("、", known.OrderBy(group => group, StringComparer.Ordinal)));
    }

    internal static string[] GroupsOf(RimRunnerTestCase testCase)
    {
        return GroupsOf(testCase.GetType());
    }

    internal static string[] GroupsOf(Type type)
    {
        lock (GroupCache)
        {
            if (!GroupCache.TryGetValue(type, out string[]? groups))
            {
                groups = RimRunnerCaseGroups.Normalize(type.GetCustomAttributes(typeof(RimRunnerGroupAttribute), true)
                    .Cast<RimRunnerGroupAttribute>().Select(attribute => attribute.Group));
                GroupCache[type] = groups;
            }

            return groups;
        }
    }

    private static readonly Dictionary<Type, string[]> GroupCache = [];

    internal static bool GroupFiltered(RimRunnerTestCase testCase)
    {
        return !RimRunnerDefScope.IsEngineSide(testCase.GetType().Assembly);
    }

    internal static string ExcludeUnknownError(string unknownId)
    {
        return "exclude 含未知用例 Id：" + unknownId + "。用例名册共 "
               + RimRunnerRegistry.All.Length + " 条"
               + RimRunnerPackages.AbsentNote(RimRunnerRegistry.AbsentTypes, "用例类");
    }

    internal string ToCanonicalLabel()
    {
        var segments = new List<string>();
        if (IsNamed)
        {
            segments.Add("case=" + string.Join(",", CaseIds));
        }
        else
        {
            segments.Add(BatchSegment);
        }

        if (Groups.Length > 0)
        {
            segments.Add("group=" + string.Join(",", Groups));
        }

        if (ExcludeIds.Length > 0)
        {
            segments.Add("exclude=" + string.Join(",", ExcludeIds));
        }

        if (EnvChecks == EnvCheckMode.Allowed)
        {
            segments.Add(EnvChecksSegment);
        }
        else if (EnvChecks == EnvCheckMode.Only)
        {
            segments.Add(EnvChecksOnlySegment);
        }

        return LabelPrefix + string.Join(";", segments);
    }

    internal string ArchiveTag()
    {
        if (IsNamed)
        {
            return CaseIds.Length <= MaxTaggedCaseIds
                ? string.Join("+", CaseIds)
                : string.Join("+", CaseIds.Take(MaxTaggedCaseIds - 1)) + "+等" + CaseIds.Length + "条";
        }

        return BatchSegment + (Groups.Length > 0 ? "-grp" : "");
    }

    private const int MaxTaggedCaseIds = 4;

    internal List<RimRunnerTestCase> ResolveCases()
    {
        HashSet<string> excluded = new(ExcludeIds, StringComparer.Ordinal);
        List<RimRunnerTestCase> main = MainSet(excluded);
        return EnvCheckSet(main, excluded).Concat(main).ToList();
    }

    internal List<RimRunnerTestCase> ResolveEnvChecks()
    {
        HashSet<string> excluded = new(ExcludeIds, StringComparer.Ordinal);
        return EnvCheckSet(MainSet(excluded), excluded).ToList();
    }

    private List<RimRunnerTestCase> MainSet(HashSet<string> excluded)
    {
        if (IsNamed)
        {
            HashSet<string> named = new(CaseIds, StringComparer.Ordinal);
            return RimRunnerRegistry.All.Where(testCase => named.Contains(testCase.Id)).ToList();
        }

        return RimRunnerRegistry.All.Where(testCase =>
            EnvChecks != EnvCheckMode.Only && IsBatchMember(testCase) && InGroups(testCase) && !excluded.Contains(testCase.Id)).ToList();
    }

    private bool InGroups(RimRunnerTestCase testCase)
    {
        return Groups.Length == 0 || !GroupFiltered(testCase) || GroupsOf(testCase).Any(group => Groups.Contains(group));
    }

    internal bool MergesEnvChecks(IReadOnlyCollection<RimRunnerTestCase> main)
    {
        return EnvChecks == EnvCheckMode.Only
               || (EnvChecks == EnvCheckMode.Allowed && main.Any(testCase =>
                   testCase.Level == RimRunnerLevel.World && !RimRunnerEnvChecks.IsEnvCheck(testCase)));
    }

    private IEnumerable<RimRunnerTestCase> EnvCheckSet(List<RimRunnerTestCase> main, HashSet<string> excluded)
    {
        if (!MergesEnvChecks(main))
        {
            return [];
        }

        return RimRunnerRegistry.All.Where(testCase =>
            RimRunnerEnvChecks.IsEnvCheck(testCase) && !excluded.Contains(testCase.Id) && !main.Contains(testCase));
    }

    internal IEnumerable<RimRunnerTestCase> BatchExcludedCases()
    {
        if (ExcludeIds.Length == 0)
        {
            return [];
        }

        HashSet<string> excluded = new(ExcludeIds, StringComparer.Ordinal);

        List<RimRunnerTestCase> main = MainSet(excluded);
        IEnumerable<RimRunnerTestCase> envChecks = MergesEnvChecks(main)
            ? RimRunnerRegistry.All.Where(testCase =>
                RimRunnerEnvChecks.IsEnvCheck(testCase) && excluded.Contains(testCase.Id)
                && !main.Contains(testCase))
            : [];

        if (EnvChecks == EnvCheckMode.Only)
        {
            return envChecks;
        }

        return envChecks.Concat(RimRunnerRegistry.All.Where(testCase =>
            IsBatchMember(testCase) && InGroups(testCase) && excluded.Contains(testCase.Id)));
    }

    internal IEnumerable<RimRunnerTestCase> GroupSkippedCases()
    {
        if (Groups.Length == 0)
        {
            return [];
        }

        HashSet<string> excluded = new(ExcludeIds, StringComparer.Ordinal);
        return RimRunnerRegistry.All.Where(testCase =>
            IsBatchMember(testCase) && !InGroups(testCase) && !excluded.Contains(testCase.Id));
    }

    internal static bool IsBatchMember(RimRunnerTestCase testCase)
    {
        return testCase.Kind == RimRunnerKind.Regression && !RimRunnerEnvChecks.IsEnvCheck(testCase);
    }

    internal static RimRunnerCaseSelector? TryParse(
        bool batch, IReadOnlyList<string>? cases, IReadOnlyList<string>? groups,
        IReadOnlyList<string>? exclude, EnvCheckMode envChecks, out string? error)
    {
        string[]? excludeIds = null;
        string[]? caseIds = null;
        error = null;

        if (exclude != null)
        {
            if (exclude.Count == 0 || exclude.Any(id => id.Length == 0))
            {
                error = "exclude 为空数组或含空 Id";
                return null;
            }

            excludeIds = NormalizeIds(exclude);

            string? unknownId = UnknownExcludeId(excludeIds);
            if (unknownId != null)
            {
                error = ExcludeUnknownError(unknownId);
                return null;
            }
        }

        if (groups != null)
        {
            if (groups.Count == 0 || groups.Any(group => group.Length == 0))
            {
                error = "group 为空数组或含空组名";
                return null;
            }

            if (groups.Select(RimRunnerCaseGroups.ShapeProblem).FirstOrDefault(problem => problem != null) is string shape)
            {
                error = "group：" + shape;
                return null;
            }

            string? unknownGroup = UnknownGroup(groups);
            if (unknownGroup != null)
            {
                error = GroupUnknownError(unknownGroup);
                return null;
            }
        }

        if (cases != null)
        {
            string[] tokens = cases.ToArray();
            if (tokens.Length == 0 || tokens.Any(token => token.Length == 0))
            {
                error = "case 为空数组或含空 Id";
                return null;
            }

            string? duplicate = tokens.GroupBy(token => token, StringComparer.Ordinal)
                .Where(group => group.Count() > 1).Select(group => group.Key).FirstOrDefault();
            if (duplicate != null)
            {
                error = "case 里重复指定 " + duplicate;
                return null;
            }

            string[] unknown = tokens.Where(id => RimRunnerRegistry.FindCase(id) == null).ToArray();
            if (unknown.Length > 0)
            {
                error = "未知用例 Id：" + string.Join("、", unknown) + "。用例名册共 " + RimRunnerRegistry.All.Length
                        + " 条"
                        + RimRunnerPackages.AbsentNote(RimRunnerRegistry.AbsentTypes, "用例类")
                        + RimRunnerEjectedAssemblies.Note;
                return null;
            }

            caseIds = tokens;
        }

        if (!batch && caseIds == null)
        {
            error = "未指定 batch，也未指定 case：batch 为 true 选批，case 给用例 Id 数组，两者只给其一";
            return null;
        }

        return Create(batch, caseIds, groups, excludeIds, envChecks, out error);
    }
}
