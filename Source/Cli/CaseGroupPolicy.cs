namespace RimRunnerCli;

public static class CaseGroupPolicy
{
    public const string DirectiveKey = "groups";

    public static IReadOnlyList<string>? ModListGroups(ModListDefinition? modList)
    {
        IReadOnlyList<string>? values = modList?.DirectiveList(DirectiveKey);
        if (values == null)
            return null;

        string label = $"mod 名单 {modList!.DirectiveSource(DirectiveKey)} 的 [rimrunner].{DirectiveKey}";
        string[] tokens = [.. values.Select(value => value.Trim())];
        if (tokens.Length == 0 || tokens.Any(token => token.Length == 0))
            throw new StoreException($"{label} 为空或含空项");
        if (tokens.Contains(RimRunner.RimRunnerCaseGroups.AllToken, StringComparer.Ordinal))
            throw new StoreException($"{label} 写有 {RimRunner.RimRunnerCaseGroups.AllToken}：{RimRunner.RimRunnerCaseGroups.AllToken} 只在 --group 里表示不按组过滤");
        if (tokens.Select(RimRunner.RimRunnerCaseGroups.ShapeProblem).FirstOrDefault(problem => problem != null) is string shape)
            throw new StoreException($"{label}：{shape}");
        string? duplicate = tokens.GroupBy(token => token, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicate != null)
            throw new StoreException($"{label} 重复写组 {duplicate}");
        return tokens;
    }

    public static IReadOnlyList<string> Check(IReadOnlyList<string> groups, string origin, CaseManifest roster)
    {
        IReadOnlyList<string> known = roster.KnownGroups();
        var faults = new List<string>();
        foreach (string group in groups.Where(group => group != RimRunner.RimRunnerCaseGroups.AllToken))
        {
            if (known.Contains(group, StringComparer.Ordinal))
                continue;
            string? near = known.FirstOrDefault(name => string.Equals(name, group, StringComparison.OrdinalIgnoreCase));
            faults.Add($"{origin} 的组 {group} 无产品用例声明该组" + (near != null ? $"（只差大小写：{near}）" : "")
                       + "；已有的组：" + (known.Count == 0 ? "（没有用例标组）" : string.Join("、", known)));
        }

        return faults;
    }

    public static string? EmptyBatch(StructuredRunRequest request, CaseManifest roster)
    {
        if (request.Groups.Count == 0)
            return null;
        if (roster.SelectedBy(request).Any(entry => !entry.EngineOwned && !entry.EnvCheck))
            return null;

        var excluded = new HashSet<string>(request.ExcludeIds, StringComparer.Ordinal);
        List<CaseManifest.CaseEntry> members =
            [.. roster.Cases.Where(entry => !entry.EngineOwned && !entry.EnvCheck && entry.InGroups(request.Groups))];
        int readouts = members.Count(entry => !TextureTierPolicy.InBatch(request, entry));
        int excludedCount = members.Count(entry => TextureTierPolicy.InBatch(request, entry) && excluded.Contains(entry.Id));
        string[] parts =
        [
            .. readouts > 0 ? [$"{readouts} 条是读数用例（只在点名时执行）"] : Array.Empty<string>(),
            .. excludedCount > 0 ? [$"{excludedCount} 条在排除名单里"] : Array.Empty<string>(),
        ];
        return $"按组 {string.Join("、", request.Groups)} 选出的产品用例为 0 条"
               + (parts.Length > 0 ? $"：组里 {members.Count} 条产品用例，" + string.Join("、", parts) : "");
    }
}
