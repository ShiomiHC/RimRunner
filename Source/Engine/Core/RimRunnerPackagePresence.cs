namespace RimRunner;

public static class RimRunnerPackagePresence
{
    public enum Cell
    {
        Present,
        Absent,
        LoadBroken,
        ModListDrift,
    }

    public static bool Loaded(string[] loadedIds, string packageId, string[] suffixedIds)
    {
        string? suffixed = null;
        for (int i = 0; i < suffixedIds.Length; i++)
        {
            if (string.Equals(BareOf(suffixedIds[i]), packageId, System.StringComparison.OrdinalIgnoreCase))
            {
                suffixed = suffixedIds[i];
            }
        }

        for (int i = 0; i < loadedIds.Length; i++)
        {
            if (string.Equals(loadedIds[i], packageId, System.StringComparison.OrdinalIgnoreCase)
                || (suffixed != null && string.Equals(loadedIds[i], suffixed, System.StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    public static string BareOf(string suffixedId)
    {
        int dot = suffixedId.LastIndexOf('.');
        return dot < 0 ? suffixedId : suffixedId[..dot];
    }

    public static bool Listed(string[] modListIds, string packageId)
    {
        for (int i = 0; i < modListIds.Length; i++)
        {
            if (string.Equals(modListIds[i], packageId, System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static Cell Classify(bool modListBound, string[] modListIds, string[] loadedIds, string[] suffixedIds, string packageId)
    {
        bool listed = !modListBound || Listed(modListIds, packageId);
        bool loaded = Loaded(loadedIds, packageId, suffixedIds);
        if (listed)
        {
            return loaded ? Cell.Present : Cell.LoadBroken;
        }

        return loaded ? Cell.ModListDrift : Cell.Absent;
    }

    public static string FormatArg(string[] modListIds)
    {
        return string.Join(",", modListIds);
    }

    public static string[] ParseArg(string value)
    {
        string[] parts = value.Split(',');
        var ids = new System.Collections.Generic.List<string>(parts.Length);
        for (int i = 0; i < parts.Length; i++)
        {
            string id = parts[i].Trim();
            if (id.Length > 0)
            {
                ids.Add(id);
            }
        }

        return ids.ToArray();
    }

    public static string FormatUnitAssemblies(string[] assemblyNames, string[] packageIds)
    {
        var pairs = new string[assemblyNames.Length];
        for (int i = 0; i < assemblyNames.Length; i++)
        {
            pairs[i] = assemblyNames[i] + ":" + packageIds[i];
        }

        return string.Join(",", pairs);
    }

    public static System.Collections.Generic.Dictionary<string, string> ParseUnitAssemblies(string value)
    {
        var map = new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (string raw in value.Split(','))
        {
            string pair = raw.Trim();
            if (pair.Length == 0)
            {
                continue;
            }

            int colon = pair.IndexOf(':');
            string assembly = colon < 0 ? string.Empty : pair[..colon].Trim();
            string package = colon < 0 ? string.Empty : pair[(colon + 1)..].Trim();
            if (assembly.Length == 0 || package.Length == 0)
            {
                throw new System.FormatException("产品单元程序集对照写错一段：「" + pair + "」，应为 <程序集名>:<packageId>");
            }

            map[assembly] = package;
        }

        return map;
    }

    public sealed class Judgement
    {
        public readonly System.Collections.Generic.SortedDictionary<string, string> Absent =
            new(System.StringComparer.Ordinal);

        public readonly System.Collections.Generic.HashSet<string> Withheld =
            new(System.StringComparer.Ordinal);

        public readonly System.Collections.Generic.List<string> Faults = [];

        public bool Admits(string typeName)
        {
            return !Absent.ContainsKey(typeName) && !Withheld.Contains(typeName);
        }
    }

    public static Judgement Judge(bool modListBound, string modListName, string[] modListIds, string[] loadedIds, string[] suffixedIds,
        string[] typeNames, string[][] packages)
    {
        var judgement = new Judgement();
        var cells = new System.Collections.Generic.Dictionary<string, Cell>(System.StringComparer.OrdinalIgnoreCase);
        var requirers = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>(
            System.StringComparer.OrdinalIgnoreCase);
        var order = new System.Collections.Generic.List<string>();

        for (int i = 0; i < typeNames.Length; i++)
        {
            var absentHere = new System.Collections.Generic.List<string>();
            bool withheld = false;
            foreach (string package in packages[i])
            {
                if (!cells.TryGetValue(package, out Cell cell))
                {
                    cell = Classify(modListBound, modListIds, loadedIds, suffixedIds, package);
                    cells[package] = cell;
                    requirers[package] = [];
                    order.Add(package);
                }

                requirers[package].Add(typeNames[i]);
                if (cell == Cell.LoadBroken)
                {
                    withheld = true;
                }
                else if (cell == Cell.Absent)
                {
                    absentHere.Add(package);
                }
            }

            if (withheld)
            {
                judgement.Withheld.Add(typeNames[i]);
            }
            else if (absentHere.Count > 0)
            {
                judgement.Absent[typeNames[i]] = string.Join(", ", absentHere.ToArray());
            }
        }

        foreach (string package in order)
        {
            string types = string.Join("、", requirers[package].ToArray());
            switch (cells[package])
            {
                case Cell.LoadBroken:
                    judgement.Faults.Add(modListBound
                        ? package + " 写在名单 " + modListName + " 里却未装入进程，依赖该包的用例类未装载：" + types
                        : package + " 未装入进程，本轮未绑名单、按在场判，依赖该包的用例类未装载：" + types);
                    break;
                case Cell.ModListDrift:
                    judgement.Faults.Add(package + " 不在名单 " + modListName + " 里却装进进程（依赖该包的用例类：" + types
                                         + "）");
                    break;
            }
        }

        return judgement;
    }
}
