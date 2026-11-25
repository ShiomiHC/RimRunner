namespace RimRunner;

/// <summary>本产品侧归属：产品与测试程序集都被认作本产品侧，引擎记住的与此刻重认的相同。</summary>
/// <remarks>只验三件事：描述符 ProbeTypes 所在的程序集、测试程序集与本产品侧 mod 包都被认作本产品侧；与按此刻已加载 mod 名单重认的结果逐项相同；mod 名单就绪前认本产品侧报错。载档后执行一次。</remarks>
public class Case_DefScopeDerivation : RimRunnerRegression
{
    private const string Source = "引擎承诺：本产品的程序集与 mod 包被完整认作本产品侧；不成立时扫描用例的扫描面塌空，"
                                  + "报告与「本产品无违例」同形，错误级日志归因也会漏掉本产品";

    public override string Id => "SCOPE1";

    public override RimRunnerLevel Level => RimRunnerLevel.Defs;

    public override void Run(RimRunnerRegressionContext ctx)
    {
        List<ModContentPack> runningMods = LoadedModManager.RunningModsListForReading;
        ctx.AssertTrue("SCOPE1-Gate-RunningModsNonEmpty", runningMods.Count > 0,
            "[engine] 载档后读到的已加载 mod 名单为空，无法识别本产品侧",
            RimRunnerExpectationSource.Literal(Source));
        if (runningMods.Count == 0) return;

        Type[] probeTypes = RimRunnerProduct.Descriptor.ProbeTypes ?? [];
        ctx.AssertTrue("SCOPE1-Gate-DescriptorProbes", probeTypes.Length > 0,
            "产品描述符的 ProbeTypes 为空：本用例无从核对本产品侧是否被完整识别",
            RimRunnerExpectationSource.Literal(Source));

        RimRunnerDefScope.Derivation cached = RimRunnerDefScope.CachedForSelfTest;
        HashSet<Assembly> probeAssemblies = [.. cached.ProbeAssemblies];
        ctx.AssertTrue("SCOPE1-ProbeAssembliesNonEmpty", probeAssemblies.Count > 0,
            "[engine] 认作本产品侧的产品程序集为空：扫描产品代码的用例未扫到任何程序集",
            RimRunnerExpectationSource.Literal(Source));

        List<string> missing = [];
        foreach (Type probeType in probeTypes)
        {
            if (probeType != null && !probeAssemblies.Contains(probeType.Assembly))
            {
                missing.Add(probeType.Name + "（" + probeType.Assembly.GetName().Name + "）");
            }
        }

        ctx.AssertNoneOffending("SCOPE1-ProbeAssembliesCoverDescriptor", missing,
            "描述符 ProbeTypes 里上列类型所在的程序集未被认作本产品侧：该程序集不属于本名单加载的任何 mod",
            RimRunnerExpectationSource.Literal(Source));

        List<string?> gaugesOutside = RimRunnerDefScope.GaugeAssemblies
            .Where(gauge => !cached.Assemblies.Contains(gauge))
            .Select(gauge => gauge.GetName().Name)
            .ToList();
        ctx.AssertTrue("SCOPE1-OurAssembliesContainSelf", gaugesOutside.Count == 0,
            "[engine] 测试程序集 " + string.Join("、", gaugesOutside) + " 未被认作本产品侧：测试代码输出的错误级日志无法归入本产品，"
            + "CLI 判定实例是否加载盘上最新的测试程序集时也会漏掉该测试程序集",
            RimRunnerExpectationSource.Literal(Source));
        ctx.AssertTrue("SCOPE1-OurPackIdsNonEmpty", cached.Packs.Count > 0,
            "[engine] 认作本产品侧的 mod 包为空",
            RimRunnerExpectationSource.Literal(Source));

        string[] missingContent = new[] { "RimRunnerDummy_Races.xml", "RimRunnerDummy_Kinds.xml" }
            .Where(file => !cached.EngineContentFileNames.Contains(file)).ToArray();
        ctx.AssertTrue("SCOPE1-EngineContentFilesNonEmpty", missingContent.Length == 0,
            "[engine] 装着引擎程序集的 pack 根下 Defs/RimRunner/ 读到 " + cached.EngineContentFileNames.Count
            + " 个文件名，缺 " + string.Join("、", missingContent) + "：缺失文件里的引擎假人进入 def 扫描面",
            RimRunnerExpectationSource.Literal(Source));

        RimRunnerDefScope.Derivation fresh = RimRunnerDefScope.Derive(runningMods);
        List<string> diffs = [];
        if (!cached.Packs.SetEquals(fresh.Packs))
            diffs.Add("Packs 缓存=" + Names(cached.Packs.Select(pack => pack.PackageId))
                      + " 重算=" + Names(fresh.Packs.Select(pack => pack.PackageId)));
        if (!cached.Assemblies.SetEquals(fresh.Assemblies))
            diffs.Add("Assemblies 缓存=" + Names(cached.Assemblies.Select(assembly => assembly.GetName().Name))
                      + " 重算=" + Names(fresh.Assemblies.Select(assembly => assembly.GetName().Name)));
        if (!cached.ProbeAssemblies.SequenceEqual(fresh.ProbeAssemblies))
            diffs.Add("ProbeAssemblies 缓存=" + Names(cached.ProbeAssemblies.Select(assembly => assembly.GetName().Name))
                      + " 重算=" + Names(fresh.ProbeAssemblies.Select(assembly => assembly.GetName().Name)));
        if (!string.Equals(cached.FallbackPrefix, fresh.FallbackPrefix, StringComparison.Ordinal))
            diffs.Add("FallbackPrefix 缓存=" + (cached.FallbackPrefix ?? "<null>")
                      + " 重算=" + (fresh.FallbackPrefix ?? "<null>"));
        if (cached.EngineContentPack?.PackageId != fresh.EngineContentPack?.PackageId)
            diffs.Add("EngineContentPack 缓存=" + (cached.EngineContentPack?.PackageId ?? "<null>")
                      + " 重算=" + (fresh.EngineContentPack?.PackageId ?? "<null>"));
        if (!cached.EngineContentFileNames.SetEquals(fresh.EngineContentFileNames))
            diffs.Add("EngineContentFileNames 缓存=" + Names(cached.EngineContentFileNames)
                      + " 重算=" + Names(fresh.EngineContentFileNames));
        ctx.AssertNoneOffending("SCOPE1-Sentinel-RecomputeMatches", diffs,
            "[engine] 引擎记住的本产品侧与此刻按同一份 mod 名单重认的结果不匹配",
            RimRunnerExpectationSource.Literal(Source));

        ctx.AssertThrows<InvalidOperationException>("SCOPE1-Sentinel-UnreadyThrows-Empty",
            () => RimRunnerDefScope.Derive([]),
            "[engine] 对空 mod 名单认本产品侧没有报错",
            RimRunnerExpectationSource.Literal(Source));
        List<ModContentPack> officialOnly = runningMods.Where(pack => pack.IsOfficialMod).ToList();
        ctx.AssertTrue("SCOPE1-Gate-OfficialOnlyListNonEmpty", officialOnly.Count > 0,
            "[engine] 已加载 mod 名单里没有官方包（Core / DLC），「只含官方包」一段的输入为空",
            RimRunnerExpectationSource.Literal(Source));
        ctx.AssertThrows<InvalidOperationException>("SCOPE1-Sentinel-UnreadyThrows-OfficialOnly",
            () => RimRunnerDefScope.Derive(officialOnly),
            "[engine] 对只含官方包（" + officialOnly.Count + " 个）的列表认本产品侧没有报错",
            RimRunnerExpectationSource.Literal(Source));

        ctx.AddDiagnostic(RimRunnerDiagnosticKind.StaticScan, "Derivation",
            "packs=" + Names(cached.Packs.Select(pack => pack.PackageId))
            + " probeAssemblies=" + Names(cached.ProbeAssemblies.Select(assembly => assembly.GetName().Name))
            + " assemblies=" + Names(cached.Assemblies.Select(assembly => assembly.GetName().Name))
            + " fallbackPrefix=" + (cached.FallbackPrefix ?? "<null>")
            + " engineContentPack=" + (cached.EngineContentPack?.PackageId ?? "<null>")
            + " engineContentFiles=" + cached.EngineContentFileNames.Count
            + " runningMods=" + runningMods.Count);
    }

    private static string Names(IEnumerable<string?> names)
    {
        List<string?> sorted = names.OrderBy(name => name!, StringComparer.Ordinal).ToList();
        return sorted.Count == 0 ? "(空)" : string.Join(",", sorted.ToArray());
    }
}
