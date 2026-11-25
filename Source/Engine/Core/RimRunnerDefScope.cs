using System.IO;
using System.Threading;

namespace RimRunner;

/// <summary>本产品侧归属：判 def、类型、pack 是否为本产品侧，并给出各扫描面。</summary>
/// <remarks>本产品侧 pack 是产品描述符探针类型所在的 pack，加上含引擎与测试程序集的 pack；本产品侧程序集即这些 pack 装载的全部程序集。</remarks>
public static class RimRunnerDefScope
{
    internal sealed class Derivation(
        HashSet<ModContentPack> packs, HashSet<Assembly> assemblies,
        List<Assembly> probeAssemblies, string? fallbackPrefix,
        ModContentPack? engineContentPack, HashSet<string> engineContentFileNames)
    {
        public readonly HashSet<ModContentPack> Packs = packs;
        public readonly HashSet<Assembly> Assemblies = assemblies;
        public readonly List<Assembly> ProbeAssemblies = probeAssemblies;
        public readonly string? FallbackPrefix = fallbackPrefix;
        public readonly ModContentPack? EngineContentPack = engineContentPack;
        public readonly HashSet<string> EngineContentFileNames = engineContentFileNames;
    }

    private const string EngineContentDefsRel = "Defs/RimRunner";

    private const string EngineContentXmlPattern = "*.xml";

    private static readonly Lock InitLock = new();
    private static volatile bool initialized;

    /// <summary>产品代码扫描面：只含产品描述符探针类型所在的程序集。</summary>
    public static IEnumerable<Assembly> ProbeAssemblies => Cached.ProbeAssemblies;

    internal static IEnumerable<Assembly> OwnedAssemblies => Cached.Assemblies;

    /// <summary>测试与引擎代码的扫描面：引擎、引擎自测（装载时）与测试程序集，按身份去重。</summary>
    public static IReadOnlyList<Assembly> GaugeAssemblies
    {
        get
        {
            if (field == null)
            {
                List<Assembly> assemblies = [EngineAssembly];
                if (SelfTestAssembly != null) assemblies.Add(SelfTestAssembly);
                Assembly? testAssembly = RimRunnerProduct.TestAssembly;
                if (testAssembly != null && testAssembly != EngineAssembly)
                {
                    assemblies.Add(testAssembly);
                }

                field = assemblies.AsReadOnly();
            }

            return field;
        }
    }

    internal static IEnumerable<Type> GaugeTypes() => GaugeAssemblies.SelectMany(assembly => assembly.GetTypes());

    internal static Assembly EngineAssembly => typeof(RimRunnerDefScope).Assembly;

    internal const string SelfTestAssemblyName = "RimRunner.SelfTest";

    internal static Assembly? SelfTestAssembly => selfTestAssembly.Value;

    private static readonly Lazy<Assembly?> selfTestAssembly = new(() => AppDomain.CurrentDomain
        .GetAssemblies().FirstOrDefault(assembly => assembly.GetName().Name == SelfTestAssemblyName));

    internal static bool IsEngineSide(Assembly? assembly) =>
        assembly != null && (assembly == EngineAssembly || assembly == SelfTestAssembly);

    /// <summary>本产品侧 pack 的 packageId。</summary>
    public static IEnumerable<string> OurPackIds => Cached.Packs.Select(pack => pack.PackageId);

    /// <summary>空 pack 时的 defName 回退前缀；描述符缺席时为 null。</summary>
    public static string? FallbackDefNamePrefix => Cached.FallbackPrefix;

    /// <summary>已加载的本产品侧程序集及其 MVID。</summary>
    /// <returns>键为程序集名，值为 "D" 格式的 MVID。</returns>
    public static SortedDictionary<string, string> OurAssemblyMvids()
    {
        SortedDictionary<string, string> mvids =
            new(StringComparer.Ordinal);
        foreach (Assembly assembly in OwnedAssemblies)
        {
            mvids[assembly.GetName().Name!] = assembly.ManifestModule.ModuleVersionId.ToString("D");
        }

        return mvids;
    }

    /// <summary>该 pack 是否为本产品侧。</summary>
    public static bool IsOursPack(ModContentPack pack)
    {
        return pack != null && Cached.Packs.Contains(pack);
    }

