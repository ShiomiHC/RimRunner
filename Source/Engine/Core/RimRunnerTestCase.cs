namespace RimRunner;

internal static class RimRunnerKindExtensions
{
    internal static bool IsRegression(this RimRunnerKind kind) => kind == RimRunnerKind.Regression;
}

/// <summary>用例装配轴：引擎执行该用例之前要装配到哪一层。</summary>
public enum RimRunnerLevel
{
    /// <summary>只需程序集：反射、读 IL、文件 IO。</summary>
    /// <remarks>调用闭包里出现 DefDatabase、LoadedModManager、DefOf 字段或补丁表查询即属 Defs 档。</remarks>
    Assembly,
    /// <summary>需要 Def 库、Harmony 补丁表，或两者都要，不需要世界实例。</summary>
    Defs,
    /// <summary>需要已载入的世界与地图：取 pawn、推 tick、世界对象。</summary>
    /// <remarks>phase 0 开始执行前，引擎清除主图上非交棒的全部 pawn（含存档带来的殖民者与动物）、尸体、非玩家阵营产 pawn 的建筑、火与孤儿 pawn；声明叙事者照发的真实帧用例不清。</remarks>
    World
}

/// <summary>执行槽：用例在一批的执行队列里的位置，由既有声明推导。</summary>
public enum RimRunnerExecutionSlot
{
    /// <summary>单阶段、在主图上执行的普通用例。</summary>
    Normal,
    /// <summary>多阶段用例档：跨存读档屏障，批内共用一次存读档。</summary>
    CheckpointBarrier,
    /// <summary>在独立小图上执行的单阶段用例，排在单阶段与多阶段之后。</summary>
    IsolatedMap,
    /// <summary>真实帧用例，排在队列最尾，每条收尾回载基线档。</summary>
    RealFrame
}

/// <summary>参数化用例标记：一个类产多个 Id。</summary>
/// <remarks>带本特性即由类内 public static Expand() 产实例（返回 IEnumerable&lt;RimRunnerTestCase&gt;），无参构造不参与发现。</remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RimRunnerParameterizedCaseAttribute : Attribute
{
}

/// <summary>声明整个用例类或场面类依赖某个可缺席的 mod 包。</summary>
/// <remarks>名单未指定且进程未装该包时，整类不实例化，类名记进报告的 absentCases。可挂多个，随基类继承。</remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class RimRunnerRequiresPackageAttribute(string packageId) : Attribute
{
    /// <summary>依赖的 packageId。</summary>
    public string PackageId { get; } = packageId;
}

/// <summary>声明用例所属的组，供按组选批。</summary>
/// <remarks>每个产品用例类都须有组（环境体检除外），没有即编译报 RR0001。可挂多个、随基类继承。组名只许英文字母、数字、- 与 _，all 留作「不按组过滤」。按组选批时随包检查不受组过滤。</remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class RimRunnerGroupAttribute(string group) : Attribute
{
    /// <summary>组名。</summary>
    public string Group { get; } = group;
}

/// <summary>真实帧用例的声明：正文能按原版速度推 tick，且声明墙钟上限与叙事者照发。</summary>
/// <remarks>正文写 RunSteps，不写 Run。声明者排在队列最尾、收尾回载基线档、不走主图还原。</remarks>
public interface IRimRunnerRealFrameCase
{
    /// <summary>正文的墙钟上限（秒），超出记失败、停止推进。</summary>
    /// <remarks>从正文开始执行起算；须为有限正数。</remarks>
    float WallClockTimeoutSeconds { get; }

    /// <summary>开 = 在基线档原样世界上执行，叙事者照发；关（缺省）= 叙事者停用。</summary>
    /// <remarks>只许 World 级。开时跳过场景装配，殖民者 AI 与需求照走；本次载档后有用例执行过即先回载基线档；在独立小图上执行时，目标是别的图的事件在 TryExecute 处拦截、记警告。关时用例窗内入队的事件扣到收尾不发，作为残留报出。</remarks>
    bool LiveStoryteller => false;
}

/// <summary>多阶段用例的声明面：段数、屏障资源与预算。</summary>
/// <remarks>同批成员共用一次存读档：phase 0 依序执行完后存读档，续段逆序执行，排在本成员之后的成员推的 tick 落在本成员两 phase 之间。正文在 IRimRunnerPhasedCase&lt;TContext&gt; 上。</remarks>
public interface IRimRunnerPhasedCase
{
    /// <summary>总段数（≥2）。</summary>
    int PhaseCount { get; }

