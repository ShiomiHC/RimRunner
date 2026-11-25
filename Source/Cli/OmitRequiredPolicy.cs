namespace RimRunnerCli;

public static class OmitRequiredPolicy
{
    public const string DirectiveKey = "omit_required";

    public static IReadOnlyList<string> Resolve(
        string? modListName, IReadOnlyList<string>? directiveValues, IReadOnlyList<string> requiredIds,
        IReadOnlyList<string> modListIds, Action<string>? log = null, string? ownModListName = null)
    {
        if (directiveValues == null)
        {
            return [];
        }

        if (directiveValues.Count == 0)
        {
            throw new StoreException(
                $"mod 名单 {modListName} 的 [rimrunner].{DirectiveKey} 没有任何 packageId");
        }

        var wanted = new List<string>(directiveValues.Count);
        foreach (string raw in directiveValues)
        {
            string trimmed = raw.Trim();
            if (trimmed.Length == 0)
            {
                throw new StoreException(
                    $"mod 名单 {modListName} 的 [rimrunner].{DirectiveKey} 含空项");
            }

            wanted.Add(trimmed);
        }

        var omitted = new List<string>();
        foreach (string id in wanted)
        {
            string? match = requiredIds.FirstOrDefault(
                required => required.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? throw new StoreException(
                    $"mod 名单 {modListName} 的 [rimrunner].{DirectiveKey} 指定 {id}，该 packageId 不在必需集合里（非可选 units 与 requiredMods）"
                    + $"（现有：{string.Join("、", requiredIds)}）");
            if (modListIds.Any(listed => listed.Equals(id, StringComparison.OrdinalIgnoreCase)))
            {
                bool inherited = ownModListName != null
                                 && !string.Equals(ownModListName, modListName, StringComparison.OrdinalIgnoreCase);
                throw new StoreException(inherited
                    ? $"mod 名单 {ownModListName} 从 {modListName} 继承的 [rimrunner].{DirectiveKey} 豁免 {id}，而生效清单的正文里有 {id}"
                    : $"mod 名单 {modListName} 的 [rimrunner].{DirectiveKey} 豁免 {id}，而生效清单的正文里有 {id}");
            }
            if (!omitted.Contains(match))
            {
                omitted.Add(match);
            }
        }

        log?.Invoke($"[冷启动] mod 名单 {modListName} 声明豁免必需 packageId：{string.Join("、", omitted)}");
        return omitted;
    }
}
