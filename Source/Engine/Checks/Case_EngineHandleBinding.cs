namespace RimRunner;

/// <summary>引擎按名绑定的游戏成员均存在。</summary>
/// <remarks>只验引擎程序集：静态只读反射句柄静态构造后不为空，静态构造不抛，[UnsafeAccessor] 强制解析不抛。跳过核对：可空声明与泛型定义上的句柄、按名探测的类型无法解析时的空句柄、泛型 [UnsafeAccessor]；载档后执行一次。</remarks>
public class Case_EngineHandleBinding : RimRunnerRegression
{
    private const string Source = "引擎承诺：引擎按名绑定的游戏成员都存在；缺失时，使用该成员的引擎功能在执行到该处时才失败";

    public override string Id => "HANDLE2";

    public override RimRunnerLevel Level => RimRunnerLevel.Assembly;

    public override void Run(RimRunnerRegressionContext ctx)
    {
        List<Type> types = AccessTools.GetTypesFromAssembly(RimRunnerDefScope.EngineAssembly).ToList();

        RimRunnerHandleCheck.HandleScan handles = RimRunnerHandleCheck.ScanHandles(types,
            RimRunnerPackages.AbsentPackageOfAssembly, new Dictionary<string, string>());
        ctx.AssertTrue("HANDLE2-Gate-HandlesChecked", handles.Checked > 0,
            "[engine] 引擎程序集里核对的不可空静态只读句柄为 0 个：" + handles.Summary,
            RimRunnerExpectationSource.Literal(Source));
        ctx.AssertNoneOffending("HANDLE2-Handles", handles.Faults,
            "[engine] 上列引擎句柄按名无法解析游戏成员，或所在类的静态构造抛出；使用这些句柄的引擎功能执行到该处即失败",
            RimRunnerExpectationSource.Literal(Source));
        ctx.AddDiagnostic(RimRunnerDiagnosticKind.Coverage, "HANDLE2-HandleScan", handles.Summary);

        RimRunnerHandleCheck.AccessorScan accessors = RimRunnerHandleCheck.ScanAccessors(types);
        ctx.AssertTrue("HANDLE2-Gate-AccessorsFound", accessors.Accessors > 0,
            "[engine] 引擎程序集里枚举到 0 个 [UnsafeAccessor] 方法",
            RimRunnerExpectationSource.Literal(Source));
        ctx.AssertNoneOffending("HANDLE2-Accessors", accessors.Faults,
            "[engine] 上列 [UnsafeAccessor] 方法的目标成员不存在，第一次调用即抛",
            RimRunnerExpectationSource.Literal(Source));
        ctx.AddDiagnostic(RimRunnerDiagnosticKind.Coverage, "HANDLE2-AccessorScan", accessors.Summary);
    }
}
