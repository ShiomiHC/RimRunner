using System.Runtime.CompilerServices;

namespace RimRunner;

/// <summary>调用点守卫的扫描面：产品代码、量具（测试与引擎），可组合。</summary>
[Flags]
public enum RimRunnerScanSurface
{
    /// <summary>产品代码：描述符 ProbeTypes 各类型所在程序集。</summary>
    Product = 1,

    /// <summary>量具：引擎程序集、测试程序集与装载时的引擎自测。</summary>
    Gauge = 2,
}

/// <summary>调用点守卫的被调方：具体重载、类型族上的同名方法，或整个类型。</summary>
public sealed class RimRunnerCalleeMatch
{
    private enum Shape { Exact, Named, AnyMemberOf }

    private readonly Shape shape;
    private readonly MethodBase?[] overloads;
    private readonly Type? type;
    private readonly string? methodName;

    private readonly HashSet<(Module, int)> exactKeys;

    private RimRunnerCalleeMatch(Shape shape, MethodBase?[]? overloads, Type? type, string? methodName)
    {
        this.shape = shape;
        this.overloads = overloads ?? [];
        this.type = type;
        this.methodName = methodName;
        exactKeys = [.. this.overloads.OfType<MethodBase>().Select(Key)];
    }

    /// <summary>钉具体重载（可多个），按方法身份比。</summary>
    /// <remarks>泛型类型与泛型方法的各个实例化都归回定义比，与定义命中同一个。</remarks>
    public static RimRunnerCalleeMatch Exact(params MethodBase?[] overloads) =>
        new(Shape.Exact, overloads, null, null);

    /// <summary>该类型及其子类上此名的全部重载。</summary>
    /// <remarks>族为接口时含实现它的类型；族根以及族根同程序集的子类都没声明此名时 TargetResolved 判红。</remarks>
    public static RimRunnerCalleeMatch Named(Type? declaringFamily, string? methodName) =>
        new(Shape.Named, null, declaringFamily, methodName);

    /// <summary>整个类型禁用：其上声明的任一方法与构造函数。</summary>
    public static RimRunnerCalleeMatch AnyMemberOf(Type? type) =>
        new(Shape.AnyMemberOf, null, type, null);

    /// <summary>被调方的可读描述，进断言文案。</summary>
    public override string ToString()
    {
        return shape switch
        {
            Shape.Exact => string.Join(" / ", overloads.Select(method => method == null ? "<null>" : Describe(method))),
            Shape.Named => (type == null ? "<null>" : type.FullName) + " 族::" + methodName,
            _ => (type == null ? "<null>" : type.FullName) + " 的全部成员",
        };
    }

    internal string? ResolveFailure()
    {
        switch (shape)
        {
            case Shape.Exact:
                if (overloads.Length == 0) return "Exact 未指定任何重载";
                int nullAt = Array.FindIndex(overloads, method => method == null);
                return nullAt < 0 ? null : "无法解析第 " + (nullAt + 1) + " 个重载";
            case Shape.Named:
                if (type == null) return "Named 的类型为 null";
                if (string.IsNullOrEmpty(methodName)) return "Named 的方法名为空";
                return FamilyDeclares(type, methodName) ? null
                    : type.FullName + " 及其同程序集子类上没有名为 " + methodName + " 的方法";
            default:
                if (type == null) return "AnyMemberOf 的类型为 null";
                return DeclaredMembers(type).Any() ? null : type.FullName + " 上没有任何方法";
        }
    }

    internal bool Matches(MethodBase edge)
    {
        if (edge == null) return false;
        return shape switch
        {
            Shape.Exact => exactKeys.Contains(Key(edge)),
            Shape.Named => edge.Name == methodName && InFamily(edge.DeclaringType, type!),
            _ => edge.DeclaringType != null && Definition(edge.DeclaringType) == Definition(type!),
        };
    }

    private static (Module, int) Key(MethodBase method)
    {
        MethodBase normalized = RimRunnerIlScan.Normalize(method);
        return (normalized.Module, normalized.MetadataToken);
    }

    private static Type Definition(Type t) => t.IsGenericType ? t.GetGenericTypeDefinition() : t;