    /// <summary>屏障资源键：跨屏障独占的全局状态，声明同一键的两条不排进同一屏障批。</summary>
    /// <remarks>一个键代表一份带过屏障的全局状态（单例域、全局模式开关）；不占写空集合，null 登记期被拒。</remarks>
    IReadOnlyCollection<string> CheckpointResources { get; }

    /// <summary>批尾声明：true 即排到屏障批批尾，两 phase 之间不执行同批别的成员的代码。</summary>
    /// <remarks>每个屏障批至多一条声明者。</remarks>
    bool RequiresBatchTail { get; }

    /// <summary>独占批声明：true 即本成员单独成一个屏障批，别的成员不并入。</summary>
    /// <remarks>给读档期按设计必出错误级日志、用 RimRunnerAcknowledgeLog 认领它的用例：同批成员共用存读档窗，窗里的日志记到每个成员头上。每条声明者让一轮多一次存读档；并存的批尾、资源键与预算不起作用。</remarks>
    bool RequiresSoloBatch => false;

    /// <summary>各 phase 自己推进的 tick 上界，下标即 phase 号；null（缺省）按台账估。</summary>
    /// <remarks>未声明时按推进量台账近几轮全过轮次的最大值加极差估；台账为空按未知，只有不限预算的成员排在本成员前面。phase 开始执行（含场景装配）到 phase 体返回的推进超过声明判 SENTINEL_FAULT。</remarks>
    int[]? CheckpointTicks => null;

    /// <summary>两 phase 之间主图能容忍被推进的拍数上限，含读档补推；null = 未声明。</summary>
    /// <remarks>至少为 1。World 级留主图的未声明登记期被拒（声明 RequiresSoloBatch 的除外），不限写 int.MaxValue；独立小图上声明不起作用。超出先独占一批重新执行，仍超判 SENTINEL_FAULT。</remarks>
    /// <returns>图拍数上限：1 只容读档补推的那一拍，int.MaxValue 即不限。</returns>
    int? CheckpointTickBudget => null;

    /// <summary>两 phase 之间能容忍的 TicksGame 差上限，含读档补推；null（缺省）= 不限。</summary>
    /// <remarks>至少为 1，在独立小图上至少为 2（领回停放的图另补推 1 tick）。超出与图拍数超预算同一处置。</remarks>
    int? CheckpointClockBudget => null;
}

/// <summary>多阶段用例契约：跨存读档屏障，逐 phase 执行。</summary>
/// <remarks>TContext 取本档上下文：回归取 RimRunnerRegressionContext，读数取 RimRunnerReadoutContext。</remarks>
public interface IRimRunnerPhasedCase<TContext> : IRimRunnerPhasedCase where TContext : RimRunnerContext
{
    /// <summary>执行第 phase 段（从 0 起），段间存读档由框架落。</summary>
    /// <remarks>RunPhase 内不存档、不读档。</remarks>
    void RunPhase(TContext ctx, int phase);

    /// <summary>phase ≥ 1 的独立小图领回之后、引擎替它补推读档那一拍之前调；缺省不执行任何操作。</summary>
    /// <remarks>与 phase 体同一个 ctx 与用例窗，交棒对象已可取回；内不推 tick。只给独立小图上的成员：声明 MapSite.Main 的成员若写即登记期拒，实验开关强制回主图时不调、记降级。</remarks>
    void BeforeLoadTick(TContext ctx, int phase)
    {
    }
}

/// <summary>用例根类型：Id、装配层与适用性。</summary>
/// <remarks>只能经 RimRunnerRegression 或 RimRunnerReadout 派生。引擎反射发现全部非抽象子类，编译进去即登记。</remarks>
public abstract class RimRunnerTestCase
{
    internal RimRunnerTestCase(RimRunnerKind kind)
    {
        Kind = kind;
    }

    /// <summary>用例唯一 Id，也是期望表与名册的键。</summary>
    public abstract string Id { get; }

    /// <summary>性质轴：由所派生的档基类定。</summary>
    public RimRunnerKind Kind { get; }

    /// <summary>装配轴声明（必填）。</summary>
    public abstract RimRunnerLevel Level { get; }

