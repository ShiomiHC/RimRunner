namespace RimRunnerCli;

public static class AbsentablePackages
{
    public static IReadOnlyList<string> Derive(OrchestratorDescriptor descriptor, string productRoot) =>
        string.IsNullOrWhiteSpace(descriptor.ModListsDir)
            ? []
            : Derive(Path.Combine(productRoot, descriptor.ModListsDir), descriptor.RequiredPackageIds);

    public static IReadOnlyList<string> Derive(string modListsDir, IReadOnlyList<string> requiredIds)
    {
        string fullDir = CommandContext.FullPath(modListsDir);
        if (!Directory.Exists(fullDir)) return [];
        IReadOnlyList<string> names;
        try
        {
            names = RimTooling.RimWorld.RimWorldModList.ListNames(fullDir, ModListDefinition.LoadOptions);
        }
        catch (RimTooling.RimWorld.ModListException)
        {
            return [];
        }

        var sets = new List<HashSet<string>>();
        var order = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in names)
        {
            ModListDefinition definition;
            try
            {
                definition = ModListDefinition.Load(fullDir, name);
            }
            catch (StoreException)
            {
                continue;
            }

            if (!Runnable(definition, requiredIds)) continue;
            sets.Add(new HashSet<string>(definition.ModIds, StringComparer.OrdinalIgnoreCase));
            foreach (string id in definition.ModIds)
                if (seen.Add(id)) order.Add(id);
        }

        return
        [
            .. order.Where(id => sets.Any(set => !set.Contains(id)))
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
        ];
    }

    private static bool Runnable(ModListDefinition definition, IReadOnlyList<string> requiredIds)
    {
        IReadOnlyList<string> omitted;
        try
        {
            omitted = OmitRequiredPolicy.Resolve(definition.Name,
                definition.DirectiveList(OmitRequiredPolicy.DirectiveKey), requiredIds, definition.ModIds);
        }
        catch (StoreException)
        {
            return false;
        }

        return requiredIds.All(required =>
            definition.ModIds.Any(id => id.Equals(required, StringComparison.OrdinalIgnoreCase))
            || omitted.Any(id => id.Equals(required, StringComparison.OrdinalIgnoreCase)));
    }
}
