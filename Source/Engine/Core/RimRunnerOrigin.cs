namespace RimRunner;

internal static class RimRunnerOrigin
{
    internal const string Case = "用例";
    internal const string Product = "产品";
    internal const string Vanilla = "vanilla";
    internal const string Engine = "引擎";
    internal const string ModPrefix = "mod:";
    internal const string AssemblyPrefix = "程序集:";

    internal static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    internal static string Of(MethodBase reader, Type? caseType)
    {
        Type type = reader.DeclaringType!;
        if (caseType != null && RimRunnerContractGates.IsCaseSide(type, caseType))
        {
            return Case;
        }

        return OfAssembly(type.Assembly);
    }

    internal static string OfRoot(Assembly assembly)
    {
        if (assembly == typeof(RimRunnerOrigin).Assembly)
        {
            return Engine;
        }

        return assembly == RimRunnerProduct.TestAssembly ? "测试程序集" : OfAssembly(assembly);
    }

    private static string OfAssembly(Assembly assembly)
    {
        if (RimRunnerDefScope.ProbeAssemblies.Contains(assembly))
        {
            return Product;
        }

        if (assembly == typeof(Game).Assembly)
        {
            return Vanilla;
        }

        ModContentPack? pack = PackOf(assembly);
        return pack != null
            ? ModPrefix + RimRunnerOutwardPackageIds.BarePackageId(pack.PackageIdPlayerFacing)
            : AssemblyPrefix + assembly.GetName().Name;
    }

    internal static bool IsLoaded(string origin)
    {
        if (!origin.StartsWith(ModPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        loadedMods ??= new HashSet<string>(LoadedModManager.RunningModsListForReading
            .Select(pack => RimRunnerOutwardPackageIds.BarePackageId(pack.PackageIdPlayerFacing)).OfType<string>(), Comparer);
        return loadedMods.Contains(origin[ModPrefix.Length..]);
    }

    private static HashSet<string>? loadedMods;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Assembly, ModContentPack?> packs = new();

    internal static ModContentPack? PackOf(Assembly assembly) =>
        packs.GetOrAdd(assembly, key => LoadedModManager.RunningModsListForReading
            .FirstOrDefault(candidate => candidate?.assemblies?.loadedAssemblies?.Contains(key) == true));

    internal static bool IsWellFormed(string origin) =>
        Comparer.Equals(origin, Case) || Comparer.Equals(origin, Product) || Comparer.Equals(origin, Vanilla)
        || (origin.StartsWith(ModPrefix, StringComparison.OrdinalIgnoreCase) && origin.Length > ModPrefix.Length)
        || (origin.StartsWith(AssemblyPrefix, StringComparison.OrdinalIgnoreCase) && origin.Length > AssemblyPrefix.Length);

    internal static string Lead(string origin) => "[" + origin + "] ";

    internal static string Body(string text)
    {
        if (!text.StartsWith("[", StringComparison.Ordinal))
        {
            return text;
        }

        int close = text.IndexOf("] ", StringComparison.Ordinal);
        return close < 0 ? text : text[(close + 2)..];
    }
}
