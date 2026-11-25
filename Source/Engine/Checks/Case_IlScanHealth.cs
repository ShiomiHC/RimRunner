namespace RimRunner;

/// <summary>产品扫描面的读取健康。</summary>
/// <remarks>只验产品描述符给出的探针程序集：未指定探针程序集即失败；逐方法读 IL 有读取失败即出 ILSCAN-UNREAD 警告。</remarks>
public class Case_IlScanHealth : RimRunnerRegression
{
    public override string Id => "ILS1";

    public override RimRunnerLevel Level => RimRunnerLevel.Assembly;

    public override void Run(RimRunnerRegressionContext ctx)
    {
        RimRunnerIlScan.Stats full = new();
        foreach (MethodBase m in RimRunnerIlScan.AllOurMethods()) RimRunnerIlScan.Instructions(m, full);

        string? readFailure = RimRunnerIlScanGate.ReadCleanFailure(full);
        ctx.WarnNoneOffending("ILS1-NoReadFailure", RimRunnerWarningKey.IlScanUnread,
            readFailure == null ? null : full.Failures,
            "无法读取 IL 的方法不进判据面，IL 型扫描用例无法检出其中的违例",
            RimRunnerExpectationSource.Literal("产品探针程序集的方法读取失败须为 0"));

        int probeAssemblies = RimRunnerIlScan.OurAssemblies().Count();
        ctx.AssertTrue("ILS1-ProbeAssembliesPresent", probeAssemblies >= 1,
            "扫描面为空：探针程序集 0 个",
            RimRunnerExpectationSource.Literal("扫描面取自描述符 ProbeTypes 所在的程序集"));

        ctx.AddDiagnostic(RimRunnerDiagnosticKind.Coverage, "ILS1-ScanHealth", "产品 " + full.Summary);
    }
}
