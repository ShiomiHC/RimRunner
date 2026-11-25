namespace RimRunner;

/// <summary>产品的反射句柄均已取到成员，声明的补丁类均已挂上。</summary>
/// <remarks>只验产品与测试程序集：静态只读反射句柄静态构造后不为空，静态构造不抛出，Harmony 特性写在类上且类在补丁表里有补丁方法。跳过核对：可空声明与登记的句柄、按名探测的类型无法解析时的空句柄、手挂与只有反向补丁的补丁类；载档后执行一次。</remarks>
public class Case_HandleBinding : RimRunnerRegression
{
    private const string Source = "产品按名绑定的游戏成员缺失时，反射句柄只得到 null，补丁类未挂上，批次仍执行；"
                                  + "用到句柄或补丁的路径被跳过时不产生任何输出";

    public override string Id => "HANDLE1";

    public override RimRunnerLevel Level => RimRunnerLevel.Defs;

    public override void Run(RimRunnerRegressionContext ctx)
    {
        List<Assembly> assemblies = [.. RimRunnerDefScope.ProbeAssemblies];
        if (RimRunnerProduct.TestAssembly is { } testAssembly && !RimRunnerDefScope.IsEngineSide(testAssembly)
                                                             && !assemblies.Contains(testAssembly))
        {
            assemblies.Add(testAssembly);
        }

        List<Type> types = assemblies.SelectMany(AccessTools.GetTypesFromAssembly).ToList();
        ctx.AssertTrue("HANDLE1-Gate-TypesScanned", types.Count > 0,
            "产品程序集与测试程序集里扫描到 0 个类型（扫描范围取自产品描述符的 ProbeTypes）",
            RimRunnerExpectationSource.Literal(Source));
        if (types.Count == 0) return;

        RimRunnerProductCapabilities? capabilities = RimRunnerProduct.TryGetCapabilities();
        IReadOnlyDictionary<string, string> optionalHandles =
            capabilities?.OptionalHandles ?? new Dictionary<string, string>();
        IReadOnlyDictionary<string, string> inactivePatchClasses =
            capabilities?.InactivePatchClasses ?? new Dictionary<string, string>();

        RimRunnerHandleCheck.HandleScan handles =
            RimRunnerHandleCheck.ScanHandles(types, RimRunnerPackages.AbsentPackageOfAssembly, optionalHandles);
        ctx.AssertNoneOffending("HANDLE1-Handles", handles.Faults,
            "上列静态只读句柄按名无法解析游戏成员，或所在类的静态构造抛出",
            RimRunnerExpectationSource.Literal(Source));
        ctx.AssertNoneOffending("HANDLE1-StaleOptionalHandles",
            RimRunnerHandleCheck.StaleExemptions(optionalHandles.Keys, handles.CandidateKeys, handles.AbsentTypes),
            "上列 OptionalHandle 登记不匹配任何静态只读句柄",
            RimRunnerExpectationSource.Literal(Source));
        ctx.AddDiagnostic(RimRunnerDiagnosticKind.Coverage, "HANDLE1-HandleScan", handles.Summary);
        if (handles.AbsentNotes.Count > 0)
        {
            ctx.AddDiagnostic(RimRunnerDiagnosticKind.Coverage, "HANDLE1-HandleAbsent", string.Join("\n", handles.AbsentNotes));
        }

        if (handles.ProbeNotes.Count > 0)
        {
            ctx.AddDiagnostic(RimRunnerDiagnosticKind.Coverage, "HANDLE1-HandleProbeAbsent",
                "下列空句柄跳过核对：这些句柄所在类按名探测的类型在本进程无法解析\n" + string.Join("\n", handles.ProbeNotes));
        }

        HashSet<Type> mounted = RimRunnerHandleCheck.MountedPatchTypes(Harmony.GetAllPatchedMethods());
        ctx.AssertTrue("HANDLE1-Gate-PatchTable", mounted.Count > 0,
            "[engine] 补丁表里没有任何补丁方法，引擎自己的启动补丁也不在：挂载判据无从核对",
            RimRunnerExpectationSource.Literal(Source));
        if (mounted.Count == 0) return;

        RimRunnerHandleCheck.MountScan mounts =
            RimRunnerHandleCheck.ScanMounts(types, mounted, RimRunnerPackages.AbsentPackageOfAssembly, inactivePatchClasses);
        ctx.AssertNoneOffending("HANDLE1-Mounts", mounts.Faults,
            "上列各类带类级 Harmony 特性（含继承所得），补丁表里没有它们的补丁方法",
            RimRunnerExpectationSource.Literal(Source));
        ctx.AssertNoneOffending("HANDLE1-Orphans", mounts.Orphans,
            "上列各类的方法上有 Harmony 特性而类上没有，补丁表里也没有它们的补丁方法：PatchAll 只认类上的特性",
            RimRunnerExpectationSource.Literal(Source));
        ctx.AssertNoneOffending("HANDLE1-StaleInactivePatchClasses",
            RimRunnerHandleCheck.StaleExemptions(inactivePatchClasses.Keys, mounts.DeclaredKeys, mounts.AbsentTypes),
            "上列 InactivePatchClass 登记不匹配任何带 Harmony 特性的类",
            RimRunnerExpectationSource.Literal(Source));
        ctx.AddDiagnostic(RimRunnerDiagnosticKind.Coverage, "HANDLE1-MountScan", mounts.Summary);
        if (mounts.Unresolved.Count > 0)
        {
            ctx.AddDiagnostic(RimRunnerDiagnosticKind.Coverage, "HANDLE1-MountUnresolved", string.Join("\n", mounts.Unresolved));
        }
    }
}
