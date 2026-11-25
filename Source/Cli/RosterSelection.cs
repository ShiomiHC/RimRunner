namespace RimRunnerCli;

public static class RosterSelection
{
    public static IReadOnlyList<string> Check(IReadOnlyList<string> ids, CaseManifest roster,
        ModListDefinition? modList)
    {
        var known = roster.Cases.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        var faults = new List<string>();
        var others = new Lazy<IReadOnlyList<ModListDefinition.Listed>>(() => ModListDefinition.LoadAll(modList!.ModListsDir));
        foreach (string id in ids)
        {
            if (!known.TryGetValue(id, out CaseManifest.CaseEntry? entry))
            {
                faults.Add(Unknown("未知用例 Id：", id, known.Keys, FindCases));
                continue;
            }

            if (MissingPackages(entry.RequiresPackages, modList) is { } missing)
            {
                faults.Add($"用例 {id} 依赖的包不在 mod 名单 {modList!.Name} 里：{string.Join("、", missing)}"
                    + Carriers(entry.RequiresPackages, modList, others.Value));
            }
        }

        return faults;
    }

    public static IReadOnlyList<string> CheckExcluded(IEnumerable<string> ids, CaseManifest roster)
    {
        var known = roster.Cases.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        var faults = new List<string>();
        foreach (string id in ids)
        {
            if (!known.ContainsKey(id))
            {
                faults.Add(Unknown("排除名单（excluded.toml）含未知用例 Id：", id, known.Keys, FindCases));
            }
        }

        return faults;
    }

    public static IReadOnlyList<string> CheckShowcase(string id, ShowcaseRoster.Roster roster,
        ModListDefinition? modList)
    {
        ShowcaseRoster.Entry? entry = roster.Showcases.FirstOrDefault(item =>
            string.Equals(item.Id, id, StringComparison.Ordinal));
        if (entry == null)
        {
            return [Unknown("展台名册里没有场面 Id：", id, roster.Showcases.Select(item => item.Id),
                "场面 Id 见 rimrunner showcase list")];
        }

        return MissingPackages(entry.RequiresPackages, modList) is { } missing
            ? [$"场面 {id} 依赖的包不在 mod 名单 {modList!.Name} 里：{string.Join("、", missing)}"
               + Carriers(entry.RequiresPackages, modList, ModListDefinition.LoadAll(modList.ModListsDir))]
            : [];
    }

    private static string Carriers(IReadOnlyList<string> required, ModListDefinition modList,
        IReadOnlyList<ModListDefinition.Listed> all)
    {
        string[] carriers =
        [
            .. all
                .Where(item => item.Definition is { } other
                               && !other.Name.Equals(modList.Name, StringComparison.OrdinalIgnoreCase)
                               && MissingPackages(required, other) == null)
                .Select(item => item.Name)
        ];
        return carriers.Length > 0
            ? $"；依赖齐全的名单：{string.Join(" / ", carriers)}"
            : "；名单目录里没有依赖齐全的名单";
    }

    private const string FindCases = "按关键词查用例：rimrunner cases find <词…>";
    private const int NearShown = 4;
    private const int NearLength = 4;

    private static string Unknown(string lead, string id, IEnumerable<string> known, string lookup)
    {
        string[] names = [.. known];
        string? sameButCase = names.FirstOrDefault(name => string.Equals(name, id, StringComparison.OrdinalIgnoreCase));
        if (sameButCase != null)
        {
            return $"{lead}{id}（只差大小写：{sameButCase}）";
        }

        string[] near =
        [
            .. names.Where(name => Math.Min(name.Length, id.Length) >= NearLength
                                   && (name.Contains(id, StringComparison.OrdinalIgnoreCase)
                                       || id.Contains(name, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(name => Math.Abs(name.Length - id.Length)).ThenBy(name => name, StringComparer.Ordinal)
        ];
        if (near.Length == 0)
        {
            return $"{lead}{id}（{lookup}）";
        }

        string rest = near.Length > NearShown ? "，" + FindingPresentation.Omitted(near.Length - NearShown, "个", null) : "";
        return $"{lead}{id}（相近的 Id：{string.Join("、", near.Take(NearShown))}{rest}）";
    }

    private static IReadOnlyList<string>? MissingPackages(IReadOnlyList<string> required, ModListDefinition? modList)
    {
        if (modList == null || required.Count == 0)
        {
            return null;
        }

        var listed = new HashSet<string>(modList.ModIds, StringComparer.OrdinalIgnoreCase);
        List<string> missing = [.. required.Where(package => !listed.Contains(package))];
        return missing.Count == 0 ? null : missing;
    }
}