    private static bool InFamily(Type? declaring, Type family)
    {
        if (declaring == null) return false;
        Type familyDefinition = Definition(family);
        for (Type? t = declaring; t != null; t = t.BaseType)
        {
            if (Definition(t) == familyDefinition) return true;
        }

        return family.IsInterface && declaring.GetInterfaces().Any(i => Definition(i) == familyDefinition);
    }

    private static bool FamilyDeclares(Type family, string name)
    {
        if (DeclaredMembers(family).Any(method => method.Name == name)) return true;
        Type[] types;
        try
        {
            types = family.Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            types = exception.Types.OfType<Type>().ToArray();
        }

        return types.Any(t => t != family && InFamily(t, family) && DeclaredMembers(t).Any(method => method.Name == name));
    }

    private static IEnumerable<MethodBase> DeclaredMembers(Type t)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                                   | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        return t.GetMethods(flags).Cast<MethodBase>().Concat(t.GetConstructors(flags));
    }

    internal static string Describe(MethodBase method) =>
        (method.DeclaringType?.FullName ?? "<null>") + "::" + method.Name;
}

/// <summary>调用点守卫的一行：被调方、后果、允许的调用方、哨兵与对照。</summary>
public sealed class RimRunnerCallSiteRule
{
    internal readonly string Name;
    internal readonly RimRunnerCalleeMatch Callee;
    internal readonly string Consequence;
    internal readonly List<(MethodBase? Caller, string Reason)> Allowed = [];
    internal readonly MethodBase? Sentinel;
    internal readonly MethodBase? Control;

    private RimRunnerCallSiteRule(
        string name, RimRunnerCalleeMatch callee, string consequence, MethodBase? sentinel, MethodBase? control)
    {
        Name = name;
        Callee = callee;
        Consequence = consequence;
        Sentinel = sentinel;
        Control = control;
    }

    /// <summary>立一行禁调，连同哨兵（须命中）与对照（须不命中）。</summary>
    /// <remarks>name 进断言 key，consequence 是违反的后果，为空出警告。哨兵是自造的违例方法，对照是只走正路的方法；原语自动把两者的宿主剔出扫描面；哨兵或对照为 null 时 TargetResolved 判红。</remarks>
    public static RimRunnerCallSiteRule Forbid(
        string name, RimRunnerCalleeMatch callee, string consequence, MethodBase? sentinel, MethodBase? control)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("调用点守卫行名为空", nameof(name));
        if (callee == null) throw new ArgumentNullException(nameof(callee));
        if (string.IsNullOrWhiteSpace(consequence))
        {
            RimRunnerWarnings.Warn(RimRunnerWarningKey.EmptyReason, "调用点守卫行 " + name + " 未写后果");
            consequence = "（未写后果）";
        }

        return new RimRunnerCallSiteRule(name, callee, consequence, sentinel, control);
    }

    /// <summary>允许一个调用方（连同其 lambda / 迭代器体），reason 写为什么允许。</summary>
    /// <remarks>reason 为空出警告；caller 为 null 时 TargetResolved 判红；允许的方法连同其 lambda 都已不调被调方时 AllowedStillCalls 判红，体因引用缺席包无法读取的无法判断是否陈旧。</remarks>
    public RimRunnerCallSiteRule AllowCaller(MethodBase? caller, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            RimRunnerWarnings.Warn(RimRunnerWarningKey.EmptyReason, "调用点守卫行 " + Name + " 的允许项未写理由");
            reason = "（未写理由）";
        }

        Allowed.Add((caller, reason));
        return this;
    }

    internal string? ResolveFailure()
    {
        List<string> problems = [];
        string? callee = Callee.ResolveFailure();
        if (callee != null) problems.Add("被调方：" + callee);
        for (int i = 0; i < Allowed.Count; i++)
        {
            if (Allowed[i].Caller == null) problems.Add("无法解析第 " + (i + 1) + " 个允许调用方");
        }

        if (Sentinel == null) problems.Add("无法解析哨兵");
        if (Control == null) problems.Add("无法解析对照");
        return problems.Count == 0 ? null : string.Join("；", problems);
    }
}

/// <summary>调用点守卫一趟的结果：各行命中的调用方与本趟读取统计。</summary>
public sealed class RimRunnerCallSiteResult
{
    internal readonly Dictionary<string, List<MethodBase>> callers = new(StringComparer.Ordinal);

