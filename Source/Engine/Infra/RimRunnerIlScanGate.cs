namespace RimRunner;

/// <summary>IL 型扫描用例的扫描面完整性闸：读取干净、程序集覆盖。</summary>
/// <remarks>传进本类读取闸的统计都算登记过；覆盖闸只防整个程序集没扫进来，名单驱动的扫描由领域闸或预期命中证明名单没塌。</remarks>
public static class RimRunnerIlScanGate
{
    /// <summary>读取失败判定：null = 通过，否则明细。</summary>
    /// <param name="stats">读取统计；调用即标为已过读取闸。</param>
    /// <param name="scanPass">非空时失败明细指明是哪一趟扫描。</param>
    public static string? ReadCleanFailure(RimRunnerIlScan.Stats stats, string? scanPass = null)
    {
        stats.ReadGated = true;
        if (stats.MethodsFailed == 0) return null;

        return stats.MethodsFailed + " 个方法" + Where(scanPass) + "无法读取 IL："
               + string.Concat(stats.Failures.Select(failure => "\n  " + failure));
    }

    private static string Where(string? scanPass) => scanPass == null ? "" : "在" + scanPass;

    internal static IEnumerable<string> ReadFailureItems(RimRunnerIlScan.Stats stats)
    {
        stats.ReadGated = true;
        return stats.Failures.Select(failure => "无法读取 IL：" + failure);
    }

    internal static void WarnUnread(RimRunnerRegressionContext ctx, string label, RimRunnerIlScan.Stats stats, string missed)
    {
        ctx.WarnNoneOffending(label, RimRunnerWarningKey.IlScanUnread, ReadFailureItems(stats),
            "无法读取 IL 的方法不进判据面，无法检出" + missed,
            RimRunnerExpectationSource.Literal("扫描面的方法读取失败须为 0"));
    }

    internal static void AssertCoverageWarnUnread(
        RimRunnerRegressionContext ctx, string keyPrefix, RimRunnerIlScan.Stats stats,
        IEnumerable<Assembly> surface, string emptyConclusionNote, string missed)
    {
        List<Assembly> assemblies = [.. surface.Distinct()];
        string? coverageFailure = CoverageFailure(stats, assemblies);
        ctx.AddDiagnostic(RimRunnerDiagnosticKind.Coverage, keyPrefix + "-ScanIntegrity", CoverageOf(stats, assemblies));
        ctx.AssertNoneOffending(keyPrefix + "-ScanIntegrity", coverageFailure == null ? [] : [coverageFailure],
            stats.Summary, RimRunnerExpectationSource.Literal(emptyConclusionNote));
        WarnUnread(ctx, keyPrefix + "-NoReadFailure", stats, missed);
    }

    /// <summary>程序集覆盖判定：null = 通过，否则明细。</summary>
    /// <remarks>surface 为空、或其中某程序集一个有体方法都没读出即不通过；该程序集读到的全是引用本轮缺席包的方法时通过。</remarks>
    public static string? CoverageFailure(RimRunnerIlScan.Stats stats, IEnumerable<Assembly> surface)
    {
        List<Assembly> assemblies = [.. surface.Distinct()];
        if (assemblies.Count == 0) return "扫描面一个程序集都没有";

        List<string> empty = [.. assemblies
            .Where(assembly => !stats.WithBodyByAssembly.ContainsKey(assembly)
                               && !stats.AbsentPackageAssemblies.Contains(assembly))
            .Select(assembly => assembly.GetName().Name ?? assembly.FullName ?? "?")];
        return empty.Count == 0 ? null : string.Join("、", empty) + " 未读出任何方法体";
    }

    /// <summary>断言一趟扫描读取干净。</summary>
    /// <param name="ctx">用例上下文。</param>
    /// <param name="key">断言名，本条断言在报告里的名字。</param>
    /// <param name="stats">这趟扫描的读取统计。</param>
    /// <param name="emptyConclusionNote">无法读取 IL 时本用例会得到的错误结论。</param>
    /// <param name="scanPass">非空时失败明细指明是哪一趟扫描。</param>
    public static void AssertReadClean(
        RimRunnerRegressionContext ctx, string key, RimRunnerIlScan.Stats stats,
        string emptyConclusionNote, string? scanPass = null)
    {
        stats.ReadGated = true;
        ctx.AssertNoneOffending(key, stats.Failures, "上列方法" + Where(scanPass) + "无法读取 IL",
            RimRunnerExpectationSource.Literal(emptyConclusionNote));
    }

    /// <summary>断言两趟扫描都读取干净（合一条 key）。</summary>
    /// <param name="ctx">用例上下文。</param>
    /// <param name="key">断言名，本条断言在报告里的名字。</param>
    /// <param name="emptyConclusionNote">无法读取 IL 时本用例会得到的错误结论。</param>
    /// <param name="labelA">第一趟扫描的名字。</param>
    /// <param name="a">第一趟扫描的统计。</param>
    /// <param name="labelB">第二趟扫描的名字。</param>
    /// <param name="b">第二趟扫描的统计。</param>
    public static void AssertReadClean(
        RimRunnerRegressionContext ctx, string key, string emptyConclusionNote,
        string labelA, RimRunnerIlScan.Stats a,
        string labelB, RimRunnerIlScan.Stats b)
    {
        a.ReadGated = true;
        b.ReadGated = true;
        ctx.AssertNoneOffending(key,
            a.Failures.Select(failure => labelA + "：" + failure).Concat(b.Failures.Select(failure => labelB + "：" + failure)),
            "上列方法无法读取 IL（" + labelA + " " + a.MethodsFailed + " 个、" + labelB + " " + b.MethodsFailed + " 个）",
            RimRunnerExpectationSource.Literal(emptyConclusionNote));
    }

    /// <summary>断言一趟扫描读取干净且覆盖 surface 里每个程序集。</summary>
    /// <remarks>surface 传这趟扫描遍历的程序集集合；连带记一条 Coverage 诊断：逐程序集有体数。</remarks>
    /// <param name="ctx">用例上下文。</param>
    /// <param name="key">断言名，本条断言在报告里的名字。</param>
    /// <param name="stats">这趟扫描的读取统计。</param>
    /// <param name="surface">这趟扫描遍历的程序集集合。</param>
    /// <param name="emptyConclusionNote">无法读取 IL 时本用例会得到的错误结论。</param>
    public static void AssertScanIntegrity(
        RimRunnerRegressionContext ctx, string key, RimRunnerIlScan.Stats stats,
        IEnumerable<Assembly> surface, string emptyConclusionNote)
    {
        List<Assembly> assemblies = [.. surface.Distinct()];
        string? coverageFailure = CoverageFailure(stats, assemblies);
        ctx.AddDiagnostic(RimRunnerDiagnosticKind.Coverage, key, CoverageOf(stats, assemblies));
        ctx.AssertNoneOffending(key,
            (coverageFailure == null ? [] : new[] { coverageFailure }).Concat(ReadFailureItems(stats)),
            stats.Summary, RimRunnerExpectationSource.Literal(emptyConclusionNote));
    }

    internal static string CoverageOf(RimRunnerIlScan.Stats stats, IEnumerable<Assembly> surface)
    {
        return string.Join(" · ", surface.Select(assembly =>
            (assembly.GetName().Name ?? "?") + " 有体 "
            + (stats.WithBodyByAssembly.TryGetValue(assembly, out int count) ? count : 0)
            + (stats.AbsentPackageAssemblies.Contains(assembly) ? "（含缺席包方法）" : "")));
    }
}
