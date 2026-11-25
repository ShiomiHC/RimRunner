namespace RimRunner;

/// <summary>包在场判定：对照名单声明与进程装载，判可缺席的包这一轮是否在场。</summary>
public static class RimRunnerPackages
{
    private sealed class Snapshot
    {
        public bool ModListBound;
        public required string ModListName;
        public required string[] ModListIds;
        public required string[] LoadedIds;
        public required string[] SuffixedIds;
    }

    private static Snapshot? snapshot;

    /// <summary>可缺席的包这一轮是否在场；名单与装载不匹配时抛。</summary>
    /// <remarks>未绑名单的实例上，包未装即抛；包已装上但程序集未装上的按未装上判：名单写该包即抛，未写即返回 false；引用该包类型的代码须隔进 NoInlining 方法，只在返回 true 时调用。</remarks>
    public static bool Present(string packageId)
    {
        Snapshot current = Current();
        return RimRunnerPackagePresence.Classify(
                    current.ModListBound, current.ModListIds, current.LoadedIds, current.SuffixedIds, packageId) switch
        {
            RimRunnerPackagePresence.Cell.Present => true,
            RimRunnerPackagePresence.Cell.Absent => false,
            RimRunnerPackagePresence.Cell.LoadBroken => throw new InvalidOperationException(
                RimRunnerModAssemblies.TextOf(packageId) is { } shortfall
                    ? packageId + (current.ModListBound ? " 在名单 " + current.ModListName + " 里，" : " ") + "packageId 已装载；" + shortfall
                    : current.ModListBound
                        ? packageId + " 写在名单 " + current.ModListName + " 里却未装入进程"
                        : packageId + " 未装入进程，本轮未绑名单、按在场判"),
            _ => throw new InvalidOperationException(
                                packageId + " 不在名单 " + current.ModListName + " 里却装进进程"),
        };
    }

    private static Dictionary<string, string>? unitAssemblies;

    internal static string? AbsentPackageOfAssembly(string assemblyName)
    {
        if (!UnitAssemblyMap().TryGetValue(assemblyName, out string? packageId))
        {
            return null;
        }

        Snapshot current = Current();
        return RimRunnerPackagePresence.Classify(current.ModListBound, current.ModListIds, current.LoadedIds,
            current.SuffixedIds, packageId) == RimRunnerPackagePresence.Cell.Absent
            ? packageId
            : null;
    }

    internal static string UnitSituation(string assemblyName)
    {
        if (!UnitAssemblyMap().TryGetValue(assemblyName, out string? packageId))
        {
            return UnitSituation(null, RimRunnerPackagePresence.Cell.Present, null);
        }

        Snapshot current = Current();
        return UnitSituation(packageId, RimRunnerPackagePresence.Classify(current.ModListBound, current.ModListIds, current.LoadedIds,
            current.SuffixedIds, packageId), current.ModListBound ? current.ModListName : null);
    }

    internal static string UnitSituation(string? packageId, RimRunnerPackagePresence.Cell cell, string? modListName)
    {
        if (packageId == null)
        {
            return "不属于产品描述符登记的任何产品单元";
        }

        return cell switch
            {
                RimRunnerPackagePresence.Cell.LoadBroken => modListName != null
                    ? "所属的 " + packageId + " 写在名单 " + modListName + " 里却未装入"
                    : "所属的 " + packageId + " 未装入（本轮未绑名单，按在场判）",
                RimRunnerPackagePresence.Cell.ModListDrift => "所属的 " + packageId + " 不在名单 " + modListName + " 里却已装入",
                RimRunnerPackagePresence.Cell.Present => "所属的 " + packageId + " 已装入",
                _ => "所属的 " + packageId + " 本轮缺席",
            };
    }

    private static Dictionary<string, string> UnitAssemblyMap()
    {
        if (unitAssemblies != null)
        {
            return unitAssemblies;
        }

        unitAssemblies = GenCommandLine.TryGetCommandLineArg(RimRunnerEngineArgs.UnitAssemblies, out string raw)
            ? RimRunnerPackagePresence.ParseUnitAssemblies(raw ?? string.Empty)
            : [];
        return unitAssemblies;
    }

    private static string[]? absentableIds;

    internal static IReadOnlyList<string> AbsentablePackageIds =>
        absentableIds ??= GenCommandLine.TryGetCommandLineArg(RimRunnerEngineArgs.AbsentablePackages, out string raw)
            ? RimRunnerPackagePresence.ParseArg(raw ?? string.Empty)
            : [];

    internal static bool Absentable(string packageId) =>
        AbsentablePackageIds.Any(id => string.Equals(id, packageId, StringComparison.OrdinalIgnoreCase));

    internal static Dictionary<Assembly, string> AbsentableAssemblies()
    {
        if (AbsentablePackageIds.Count == 0)
        {
            return [];
        }

        string[] suffixedIds = Current().SuffixedIds;
        return AttributeProviders(LoadedModManager.RunningModsListForReading
                .Where(pack => !string.IsNullOrEmpty(pack.PackageId))
                .Select(pack => (AbsentablePackageIds.FirstOrDefault(id =>
                    RimRunnerPackagePresence.Loaded([pack.PackageId], id, suffixedIds)),
                    (IEnumerable<Assembly>)pack.assemblies.loadedAssemblies)),
            assembly => RimRunnerDefScope.GaugeAssemblies.Contains(assembly));
    }

    internal const string ProviderSeparator = " / ";