    /// <summary>本趟主扫描面的读取统计。</summary>
    public RimRunnerIlScan.Stats Stats { get; internal set; } = null!;

    /// <summary>某行在扫描面内命中的调用方（已归回源方法）。</summary>
    /// <returns>含允许的调用方；该行 TargetResolved 判红或 ruleName 不是本趟的行名时为空表。</returns>
    public IReadOnlyList<MethodBase> Callers(string ruleName) =>
        callers.TryGetValue(ruleName, out List<MethodBase>? list) ? list : (IReadOnlyList<MethodBase>)[];
}

/// <summary>调用点守卫（禁调 / 调用方白名单）：某方法只许这几处调用、本产品侧不许直调。</summary>
public static class RimRunnerCallSiteGuard
{
    /// <summary>扫一遍扫描面，按各行发断言并交回命中结果。</summary>
    /// <remarks>每行发 TargetResolved、SentinelCaught、ControlClean、NoViolation；ScanIntegrity 与 NoReadFailure 各共一条。编译器生成体无法归入源方法时照列，须挪进普通方法。</remarks>
    public static RimRunnerCallSiteResult Run(
        RimRunnerRegressionContext ctx, string keyPrefix, RimRunnerScanSurface surface,
        params RimRunnerCallSiteRule[] rules) =>
        Run(ctx, keyPrefix, surface, (string?)null, rules);

    internal static RimRunnerCallSiteResult RunWarned(
        RimRunnerRegressionContext ctx, string keyPrefix, RimRunnerScanSurface surface, string violationWarningKey,
        params RimRunnerCallSiteRule[] rules) =>
        Run(ctx, keyPrefix, surface, violationWarningKey, rules);