    /// <summary>贴图档位下限：实例档位低于本值时本用例被拒。</summary>
    /// <remarks>缺省 Placeholder 即不限。名单未写档位即从 Native 起；名单声明 Placeholder 时，CLI 把档位升到本批各条下限的最大值。调用 AddScreenshot 未申报 Native 的，契约闸拒该条；只能写成常量。</remarks>
    public virtual RimRunnerTextures MinTextures => RimRunnerTextures.Placeholder;

    /// <summary>适用性：返回理由即整条不开始执行，null 即适用；每批开始执行前判定。</summary>
    /// <remarks>只许读名单与基线档决定的环境。不适用即不建条目、不入台账，报告记 Id 与理由；指定的用例不适用则拒批；抛异常或返回空白串时照建条目、判 UNCLAIMED_FAULT。</remarks>
    public virtual string? NotApplicableReason() => null;

    internal virtual RimRunnerMapSite DeclaredMapSite => RimRunnerMapSite.Default;

    internal virtual bool DeclaresQuietMachine => false;

    internal virtual bool DeclaresObservesRuntime => true;

    internal virtual bool DeclaresEmitsSamples => false;

    internal bool DeclaresLiveStoryteller => this is IRimRunnerRealFrameCase realFrame && realFrame.LiveStoryteller;

    internal bool DefaultsToIsolatedMap => RimRunnerIsolatedMap.MainByDefault(Level) == null;

    internal bool SitedOnIsolatedMap
    {
        get
        {
            RimRunnerMapSite site = DeclaredMapSite;
            return site.Kind == RimRunnerMapSite.Choice.Isolated || (site.IsDefault && DefaultsToIsolatedMap);
        }
    }

    internal bool OnIsolatedMap => SitedOnIsolatedMap && !RimRunnerForcedMainMap.Covers(this);

    internal IntVec2 IsolatedMapSize => DeclaredMapSite.Size;

    internal RimRunnerMapGround IsolatedMapGround => DeclaredMapSite.Ground;

    internal string? MainMapReason
    {
        get
        {
            if (OnIsolatedMap)
            {
                return null;
            }

            RimRunnerMapSite site = DeclaredMapSite;
            if (site.Kind == RimRunnerMapSite.Choice.Main)
            {
                return site.Reason;
            }

            return SitedOnIsolatedMap
                ? "实验开关 " + RimRunnerExperiments.ForceMainMap + " 强制回到主图"
                : RimRunnerIsolatedMap.MainByDefault(Level);
        }
    }

    /// <summary>执行槽：推导值。</summary>
    public RimRunnerExecutionSlot Slot => this is IRimRunnerRealFrameCase
            ? RimRunnerExecutionSlot.RealFrame
        : this is IRimRunnerPhasedCase
            ? RimRunnerExecutionSlot.CheckpointBarrier
            : OnIsolatedMap
                ? RimRunnerExecutionSlot.IsolatedMap
                : RimRunnerExecutionSlot.Normal;

    internal abstract RimRunnerContext NewContext();

    internal abstract void InvokeRun(RimRunnerContext ctx);

    internal abstract void InvokePhase(RimRunnerContext ctx, int phase);

    internal abstract void InvokeBeforeLoadTick(RimRunnerContext ctx, int phase);

    internal abstract IEnumerable<RimRunnerWait> InvokeSteps(RimRunnerContext ctx);

    internal bool WritesSteps => writesSteps ??= RimRunnerCaseBody.KindOf(GetType()) is var (_, kindBase, contextType)
                                                 && RimRunnerCaseBody.WritesSteps(GetType(), kindBase, contextType);

    private bool? writesSteps;
}

internal static class RimRunnerCaseBody
{
    internal static (RimRunnerKind Kind, Type Base, Type Context)? KindOf(Type type)
    {
        if (typeof(RimRunnerRegression).IsAssignableFrom(type))
            return (RimRunnerKind.Regression, typeof(RimRunnerRegression), typeof(RimRunnerRegressionContext));
        if (typeof(RimRunnerReadout).IsAssignableFrom(type))
            return (RimRunnerKind.Readout, typeof(RimRunnerReadout), typeof(RimRunnerReadoutContext));
        return null;
    }

