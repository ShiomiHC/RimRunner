namespace RimRunnerCli;

public static class UnitAssemblyMap
{
    public static IReadOnlyList<(string Assembly, string PackageId)> Derive(OrchestratorDescriptor descriptor)
    {
        var map = new List<(string, string)>();
        foreach ((string assembly, string dllPath) in descriptor.FreshnessAssemblies
                     .OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            string path = Normalize(dllPath);
            OrchestratorDescriptor.UnitDescriptor? owner = null;
            int ownerLength = -1;
            foreach (OrchestratorDescriptor.UnitDescriptor unit in descriptor.Units)
            {
                string prefix = Normalize(unit.SourceRel).TrimEnd('/') + "/";
                if (prefix.Length > 1 && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && prefix.Length > ownerLength)
                {
                    owner = unit;
                    ownerLength = prefix.Length;
                }
            }

            if (owner != null && !string.IsNullOrWhiteSpace(owner.PackageId))
            {
                map.Add((assembly, owner.PackageId));
            }
        }

        return map;
    }

    private static string Normalize(string path)
    {
        string forward = path.Replace('\\', '/').Trim();
        while (forward.StartsWith("./", StringComparison.Ordinal))
        {
            forward = forward[2..];
        }

        return forward;
    }
}