    private static RimRunnerCallSiteResult Run(
        RimRunnerRegressionContext ctx, string keyPrefix, RimRunnerScanSurface surface, string? violationWarningKey,
        RimRunnerCallSiteRule[] rules)
    {
        if (string.IsNullOrWhiteSpace(keyPrefix)) throw new ArgumentException("调用点守卫的 key 前缀为空", nameof(keyPrefix));
        if (rules == null || rules.Length == 0) throw new ArgumentException("调用点守卫没有任何行", nameof(rules));
        if (rules.Any(rule => rule == null)) throw new ArgumentNullException(nameof(rules), "调用点守卫的行含 null");
        string? duplicate = rules.GroupBy(rule => rule.Name).Where(g => g.Count() > 1).Select(g => g.Key).FirstOrDefault();
        if (duplicate != null) throw new ArgumentException("调用点守卫行名重复：" + duplicate, nameof(rules));

        RimRunnerCallSiteResult result = new();
        List<RimRunnerCallSiteRule> live = [];
        foreach (RimRunnerCallSiteRule rule in rules)
        {
            string? failure = rule.ResolveFailure();
            ctx.AssertTrue(keyPrefix + "-" + rule.Name + "-TargetResolved", failure == null, failure ?? "",
                RimRunnerExpectationSource.Literal("被调方、允许项、哨兵与对照都取得到，IL 命中判定才有可比对的目标"));
            result.callers[rule.Name] = [];
            if (failure == null) live.Add(rule);
        }

        HashSet<Type> probeHosts = [.. rules
            .SelectMany(rule => new[] { rule.Sentinel, rule.Control })
            .Select(method => method?.DeclaringType)
            .OfType<Type>()];

        List<Assembly> assemblies = Assemblies(surface);
        if (violationWarningKey != null && RimRunnerDefScope.SelfTestAssembly is { } selfTestAssembly)
        {
            assemblies.Remove(selfTestAssembly);
        }
        RimRunnerIlScan.Stats stats = new();
        Dictionary<RimRunnerCallSiteRule, List<MethodBase>> hits = live.ToDictionary(rule => rule, _ => new List<MethodBase>());
        foreach (Assembly assembly in assemblies)
        {
            foreach (MethodBase method in RimRunnerIlScan.AllMethods(assembly))
            {
                if (HostedBy(method.DeclaringType, probeHosts)) continue;
                List<MethodBase> edges = RimRunnerIlScan.CallEdges(method, stats).ToList();
                foreach (RimRunnerCallSiteRule rule in live)
                {
                    if (edges.Any(rule.Callee.Matches)) hits[rule].Add(method);
                }
            }
        }

        result.Stats = stats;

        ctx.AssertNoneOffending(keyPrefix + "-ScanIntegrity", IntegrityProblems(surface, assemblies, stats), stats.Summary,
            RimRunnerExpectationSource.Literal("扫描面每个程序集都读出方法体；否则「零违例」与「未扫到」同形"));
        RimRunnerIlScanGate.WarnUnread(ctx, keyPrefix + "-NoReadFailure", stats, "其中对被调方的直调");

        List<string> unjudgedAllowed = [];
        foreach (RimRunnerCallSiteRule rule in live)
        {
            string key = keyPrefix + "-" + rule.Name;
            RimRunnerIlScan.Stats probeStats = new();

            MethodBase sentinel = rule.Sentinel!;
            MethodBase control = rule.Control!;
            List<string> sentinelHit = ProbeHits(rule, sentinel, probeStats);
            ctx.AssertTrue(key + "-SentinelCaught", sentinelHit.Count > 0,
                "已知违例哨兵 " + RimRunnerCalleeMatch.Describe(sentinel) + " 未被判出：" + probeStats.Summary,
                RimRunnerExpectationSource.Literal("体内调 " + rule.Callee + " 的哨兵须被判出"));
            List<string> controlHit = ProbeHits(rule, control, probeStats);
            ctx.AssertTrue(key + "-ControlClean", controlHit.Count == 0,
                "合规对照被判出：" + string.Join(" / ", controlHit),
                RimRunnerExpectationSource.Literal("只走正路的对照不得判出"));

            List<string> offenders = Offenders(rule, hits[rule], probeStats, out List<MethodBase> owners);
            result.callers[rule.Name] = owners;
            RimRunnerExpectationSource violationSource =
                RimRunnerExpectationSource.Literal("扫描面内对 " + rule.Callee + " 的直调只出自允许的调用方");
            if (violationWarningKey == null)
                ctx.AssertNoneOffending(key + "-NoViolation", offenders, rule.Consequence, violationSource);
            else
                ctx.WarnNoneOffending(key + "-NoViolation", violationWarningKey, offenders, rule.Consequence, violationSource);

            if (rule.Allowed.Count > 0)
            {
                ctx.AssertNoneOffending(key + "-AllowedStillCalls", StaleAllowed(rule, probeStats, out List<string> unjudged),
                    "允许项已不再调用被调方；同一方法里之后新写的直调仍按该允许项通过",
                    RimRunnerExpectationSource.Literal("允许名单每一项都对应一个现存的调用"));
                foreach (string entry in unjudged) unjudgedAllowed.Add(rule.Name + "：" + entry);
            }
        }

        ctx.AddDiagnostic(RimRunnerDiagnosticKind.StaticScan, keyPrefix + "-Scan",
            RimRunnerIlScanGate.CoverageOf(stats, assemblies)
            + (unjudgedAllowed.Count == 0 ? "" : "；因缺席包无法读取、无法判断是否陈旧的允许项 " + unjudgedAllowed.Count + " 处：" + string.Join(" / ", unjudgedAllowed))
            + "；" + stats.Summary);
        return result;
    }

    internal static List<Assembly> Assemblies(RimRunnerScanSurface surface)
    {
        List<Assembly> assemblies = [];
        if ((surface & RimRunnerScanSurface.Product) != 0) assemblies.AddRange(RimRunnerDefScope.ProbeAssemblies);
        if ((surface & RimRunnerScanSurface.Gauge) != 0) assemblies.AddRange(RimRunnerDefScope.GaugeAssemblies);
        return assemblies.Distinct().ToList();
    }

    internal static List<string> IntegrityProblems(
        RimRunnerScanSurface surface, IReadOnlyCollection<Assembly> assemblies, RimRunnerIlScan.Stats stats)
    {
        List<string> problems = [];
        if ((surface & (RimRunnerScanSurface.Product | RimRunnerScanSurface.Gauge)) == 0) problems.Add("扫描面参数为空");
        string? coverage = RimRunnerIlScanGate.CoverageFailure(stats, assemblies);
        if (coverage != null) problems.Add(coverage);
        return problems;
    }

