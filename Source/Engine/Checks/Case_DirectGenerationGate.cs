using System.Runtime.CompilerServices;

namespace RimRunner;

/// <summary>pawn 生成必经生成根：直调 vanilla 生成器或隔离生成入口的方法须挂声明。</summary>
/// <remarks>只验测试程序集方法体（含 lambda、局部函数、状态机）对 GeneratePawn 各重载与两个隔离生成入口的 call；违例、声明陈旧、理由空白、无法归入源方法四项各记警告；因缺席包无法读取的方法无法判断声明是否陈旧。</remarks>
public class Case_DirectGenerationGate : RimRunnerRegression
{
    public override string Id => "PGEN2";
    public override RimRunnerLevel Level => RimRunnerLevel.Assembly;

    private sealed class Verdict
    {
        public readonly List<string> Offenders = [];
        public readonly List<string> Stale = [];
        public readonly List<string> BlankReason = [];
        public readonly List<string> Exempt = [];
        public readonly List<string> Unjudged = [];
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Pawn SentinelBare(PawnKindDef kind) =>
        PawnGenerator.GeneratePawn(new PawnGenerationRequest(kind));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Func<Pawn> SentinelLambda(PawnKindDef kind) =>
        () => RimRunnerPawns.GenerateIsolatedPawn(kind, null);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Pawn SentinelLocal(PawnKindDef kind)
    {
        return Make();

        Pawn Make() => RimRunnerPawns.GenerateIsolatedPawn(kind, null);
    }