    /// <summary>该类型是否由本产品侧程序集定义。</summary>
    public static bool IsOursType(Type type)
    {
        return type != null && Cached.Assemblies.Contains(type.Assembly);
    }

    /// <summary>该 def 是否为本产品侧（pack 集合成员，空 pack 按前缀回退）。</summary>
    public static bool IsOursDef(Def def)
    {
        if (def == null)
        {
            return false;
        }

        Derivation scope = Cached;
        return def.modContentPack != null
            ? scope.Packs.Contains(def.modContentPack)
            : def.defName != null && scope.FallbackPrefix != null
                                  && def.defName.StartsWith(scope.FallbackPrefix, StringComparison.Ordinal);
    }

    private static Derivation Cached
    {
        get
        {
            if (initialized)
            {
                return field!;
            }

            lock (InitLock)
            {
                if (!initialized)
                {
                    field = Derive(LoadedModManager.RunningModsListForReading);
                    initialized = true;
                }

                return field!;
            }
        }
    }

    internal static Derivation CachedForSelfTest => Cached;

    internal static Derivation Derive(IReadOnlyList<ModContentPack> runningMods)
    {
        if (runningMods == null)
        {
            throw new ArgumentNullException(nameof(runningMods));
        }

        List<ModContentPack> gaugePacks = [];
        ModContentPack? engineContentPack = null;
        foreach (Assembly gauge in GaugeAssemblies)
        {
            ModContentPack gaugePack = FindPackHolding(runningMods, gauge) ?? throw new InvalidOperationException(
                    RimRunnerLog.Prefix + " 归属判定在 mod 名单就绪前被触发：RunningMods 当前 "
                    + runningMods.Count + " 个 pack，均未装载测试程序集 "
                    + gauge.GetName().Name);
            gaugePacks.Add(gaugePack);
            if (gauge == EngineAssembly)
            {
                engineContentPack = gaugePack;
            }
        }

        HashSet<string> engineContentFileNames = EngineContentFiles(engineContentPack);

        HashSet<ModContentPack> packs = [];
        HashSet<Assembly> assemblies = [];
        List<Assembly> probeAssemblies = [];
        string? fallbackPrefix = null;

        RimRunnerProductDescriptor? descriptor = RimRunnerProduct.TryGetDescriptor();
        if (descriptor == null && RimRunnerEjectedAssemblies.All.Count > 0)
        {
            return new Derivation(packs, assemblies, probeAssemblies, fallbackPrefix, engineContentPack, engineContentFileNames);
        }

        if (descriptor == null)
        {
            RimRunnerStartupErrors.Raise("找不到产品描述符：已加载程序集中没有任何 "
                                          + nameof(RimRunnerProductDescriptor) + " 的非抽象子类。归属判定退化为空集，全部按非本产品与引擎处置");
            return new Derivation(packs, assemblies, probeAssemblies, fallbackPrefix, engineContentPack, engineContentFileNames);
        }

        Type[] probeTypes = descriptor.ProbeTypes ?? [];
        if (probeTypes.Length == 0)
        {
            RimRunnerStartupErrors.Raise("产品描述符 " + descriptor.GetType().Name
                      + " 的探针列表为空：产品扫描面退化为空集，全部 IL / 反射型扫描用例的差集将平凡成立");
        }

        foreach (Type probeType in probeTypes)
        {
            if (probeType == null)
            {
                RimRunnerStartupErrors.Raise("产品描述符 " + descriptor.GetType().Name
                          + " 的探针列表含 null 项");
                continue;
            }

            ModContentPack? pack = FindPackHolding(runningMods, probeType.Assembly);
            if (pack == null)
            {
                RimRunnerStartupErrors.Raise("产品描述符探针 " + probeType.Name + "（程序集 "
                          + probeType.Assembly.GetName().Name + "）未命中任何已加载 mod 的程序集——"
                          + "探针类型不属于任何 RunningMods pack，归属判定将缺这一支");
                continue;
            }

            probeAssemblies.Add(probeType.Assembly);
            packs.Add(pack);
        }

        string? prefix = descriptor.DefNameFallbackPrefix;
        if (prefix != null && string.IsNullOrWhiteSpace(prefix))
        {
            RimRunnerStartupErrors.Raise("产品描述符的 DefNameFallbackPrefix 是"
                                         + (prefix.Length == 0 ? "空串" : "全空白串")
                                         + "：找不到所属 mod 的 def 不按前缀认领");
            prefix = null;
        }

        fallbackPrefix = prefix;

        foreach (ModContentPack gaugePack in gaugePacks)
        {
            packs.Add(gaugePack);
        }

        foreach (ModContentPack pack in packs)
        {
            foreach (Assembly assembly in pack.assemblies.loadedAssemblies)
            {
                assemblies.Add(assembly);
            }
        }

        return new Derivation(packs, assemblies, probeAssemblies, fallbackPrefix, engineContentPack, engineContentFileNames);
    }