    internal static Dictionary<Assembly, string> AttributeProviders(
        IEnumerable<(string? absentableId, IEnumerable<Assembly> assemblies)> packs, Func<Assembly, bool> excluded)
    {
        var providers = new Dictionary<Assembly, SortedSet<string>>();
        var pinned = new HashSet<Assembly>();
        foreach ((string? absentableId, IEnumerable<Assembly> assemblies) in packs)
        {
            foreach (Assembly assembly in assemblies)
            {
                if (excluded(assembly))
                {
                    continue;
                }

                if (absentableId == null)
                {
                    pinned.Add(assembly);
                    continue;
                }

                if (!providers.TryGetValue(assembly, out SortedSet<string>? set))
                {
                    set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    providers[assembly] = set;
                }

                set.Add(absentableId);
            }
        }

        return providers
            .Where(pair => !pinned.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => string.Join(ProviderSeparator, pair.Value));
    }

    internal static string[] SplitProviders(string providers) =>
        providers.Split([ProviderSeparator], StringSplitOptions.None);

    internal static string[] Requirements(Type type, Func<string, string?>? absentPackageOf = null)
    {
        string[] implied = ImpliedRequirements(type, absentPackageOf ?? AbsentPackageOfAssembly);
        if (implied.Length > 0)
        {
            return implied;
        }

        return Declared(type)
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static List<string> DeclarationFaults(Type type, Func<string, string?>? absentPackageOf = null)
    {
        var faults = new List<string>();
        if (ImpliedRequirements(type, absentPackageOf ?? AbsentPackageOfAssembly).Length > 0)
        {
            return faults;
        }

        string[] declared = Declared(type);
        if (declared.Any(string.IsNullOrEmpty))
        {
            faults.Add(type.FullName + " 的 [RimRunnerRequiresPackage] 含空 packageId——空串不指向任何包");
        }

        foreach (IGrouping<string, string> duplicate in declared
                     .Where(id => !string.IsNullOrEmpty(id))
                     .GroupBy(id => id, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            faults.Add(type.FullName + " 声明 " + duplicate.Count() + " 次 [RimRunnerRequiresPackage(\""
                       + duplicate.Key + "\")]（含基类继承来的）");
        }

        return faults;
    }

    internal static RimRunnerPackagePresence.Judgement Judge(IEnumerable<Type> types)
    {
        Snapshot current = Current();
        return JudgeAgainst(types, current.ModListBound, current.ModListName, current.ModListIds, current.LoadedIds,
            current.SuffixedIds);
    }

    internal static RimRunnerPackagePresence.Judgement JudgeAgainst(IEnumerable<Type> types, bool modListBound,
        string modListName, string[] modListIds, string[] loadedIds, string[] suffixedIds)
    {
        var names = new List<string>();
        var packages = new List<string[]>();
        foreach (Type type in types)
        {
            string[] required = Requirements(type);
            if (required.Length > 0)
            {
                names.Add(type.FullName!);
                packages.Add(required);
            }
        }

        return RimRunnerPackagePresence.Judge(modListBound, modListName, modListIds, loadedIds, suffixedIds,
            names.ToArray(), packages.ToArray());
    }

    internal static string AbsentNote(IReadOnlyDictionary<string, string> absentTypes, string noun)
    {
        if (absentTypes.Count == 0)
        {
            return string.Empty;
        }

        string packages = string.Join("、", absentTypes.Values
            .SelectMany(joined => joined.Split([", "], StringSplitOptions.None))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray());
        return "。本实例的名单不带 " + packages + "，依赖它们的 " + absentTypes.Count + " 个" + noun
               + "未装载";
    }

    private static string[] ImpliedRequirements(Type type, Func<string, string?> absentPackageOf)
    {
        List<string> implied = [];
        for (Type? level = type; level != null; level = level.BaseType)
        {
            RimRunnerAbsentReferences.Hit? hit = RimRunnerAbsentReferences.AttributeOf(level);
            if (hit == null)
            {
                continue;
            }

            implied.Add(absentPackageOf(hit.Assembly) ?? throw new InvalidOperationException(level.FullName
                + " 的特性引用的程序集 " + hit.Assembly + RimRunnerAbsentReferences.NotAttributed(hit.Assembly) + "（" + hit.Describe() + "）"));
        }

        return implied.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string[] Declared(Type type)
    {
        return type.GetCustomAttributes(typeof(RimRunnerRequiresPackageAttribute), true)
            .Cast<RimRunnerRequiresPackageAttribute>()
            .Select(attribute => attribute.PackageId)
            .ToArray();
    }

    private static Snapshot Current()
    {
        Snapshot? current = snapshot;
        if (current != null)
        {
            return current;
        }

        bool bound = GenCommandLine.TryGetCommandLineArg(RimRunnerEngineArgs.ModListPackages, out string declared);
        string[] suffixedIds = GenCommandLine.TryGetCommandLineArg(RimRunnerEngineArgs.SuffixedPackageIds, out string rawIds)
            ? RimRunnerPackagePresence.ParseArg(rawIds ?? string.Empty)
            : [];
        current = new Snapshot
        {
            ModListBound = bound,
            ModListName = GenCommandLine.TryGetCommandLineArg(RimRunnerEngineArgs.ModList, out string modList)
                         && !string.IsNullOrEmpty(modList)
                ? modList
                : "<未命名>",
            ModListIds = bound ? RimRunnerPackagePresence.ParseArg(declared ?? string.Empty) : [],
            LoadedIds = LoadedModManager.RunningModsListForReading
                .Select(pack => pack.PackageId)
                .Where(id => !string.IsNullOrEmpty(id) && !RimRunnerModAssemblies.Short(id))
                .ToArray(),
            SuffixedIds = suffixedIds,
        };
        snapshot = current;
        return current;
    }
}