    internal static bool WritesRun(Type type, Type kindBase, Type contextType)
    {
        System.Reflection.MethodInfo? found = type.GetMethod("Run", [contextType]);
        return found != null && found.DeclaringType != kindBase;
    }

    internal static bool WritesSteps(Type type, Type kindBase, Type contextType)
    {
        System.Reflection.MethodInfo? found = type.GetMethod("RunSteps", [contextType]);
        return found != null && found.DeclaringType != kindBase;
    }

    internal static void InvokePhase<TContext>(RimRunnerTestCase testCase, RimRunnerContext ctx, int phase)
        where TContext : RimRunnerContext
    {
        if (testCase is not IRimRunnerPhasedCase<TContext> phased)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " 用例 " + testCase.Id + " 没有实现 IRimRunnerPhasedCase<"
                                                + typeof(TContext).Name + ">");
        }

        phased.RunPhase((TContext)ctx, phase);
    }

    internal static void InvokeBeforeLoadTick<TContext>(RimRunnerTestCase testCase, RimRunnerContext ctx, int phase)
        where TContext : RimRunnerContext
    {
        if (testCase is IRimRunnerPhasedCase<TContext> phased)
        {
            phased.BeforeLoadTick((TContext)ctx, phase);
        }
    }

    internal static bool WritesBeforeLoadTick(Type type) => BeforeLoadTickBody(type) != null;

    internal static System.Reflection.MethodInfo? BeforeLoadTickBody(Type type)
    {
        if (KindOf(type) is not var (_, _, contextType))
        {
            return null;
        }

        Type contract = typeof(IRimRunnerPhasedCase<>).MakeGenericType(contextType);
        if (!contract.IsAssignableFrom(type))
        {
            return null;
        }

        System.Reflection.InterfaceMapping map = type.GetInterfaceMap(contract);
        int at = Array.IndexOf(map.InterfaceMethods, contract.GetMethod(nameof(IRimRunnerPhasedCase<RimRunnerContext>.BeforeLoadTick)));
        return at >= 0 && map.TargetMethods[at].DeclaringType != contract ? map.TargetMethods[at] : null;
    }

    internal static InvalidOperationException NoRun(RimRunnerTestCase testCase) =>
        new(RimRunnerLog.Prefix + " 用例 " + testCase.Id + " 没有正文：单段用例覆写 Run 或 RunSteps；多阶段用例的正文在 RunPhase；真实帧用例的正文在 RunSteps");
}

/// <summary>回归用例基类。</summary>
/// <remarks>正文三选一：覆写 Run，覆写 RunSteps（跨帧），或实现多阶段接口（接口取 RimRunnerRegressionContext）；声明真实帧的写 RunSteps；不合此形登记期被拒。</remarks>
public abstract class RimRunnerRegression : RimRunnerTestCase
{
    protected RimRunnerRegression() : base(RimRunnerKind.Regression)
    {
    }

    /// <summary>本用例在哪张图上执行，缺省由引擎按 Level 定（World 级上独立小图）。</summary>
    /// <remarks>在小图上时 ctx.Map 指小图、Find.CurrentMap 指主图，读 Find.CurrentMap 与相机记警告，相机跳转或直写切到小图记残留；真实帧用例的小图即当前地图。</remarks>
    public virtual RimRunnerMapSite MapSite => RimRunnerMapSite.Default;

    /// <summary>单段用例体：断言经 ctx 记下。</summary>
    /// <remarks>与 RunSteps 二选一；多阶段用例的正文在 RunPhase。</remarks>
    public virtual void Run(RimRunnerRegressionContext ctx) => throw RimRunnerCaseBody.NoRun(this);

    /// <summary>跨帧的单段用例体：yield return 一次等待即让出，断言经 ctx 记下。</summary>
    /// <remarks>与 Run 二选一。本帧做得完的同步调（burst 照用 RimRunnerTimeDriver）；只让出帧时游戏暂停，累计限 1800 帧；RimRunnerWait.Ticks 要声明 IRimRunnerRealFrameCase。</remarks>
    public virtual IEnumerable<RimRunnerWait> RunSteps(RimRunnerRegressionContext ctx) => throw RimRunnerCaseBody.NoRun(this);

    internal override RimRunnerMapSite DeclaredMapSite => MapSite;

    internal override RimRunnerContext NewContext() => new RimRunnerRegressionContext();