    internal static List<string> ProbeHits(RimRunnerCallSiteRule rule, MethodBase probe, RimRunnerIlScan.Stats stats)
    {
        List<string> found = [];
        foreach (MethodBase body in WithOwned(probe, stats))
        {
            if (RimRunnerIlScan.CallEdges(body, stats).Any(rule.Callee.Matches))
                found.Add(RimRunnerCalleeMatch.Describe(body));
        }

        return found;
    }

    internal static List<string> Offenders(
        RimRunnerCallSiteRule rule, IEnumerable<MethodBase> hitBodies, RimRunnerIlScan.Stats stats,
        out List<MethodBase> owners)
    {
        HashSet<MethodBase> allowed = [];
        foreach ((MethodBase? caller, _) in rule.Allowed)
        {
            if (caller == null) continue;
            allowed.UnionWith(WithOwned(caller, stats));
        }

        Dictionary<Type, Dictionary<MethodBase, HashSet<MethodBase>>> ownedByType = [];
        SortedSet<string> offenders = new(StringComparer.Ordinal);
        owners = [];
        foreach (MethodBase body in hitBodies)
        {
            MethodBase? owner = IsGeneratedBody(body) ? OwnerOf(body, stats, ownedByType) : body;
            if (owner != null && !owners.Contains(owner)) owners.Add(owner);
            if (allowed.Contains(body)) continue;
            offenders.Add(owner == null
                ? RimRunnerCalleeMatch.Describe(body) + "（编译器生成体，无法归入源方法）"
                : RimRunnerCalleeMatch.Describe(owner));
        }

        return offenders.ToList();
    }

    internal static List<string> StaleAllowed(
        RimRunnerCallSiteRule rule, RimRunnerIlScan.Stats stats, out List<string> unjudged)
    {
        List<string> stale = [];
        unjudged = [];
        foreach ((MethodBase? caller, string reason) in rule.Allowed)
        {
            if (caller == null) continue;
            int absentBefore = stats.MethodsAbsentPackage;
            if (ProbeHits(rule, caller, stats).Count > 0) continue;
            string where = RimRunnerCalleeMatch.Describe(caller) + "（理由：" + reason + "）";
            if (stats.MethodsAbsentPackage > absentBefore) unjudged.Add(where);
            else stale.Add(where);
        }

        return stale;
    }

    private static IEnumerable<MethodBase> WithOwned(MethodBase source, RimRunnerIlScan.Stats stats) =>
        new[] { source }.Concat(RimRunnerIlScan.OwnedBodies(source, stats));

    private static bool IsGeneratedBody(MethodBase method)
    {
        for (Type? t = method.DeclaringType; t != null; t = t.DeclaringType)
        {
            if (t.IsDefined(typeof(CompilerGeneratedAttribute), false)) return true;
        }

        return method.Name.StartsWith("<") && method.IsDefined(typeof(CompilerGeneratedAttribute), false);
    }

    private static MethodBase? OwnerOf(
        MethodBase body, RimRunnerIlScan.Stats stats,
        Dictionary<Type, Dictionary<MethodBase, HashSet<MethodBase>>> ownedByType)
    {
        Type? outer = body.DeclaringType;
        while (outer != null && outer.IsDefined(typeof(CompilerGeneratedAttribute), false)) outer = outer.DeclaringType;
        if (outer == null) return null;

        if (!ownedByType.TryGetValue(outer, out Dictionary<MethodBase, HashSet<MethodBase>>? owned))
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                                       | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            owned = outer.GetMethods(flags).Cast<MethodBase>().Concat(outer.GetConstructors(flags))
                .Where(method => !IsGeneratedBody(method))
                .ToDictionary(method => method, method => RimRunnerIlScan.OwnedBodies(method, stats));
            ownedByType[outer] = owned;
        }

        return owned.FirstOrDefault(entry => entry.Value.Contains(body)).Key;
    }

    private static bool HostedBy(Type? type, HashSet<Type> hosts)
    {
        for (Type? t = type; t != null; t = t.DeclaringType)
        {
            if (hosts.Contains(t)) return true;
        }

        return false;
    }
}
