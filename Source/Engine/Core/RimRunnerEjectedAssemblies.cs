namespace RimRunner;

internal static class RimRunnerEjectedAssemblies
{
    private static IReadOnlyList<RimRunnerModAssemblies.Ejection>? all;

    internal static IReadOnlyList<RimRunnerModAssemblies.Ejection> All => all ??= Find();

    private static IReadOnlyList<RimRunnerModAssemblies.Ejection> Find()
    {
        ModContentPack? pack = LoadedModManager.RunningModsListForReading.FirstOrDefault(candidate =>
            candidate?.assemblies?.loadedAssemblies?.Contains(RimRunnerDefScope.EngineAssembly) == true);
        return pack == null ? [] : RimRunnerModAssemblies.Of(pack);
    }

    internal static IReadOnlyList<string> Faults =>
        All.Select(ejection => Describe(ejection, RimRunnerPackages.AbsentPackageOfAssembly, RimRunnerProduct.TestAssembly != null))
            .ToList();

    internal const string TestAssemblyAbsent = "测试程序集不在本进程";
    internal const string EjectedReason = "被游戏剔除";
    internal const string AbsentPackageOwner = "属于本轮名单缺席的 ";
    internal const string UnresolvedAssembly = "本进程无法解析程序集 ";
    internal const string UnknownCause =
        "引擎无法识别是哪处引用导致它无法装入；Player.log 里 ReflectionTypeLoadException getting types in assembly ";

    internal static string ReferenceLine(string type, RimRunnerAbsentReferences.Hit hit) => type + " 的" + hit.Describe();

    internal static string Describe(RimRunnerModAssemblies.Ejection ejection, Func<string, string?> absentPackageOf, bool testAssemblyLoaded)
    {
        if (ejection.NotLoaded)
        {
            string consequence = testAssemblyLoaded
                ? ""
                : "。" + TestAssemblyAbsent + "，它的用例、产品描述符与 Harmony 补丁都不在本进程";
            return RimRunnerModAssemblies.Describe(ejection, "测试 mod ") + consequence;
        }

        string head = "测试 mod 的程序集 " + ejection.Assembly + " " + EjectedReason + "，它的用例、产品描述符与 Harmony 补丁都不在本进程";
        if (ejection.Causes.Count == 0)
        {
            return head + "。" + UnknownCause + ejection.Assembly + " 那条列出无法装入的类型";
        }

        List<string> groups = [];
        foreach (IGrouping<string, (string Type, RimRunnerAbsentReferences.Hit Hit)> group in ejection.Causes
                     .GroupBy(cause => cause.Hit.Assembly, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            string? package = absentPackageOf(group.Key);
            string owner = package != null
                ? "程序集 " + group.Key + " " + AbsentPackageOwner + package
                : UnresolvedAssembly + group.Key + "，" + RimRunnerPackages.UnitSituation(group.Key)
                  + (RimRunnerModAssemblies.OwnerText(group.Key) is { } modOwner ? "；" + modOwner : "");
            groups.Add(owner + "，下列引用落在该程序集上：\n  "
                       + string.Join("\n  ", group.Select(cause => ReferenceLine(cause.Type, cause.Hit))));
        }

        return head + "。" + string.Join("\n", groups)
               + "\n基类、接口与值类型字段用到的类型缺席时整个程序集无法装入";
    }

    internal static string Names =>
        string.Join("、", All.SelectMany(ejection => ejection.NotLoaded ? ejection.Missing : [ejection.Assembly]));

    internal static string Note =>
        All.Count == 0
            ? string.Empty
            : "。测试 mod 的 " + Names + " 未能装入，其中的用例不在本进程（拒因在包依赖一栏）";

    internal static bool Explains(string text) =>
        All.Any(ejection => ejection.NotLoaded
            ? ejection.Error != null && text.StartsWith("Exception loading " + ejection.Assembly + ":", StringComparison.Ordinal)
            : ejection.Causes.Count > 0
              && text.StartsWith("ReflectionTypeLoadException getting types in assembly " + ejection.Assembly,
                  StringComparison.Ordinal));
}
