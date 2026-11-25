namespace RimRunnerCli;

internal static class ModListListing
{
    internal static string[]? StripBare(string[] args)
    {
        string flag = Switches.Modlist.FlagName;
        for (int index = 0; index < args.Length; index++)
        {
            if (args[index] != flag)
                continue;
            if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
                continue;
            return [.. args[..index], .. args[(index + 1)..]];
        }

        return null;
    }

    internal static IEnumerable<string> Lines(string modListsDir, string? defaultModList)
    {
        yield return $"名单目录：{CommandContext.FullPath(modListsDir)}";
        IReadOnlyList<ModListDefinition.Listed> all = ModListDefinition.LoadAll(modListsDir);
        if (all.Count == 0)
        {
            yield return "  " + ModListDefinition.AvailableNames(modListsDir);
        }
        else
        {
            int width = all.Max(item => item.Name.Length);
            foreach (ModListDefinition.Listed item in all)
            {
                string detail = item.Definition is { } definition ? Describe(definition) : "无法解析：" + item.Error;
                string mark = string.Equals(item.Name, defaultModList, StringComparison.OrdinalIgnoreCase) ? "（缺省）" : "";
                yield return $"  {item.Name.PadRight(width)}  {detail}{mark}";
            }
        }

        if (string.IsNullOrWhiteSpace(defaultModList))
            yield return "描述符未指定缺省名单";
        else if (!all.Any(item => string.Equals(item.Name, defaultModList, StringComparison.OrdinalIgnoreCase)))
            yield return $"描述符的缺省名单 {defaultModList} 不在名单目录里";
    }

    private static string Describe(ModListDefinition definition) =>
        $"{definition.ModIds.Count} 个 mod"
        + (definition.ModsFrom is { } parent ? $"，继承自 {parent}" : "")
        + (definition.SaveFrom is { } owner ? $"，进档用 {owner} 的档" : "");
}