    private static ModContentPack? FindPackHolding(
        IReadOnlyList<ModContentPack> runningMods, Assembly assembly)
    {
        for (int i = 0; i < runningMods.Count; i++)
        {
            ModContentPack candidate = runningMods[i];
            if (candidate?.assemblies?.loadedAssemblies?.Contains(assembly) == true)
            {
                return candidate;
            }
        }

        return null;
    }

    private static HashSet<string> EngineContentFiles(ModContentPack? pack)
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        if (pack == null)
        {
            return names;
        }

        string dir = Path.Combine(pack.RootDir, EngineContentDefsRel);
        if (!Directory.Exists(dir))
        {
            return names;
        }

        foreach (string file in Directory.EnumerateFiles(dir, EngineContentXmlPattern, SearchOption.AllDirectories))
        {
            names.Add(Path.GetFileName(file));
        }

        return names;
    }

    private static bool IsEngineContentDummy(Derivation scope, Def def)
    {
        return def != null
               && scope.EngineContentPack != null
               && def.modContentPack == scope.EngineContentPack
               && def.fileName != null
               && scope.EngineContentFileNames.Contains(def.fileName);
    }

    internal static bool IsEngineContentDummyForSelfTest(Def def) => IsEngineContentDummy(Cached, def);

    private enum Reach
    {
        Own,
        OwnAndOfficial
    }

    private readonly record struct Scan<T>(List<T> Kept, string Label, string? Shortfall, string Expectation);

    /// <summary>def 扫描面：只留本产品侧 def，滤掉的逐个记名进诊断。</summary>
    /// <remarks>官方包只记条数。ThingDef 与 PawnKindDef 缺省滤掉引擎内容里的假人，要回传 includeEngineDummies；where 收窄后为空即失败。</remarks>
    public static List<T> OwnDefs<T>(RimRunnerRegressionContext ctx, Func<T, bool>? where = null,
        bool includeEngineDummies = false)
        where T : Def
        => Asserted(ctx, Collect(ctx, Reach.Own, where, includeEngineDummies));

    /// <summary>读数用例取本产品侧 def：同 OwnDefs 的扫描面，收窄后为空即中止。</summary>
    /// <remarks>where 收窄后为空按前提不满足认领并中止本条；其余同回归用例版。</remarks>
    public static List<T> OwnDefs<T>(RimRunnerReadoutContext ctx, Func<T, bool>? where = null,
        bool includeEngineDummies = false)
        where T : Def
        => Required(Collect(ctx, Reach.Own, where, includeEngineDummies));

    /// <summary>def 扫描面：留本产品侧与官方包的 def，滤掉的逐个记名进诊断。</summary>
    /// <remarks>查产品对 vanilla 内容的改动时用；只查自己的用 OwnDefs。假人过滤与 where 收窄后判空同 OwnDefs。</remarks>
    public static List<T> OwnAndOfficialDefs<T>(RimRunnerRegressionContext ctx, Func<T, bool>? where = null,
        bool includeEngineDummies = false)
        where T : Def
        => Asserted(ctx, Collect(ctx, Reach.OwnAndOfficial, where, includeEngineDummies));

    /// <summary>读数用例取本产品侧与官方包的 def：收窄后为空即中止。</summary>
    /// <remarks>where 收窄后为空按前提不满足认领并中止本条；其余同回归用例版。</remarks>
    public static List<T> OwnAndOfficialDefs<T>(RimRunnerReadoutContext ctx, Func<T, bool>? where = null,
        bool includeEngineDummies = false)
        where T : Def
        => Required(Collect(ctx, Reach.OwnAndOfficial, where, includeEngineDummies));

    internal static (List<T> Kept, string? Shortfall) CollectForSelfTest<T>(RimRunnerContext ctx, bool ownOnly,
        Func<T, bool>? where = null, bool includeEngineDummies = false)
        where T : Def
    {
        Scan<T> scan = Collect(ctx, ownOnly ? Reach.Own : Reach.OwnAndOfficial, where, includeEngineDummies);
        return (scan.Kept, scan.Shortfall);
    }

    private static List<T> Asserted<T>(RimRunnerRegressionContext ctx, Scan<T> scan)
    {
        ctx.AssertTrue(scan.Label + "-nonEmpty", scan.Shortfall == null, scan.Shortfall ?? "",
            RimRunnerExpectationSource.Literal(scan.Expectation));
        return scan.Kept;
    }

    private static List<T> Required<T>(Scan<T> scan)
    {
        RimRunnerFault.Require(scan.Shortfall == null, "def 扫描面为空",
            scan.Label + "：" + scan.Shortfall);
        return scan.Kept;
    }

    private static Scan<T> Collect<T>(RimRunnerContext ctx, Reach reach, Func<T, bool>? where, bool includeEngineDummies)
        where T : Def
    {
        if (ctx == null)
        {
            throw new ArgumentNullException(nameof(ctx));
        }

        Derivation scope = Cached;
        bool filtersEngineDummies = !includeEngineDummies
                                    && (typeof(T) == typeof(ThingDef) || typeof(T) == typeof(PawnKindDef));
        bool engineContentReadable = scope.EngineContentFileNames.Count > 0;
        List<T> kept = [];
        List<string> dropped = [];
        List<string> engineDummies = [];
        int officialDropped = 0;
        foreach (T def in DefDatabase<T>.AllDefsListForReading)
        {
            if (def == null)
            {
                continue;
            }

            if (filtersEngineDummies && engineContentReadable && IsEngineContentDummy(scope, def)
                && def.defName is { } engineDefName)
            {
                engineDummies.Add(engineDefName);
            }
            else if (IsOursDef(def))
            {
                kept.Add(def);
            }
            else if (def.modContentPack != null && def.modContentPack.IsOfficialMod)
            {
                if (reach == Reach.OwnAndOfficial)
                {
                    kept.Add(def);
                }
                else
                {
                    officialDropped++;
                }
            }
            else if (def.defName != null)
            {
                dropped.Add(def.defName);
            }
        }

        int beforeWhere = kept.Count;
        if (where != null)
        {
            kept = kept.Where(where).ToList();
        }

        string typeName = typeof(T).Name;
        string label = reach + "Defs-" + typeName;
        string engineNote = !filtersEngineDummies
            ? ""
            : engineContentReadable
                ? "；引擎内容假人再滤掉 " + engineDummies.Count
                  + (engineDummies.Count == 0 ? "" : "：" + string.Join("、", engineDummies))
                : "；引擎内容目录无法读取，未滤";
        string officialNote = reach == Reach.Own ? "；官方包 " + officialDropped + " 条不在本扫描面" : "";
        string whereNote = where == null ? "" : "；收窄 " + beforeWhere + " → " + kept.Count;
        ctx.AddDiagnostic(RimRunnerDiagnosticKind.Coverage, label,
            "留下 " + kept.Count + "、滤掉 " + dropped.Count
            + (dropped.Count == 0 ? "" : "：" + string.Join("、", dropped))
            + officialNote + engineNote + whereNote);
        string? shortfall = kept.Count > 0
            ? null
            : "扫描面 " + typeName + " 为空（"
              + (where == null ? "" : "收窄前 " + beforeWhere + " 条、")
              + "滤掉 " + dropped.Count
              + (reach == Reach.Own ? "、官方包 " + officialDropped : "")
              + (filtersEngineDummies && engineContentReadable ? "、引擎内容假人 " + engineDummies.Count : "") + "）";
        string expectation = (reach == Reach.Own ? "本产品侧 " : "本产品侧与官方包 ") + typeName + " 的扫描面（收窄之后）不得为空";
        return new Scan<T>(kept, label, shortfall, expectation);
    }
}