    [RimRunnerDirectGeneration("PGEN2 的合规对照：带声明的 lambda 内直调")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Func<Pawn> ControlExempt(PawnKindDef kind) =>
        () => RimRunnerPawns.GenerateIsolatedPawn(kind, null);

    [RimRunnerDirectGeneration("PGEN2 的陈旧对照：带声明而无直调")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Pawn ControlStale(RimRunnerRegressionContext ctx) =>
        RimRunnerPawnGen.Subject(ctx, RimRunnerSubjectSpec.DisposableHumanlike(RimRunnerDisposableAgency.Free));

    public override void Run(RimRunnerRegressionContext ctx)
    {
        HashSet<MethodBase> gated = GatedCallees(out string? missing);
        ctx.AssertTrue("PGEN2-TargetResolved", missing == null,
            "无法解析被闸方法：" + missing,
            RimRunnerExpectationSource.Literal("IL 命中判定须有可比对的目标方法"));
        if (missing != null) return;

        Type self = typeof(Case_DirectGenerationGate);
        Assembly[] scanAssemblies = RimRunnerDefScope.GaugeAssemblies
            .Where(assembly => assembly != RimRunnerDefScope.SelfTestAssembly)
            .ToArray();
        List<MethodBase> methods = scanAssemblies.SelectMany(RimRunnerIlScan.AllMethods)
            .Where(method => Outermost(method.DeclaringType) != self).ToList();

        RimRunnerIlScan.Stats stats = new();
        Verdict verdict = Evaluate(methods.Where(method => !IsGeneratedBody(method)),
            methods.Where(IsGeneratedBody), gated, stats, out List<string> unowned);

        RimRunnerIlScanGate.AssertCoverageWarnUnread(ctx, "PGEN2", stats, scanAssemblies,
            "有测试程序集未扫入时调用点集合随之缺失，「零违例」与「未扫到」同形", "其中的直接生成调用");

        RimRunnerIlScan.Stats probeStats = new();
        Verdict probe = Evaluate(
        [
            AccessTools.Method(self, nameof(SentinelBare)),
            AccessTools.Method(self, nameof(SentinelLambda)),
            AccessTools.Method(self, nameof(SentinelLocal)),
            AccessTools.Method(self, nameof(ControlExempt)),
            AccessTools.Method(self, nameof(ControlStale)),
        ], [], gated, probeStats, out _);
        string expectedOffenders = string.Join(" / ",
            new[] { nameof(SentinelBare), nameof(SentinelLambda), nameof(SentinelLocal) }.Select(name => Name(self, name)).OrderBy(x => x));
        ctx.AssertEqual("PGEN2-SentinelCaught", expectedOffenders,
            string.Join(" / ", probe.Offenders.OrderBy(x => x)),
            RimRunnerExpectationSource.Literal("裸直调、lambda 内与局部函数内的直调须判为违例，带声明的 lambda 直调须判为合规"));
        ctx.AssertEqual("PGEN2-SentinelStale", Name(self, nameof(ControlStale)),
            string.Join(" / ", probe.Stale),
            RimRunnerExpectationSource.Literal("带声明而无直调的方法须判陈旧"));

        MethodBase unreadable = AbsentAssemblyProbeHost.HostType().GetMethod("ExemptIsolated")!;
        string unreadableName = Name(unreadable.DeclaringType, unreadable.Name);
        Verdict absentProbe = Evaluate([unreadable], [], gated,
            new RimRunnerIlScan.Stats
            {
                AbsentPackageOf = name => name == AbsentAssemblyProbeHost.AbsentName ? AbsentAssemblyProbeHost.AbsentPackage : null,
            }, out _);
        ctx.AssertEqual("PGEN2-SentinelAbsentUnjudged", "陈旧 [] 违例 [] 无法判断是否陈旧 [" + unreadableName + "]",
            "陈旧 [" + string.Join(" / ", absentProbe.Stale) + "] 违例 [" + string.Join(" / ", absentProbe.Offenders)
            + "] 无法判断是否陈旧 [" + string.Join(" / ", absentProbe.Unjudged) + "]",
            RimRunnerExpectationSource.Literal("带声明、体因缺席包无法读取的方法无法判断是否陈旧，单列"));
        Verdict failedProbe = Evaluate([unreadable], [], gated,
            new RimRunnerIlScan.Stats { AbsentPackageOf = _ => null }, out _);
        ctx.AssertEqual("PGEN2-SentinelFailedStale", unreadableName,
            string.Join(" / ", failedProbe.Stale),
            RimRunnerExpectationSource.Literal("因无法读取而缺的程序集不属缺席包时仍判陈旧"));

        ctx.WarnNoneOffending("PGEN2-NoViolation", RimRunnerWarningKey.DirectGeneration, verdict.Offenders,
            "绕过生成根（RimRunnerPawnGen）的生成缺少根的固定步骤（昵称、派系兜底、产品钩子、生成清理、回收登记）",
            RimRunnerExpectationSource.Literal("测试程序集内 pawn 生成经生成根，或带豁免声明"));
        ctx.WarnNoneOffending("PGEN2-Fresh", RimRunnerWarningKey.DirectGeneration, verdict.Stale,
            "豁免声明所在方法已无直调；豁免按方法生效",
            RimRunnerExpectationSource.Literal("豁免声明须逐条对应一个现存的直调"));
        ctx.WarnNoneOffending("PGEN2-ReasonGiven", RimRunnerWarningKey.DirectGeneration, verdict.BlankReason,
            "豁免声明的理由为空白",
            RimRunnerExpectationSource.Literal("豁免声明须写明不经生成根的理由"));
        ctx.WarnNoneOffending("PGEN2-Owned", RimRunnerWarningKey.DirectGeneration, unowned,
            "直调落在无法归入源方法的编译器生成体里，豁免声明无处可挂",
            RimRunnerExpectationSource.Literal("直调点须能归到一个源方法"));

        ctx.AddDiagnostic(RimRunnerDiagnosticKind.StaticScan, "Exempt",
            "豁免 " + verdict.Exempt.Count + " 处：" + string.Join(" / ", verdict.Exempt)
            + (verdict.Unjudged.Count == 0 ? "" : "；因缺席包无法读取、无法判断是否陈旧 " + verdict.Unjudged.Count + " 处：" + string.Join(" / ", verdict.Unjudged))
            + "；" + stats.Summary);
    }

    private static HashSet<MethodBase> GatedCallees(out string? missing)
    {
        HashSet<MethodBase> gated = [.. typeof(PawnGenerator)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name == nameof(PawnGenerator.GeneratePawn))];
        List<string> absent = [];
        if (gated.Count == 0) absent.Add("PawnGenerator.GeneratePawn");

        foreach (string name in new[] { nameof(RimRunnerPawns.GenerateIsolatedPawn), nameof(RimRunnerPawns.GenerateRaidMemberPawn) })
        {
            MethodBase method = AccessTools.Method(typeof(RimRunnerPawns), name);
            if (method == null) absent.Add("RimRunnerPawns." + name);
            else gated.Add(method);
        }

        missing = absent.Count == 0 ? null : string.Join(" / ", absent);
        return gated;
    }

    private static Verdict Evaluate(IEnumerable<MethodBase> sources, IEnumerable<MethodBase> generated,
        HashSet<MethodBase> gated, RimRunnerIlScan.Stats stats, out List<string> unowned)
    {
        Verdict verdict = new();
        HashSet<MethodBase> ownedHits = [];

        foreach (MethodBase source in sources)
        {
            if (source == null) continue;
            int absentBefore = stats.MethodsAbsentPackage;
            bool hit = CallsGated(source, gated, stats);
            foreach (MethodBase body in RimRunnerIlScan.OwnedBodies(source, stats))
            {
                if (!CallsGated(body, gated, stats)) continue;
                hit = true;
                ownedHits.Add(body);
            }

            RimRunnerDirectGenerationAttribute? exemption = source.GetCustomAttribute<RimRunnerDirectGenerationAttribute>();
            string where = Name(source.DeclaringType, source.Name);
            if (exemption == null)
            {
                if (hit) verdict.Offenders.Add(where);
                continue;
            }

            if (hit) verdict.Exempt.Add(where);
            else if (stats.MethodsAbsentPackage > absentBefore) verdict.Unjudged.Add(where);
            else verdict.Stale.Add(where);
            if (string.IsNullOrWhiteSpace(exemption.Reason)) verdict.BlankReason.Add(where);
        }

        unowned = generated
            .Where(body => body != null && !ownedHits.Contains(body) && CallsGated(body, gated, stats))
            .Select(body => Name(body.DeclaringType, body.Name))
            .ToList();
        return verdict;
    }

    private static bool CallsGated(MethodBase method, HashSet<MethodBase> gated, RimRunnerIlScan.Stats stats) =>
        RimRunnerIlScan.CallEdges(method, stats).Any(gated.Contains);

    private static bool IsGeneratedBody(MethodBase method)
    {
        for (Type? type = method.DeclaringType; type != null; type = type.DeclaringType)
        {
            if (type.GetCustomAttribute<CompilerGeneratedAttribute>() != null) return true;
        }

        return method.Name.StartsWith("<") && method.GetCustomAttribute<CompilerGeneratedAttribute>() != null;
    }

    private static Type? Outermost(Type? type)
    {
        while (type?.DeclaringType != null) type = type.DeclaringType;
        return type;
    }

    private static string Name(Type? type, string method) => (type?.FullName ?? "<null>") + "::" + method;
}