    internal override void InvokeRun(RimRunnerContext ctx) => Run((RimRunnerRegressionContext)ctx);

    internal override void InvokePhase(RimRunnerContext ctx, int phase) =>
        RimRunnerCaseBody.InvokePhase<RimRunnerRegressionContext>(this, ctx, phase);

    internal override void InvokeBeforeLoadTick(RimRunnerContext ctx, int phase) =>
        RimRunnerCaseBody.InvokeBeforeLoadTick<RimRunnerRegressionContext>(this, ctx, phase);

    internal override IEnumerable<RimRunnerWait> InvokeSteps(RimRunnerContext ctx) => RunSteps((RimRunnerRegressionContext)ctx);
}

/// <summary>读数用例基类。</summary>
/// <remarks>正文三选一：覆写 Run，覆写 RunSteps（跨帧），或实现多阶段接口（接口取 RimRunnerReadoutContext）；声明真实帧的写 RunSteps；不合此形登记期被拒。</remarks>
public abstract class RimRunnerReadout : RimRunnerTestCase
{
    protected RimRunnerReadout() : base(RimRunnerKind.Readout)
    {
    }

    /// <summary>本用例在哪张图上执行，缺省由引擎按 Level 定（World 级上独立小图）。</summary>
    /// <remarks>在小图上时 ctx.Map 指小图、Find.CurrentMap 指主图，读 Find.CurrentMap 与相机记警告，相机跳转或直写切到小图记残留；真实帧用例的小图即当前地图。</remarks>
    public virtual RimRunnerMapSite MapSite => RimRunnerMapSite.Default;

    /// <summary>开 = 执行到底零样本判 SENTINEL_FAULT；关时调 AddSample 照收并警告。</summary>
    /// <remarks>断在半路的、分片组里一格未认领到的不判。样本行与封批行照写，与本开关无关；--no-compat 的批不写这两种行。</remarks>
    public virtual bool EmitsSamples => false;

    /// <summary>开 = 本用例开始执行时机器上不许有别的批；CLI 据此自动独占机器。</summary>
    public virtual bool QuietMachine => false;

    /// <summary>开 = 用例体内的 UI 事件与登记 pawn 的运行期事件进观测缓冲。</summary>
    /// <remarks>每类缓冲上限 512 条，超出的丢弃并出警告，此后用例读该类缓冲即判哨兵故障。</remarks>
    public virtual bool ObservesRuntime => true;

    /// <summary>单段用例体：产出经 ctx 交出。</summary>
    /// <remarks>与 RunSteps 二选一；多阶段用例的正文在 RunPhase。</remarks>
    public virtual void Run(RimRunnerReadoutContext ctx) => throw RimRunnerCaseBody.NoRun(this);

    /// <summary>跨帧的单段用例体：yield return 一次等待即让出，产出经 ctx 交出。</summary>
    /// <remarks>与 Run 二选一。本帧做得完的同步调（burst 照用 RimRunnerTimeDriver）；只让出帧时游戏暂停，累计限 1800 帧；RimRunnerWait.Ticks 要声明 IRimRunnerRealFrameCase。</remarks>
    public virtual IEnumerable<RimRunnerWait> RunSteps(RimRunnerReadoutContext ctx) => throw RimRunnerCaseBody.NoRun(this);

    internal override RimRunnerMapSite DeclaredMapSite => MapSite;

    internal override bool DeclaresQuietMachine => QuietMachine;

    internal override bool DeclaresObservesRuntime => ObservesRuntime;

    internal override bool DeclaresEmitsSamples => EmitsSamples;

    internal override RimRunnerContext NewContext() => new RimRunnerReadoutContext();

    internal override void InvokeRun(RimRunnerContext ctx) => Run((RimRunnerReadoutContext)ctx);

    internal override void InvokePhase(RimRunnerContext ctx, int phase) =>
        RimRunnerCaseBody.InvokePhase<RimRunnerReadoutContext>(this, ctx, phase);

    internal override void InvokeBeforeLoadTick(RimRunnerContext ctx, int phase) =>
        RimRunnerCaseBody.InvokeBeforeLoadTick<RimRunnerReadoutContext>(this, ctx, phase);

    internal override IEnumerable<RimRunnerWait> InvokeSteps(RimRunnerContext ctx) => RunSteps((RimRunnerReadoutContext)ctx);
}
