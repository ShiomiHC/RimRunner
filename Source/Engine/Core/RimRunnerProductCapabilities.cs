namespace RimRunner;

/// <summary>产品能力的登记面，只在描述符的 Configure 里能取得。</summary>
/// <remarks>列表类可多次登记、累加，单值类登记两次即抛；参数为 null 或空串即抛；Configure 返回后再登记即抛；登记的委托调用时抛出，引擎改抛点名登记项与委托方法的异常，原异常作 InnerException。</remarks>
public sealed class RimRunnerProductCapabilities
{
    private readonly Dictionary<string, string[]> caseKeysByFile = new(StringComparer.Ordinal);
    private readonly List<Type> healingCompTypes = [];
    private readonly List<Type> scenarioTypes = [];
    private readonly List<Action<Pawn>> generatedHooks = [];
    private readonly List<Action<Pawn>> spawnedHooks = [];
    private readonly List<Func<RimRunnerContext, IEnumerable<RimRunnerLiveFingerprint>>> fingerprintSources = [];
    private readonly List<Action<IDictionary<string, string>>> worldPrinters =
        [];
    private readonly Dictionary<Type, string[]> exemptionsByComponent = [];
    private readonly List<Action> sweepers = [];
    private readonly List<KeyValuePair<string, Func<Ability, float>>> activationMeters = [];
    private readonly List<Func<Map, IntVec3, Vector3?>> drawnCellLocators = [];
    private readonly Dictionary<string, string> optionalHandles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> inactivePatchClasses = new(StringComparer.Ordinal);
    private readonly List<string> names = [];
    private Func<Pawn, string>? pawnDiagnostics;
    private bool frozen;

    internal RimRunnerProductCapabilities()
    {
    }

    /// <summary>登记一份期望表及其 cases 允许的列。</summary>
    /// <remarks>cases 非空而未登记的期望表加载即抛；登记解析器不认的列也抛。</remarks>
    /// <param name="fileId">期望表 id，即文件名去掉扩展名。</param>
    /// <param name="columns">该表 cases 允许的列名；只有 params 的表不传。</param>
    /// <exception cref="ArgumentException">同一 fileId 登记两次时抛。</exception>
    public void ExpectationCaseKeys(string fileId, params string[] columns)
    {
        Open(nameof(ExpectationCaseKeys));
        RequireText(fileId, nameof(ExpectationCaseKeys), nameof(fileId));
        if (columns == null || columns.Any(string.IsNullOrEmpty))
        {
            throw Invalid(nameof(ExpectationCaseKeys), "期望表 " + fileId + " 的列名列表为 null 或含空列名");
        }

        if (caseKeysByFile.ContainsKey(fileId))
        {
            throw Invalid(nameof(ExpectationCaseKeys), "期望表 " + fileId + " 登记两次");
        }

        caseKeysByFile[fileId] = (string[])columns.Clone();
        names.Add(nameof(ExpectationCaseKeys) + ":" + fileId);
    }

    /// <summary>登记 DPA 的 Analyzer.xml 里本产品的顶层节点名。</summary>
    /// <remarks>不登记即没有 DPA 节点名，profiler 相关能力对本产品不可用。</remarks>
    public void ProfilerEntryNode(string nodeName)
    {
        Open(nameof(ProfilerEntryNode));
        RequireText(nodeName, nameof(ProfilerEntryNode), nameof(nodeName));
        if (ProfilerEntryNodeName != null)
        {
            throw Invalid(nameof(ProfilerEntryNode), "登记两次");
        }

        ProfilerEntryNodeName = nodeName;
        names.Add(nameof(ProfilerEntryNode) + ":" + nodeName);
    }

    /// <summary>登记会实时削减 injury severity 的 HediffComp 类型。</summary>
    /// <remarks>带这些 comp 的 hediff 整个不记 injuryPeaks；漏列时 peak 采样静默停摆，名单与期望算法须同处维护。</remarks>
    public void HealingHediffComps(params Type[] compTypes)
    {
        Open(nameof(HealingHediffComps));
        if (compTypes == null || compTypes.Length == 0)
        {
            throw Invalid(nameof(HealingHediffComps), "类型列表为 null 或为空");
        }

        foreach (Type type in compTypes)
        {
            if (type == null || !typeof(HediffComp).IsAssignableFrom(type))
            {
                throw Invalid(nameof(HealingHediffComps), (type?.FullName ?? "null") + " 不是 HediffComp 的子类");
            }
        }

        healingCompTypes.AddRange(compTypes);
        names.Add(nameof(HealingHediffComps));
    }

    /// <summary>登记 pawn 的产品诊断，进 live hediffs 的 productDiagnostics。</summary>
    /// <remarks>不登记时 productDiagnostics 恒为 null。</remarks>
    /// <param name="describe">给一个 pawn 返回诊断文本；没有诊断文本时返回 null。</param>
    public void PawnDiagnostics(Func<Pawn, string> describe)
    {
        Open(nameof(PawnDiagnostics));
        RequireValue(describe, nameof(PawnDiagnostics), nameof(describe));
        if (pawnDiagnostics != null)
        {
            throw Invalid(nameof(PawnDiagnostics), "登记两次");
        }

        pawnDiagnostics = describe;
        names.Add(nameof(PawnDiagnostics));
    }

    /// <summary>登记一个共享场景装配器。</summary>
    /// <remarks>引擎在主线程首次装配时实例化并缓存，登记期不构造；不登记即空名册，测试程序集里的场景子类未登记时本批首次装配判哨兵故障并点名。</remarks>
    public void Scenario<T>() where T : RimRunnerScenario, new()
    {
        Open(nameof(Scenario));
        if (scenarioTypes.Contains(typeof(T)))
        {
            throw Invalid(nameof(Scenario), typeof(T).Name + " 登记两次");
        }

        scenarioTypes.Add(typeof(T));
        names.Add(nameof(Scenario) + ":" + typeof(T).Name);
    }

    /// <summary>登记引擎单个 pawn 生成入口在定派系之后、落图之前调的钩子。</summary>
    /// <remarks>只有靶与被测体的生成经过本钩子；对非本产品种族的 pawn 须无操作。</remarks>
    public void OnPawnGenerated(Action<Pawn> hook)
    {
        Open(nameof(OnPawnGenerated));
        RequireValue(hook, nameof(OnPawnGenerated), nameof(hook));
        generatedHooks.Add(hook);
        names.Add(nameof(OnPawnGenerated));
    }

    /// <summary>登记引擎各生成入口落图之后、交给用例之前调的钩子。</summary>
    /// <remarks>靶、被测体、袭击成员都经过本钩子；对非本产品种族的 pawn 须无操作。钩子在落图并做完生成清理之后调用，早于施加行动档、需求、hediff 屏蔽等其余项。</remarks>
    public void OnPawnSpawned(Action<Pawn> hook)
    {
        Open(nameof(OnPawnSpawned));
        RequireValue(hook, nameof(OnPawnSpawned), nameof(hook));
        spawnedHooks.Add(hook);
        names.Add(nameof(OnPawnSpawned));
    }

    /// <summary>登记产品失败指纹源：用例收尾前调，每条进 failureFingerprints，主体与正文分开。</summary>
    /// <remarks>只读现场；PASS 的用例不发布。</remarks>
    public void LiveFingerprints(Func<RimRunnerContext, IEnumerable<RimRunnerLiveFingerprint>> source)
    {
        Open(nameof(LiveFingerprints));
        RequireValue(source, nameof(LiveFingerprints), nameof(source));
        fingerprintSources.Add(source);
        names.Add(nameof(LiveFingerprints));
    }

    /// <summary>登记世界指纹的产品段，往指纹表追加产品自己的全局状态。</summary>
    /// <remarks>场面立场前后、每次点步骤前后各调一次，局部场面清理后再调一次。执行批次段的主图占用起止各调一次，该段只记日志与失败指纹。键带产品前缀，值是现值的稳定文本，没有的项不出键。只读现场。不登记则场面比对与执行批次段比对都不含产品状态。</remarks>
    public void WorldPrint(Action<IDictionary<string, string>> append)
    {
        Open(nameof(WorldPrint));
        RequireValue(append, nameof(WorldPrint), nameof(append));
        worldPrinters.Add(append);
        names.Add(nameof(WorldPrint));
    }

    /// <summary>登记组件状态比对里逐 tick 自然变化的存档元素。</summary>
    /// <remarks>只豁免列出的元素；同一组件只登记一次。</remarks>
    /// <param name="componentType">组件类型。</param>
    /// <param name="elements">该组件存档里的顶层元素名（Scribe 的 label）。</param>
    public void ComponentStateExemption(Type componentType, params string[] elements)
    {
        Open(nameof(ComponentStateExemption));
        RequireValue(componentType, nameof(ComponentStateExemption), nameof(componentType));
        if (elements == null || elements.Length == 0 || elements.Any(string.IsNullOrEmpty))
        {
            throw Invalid(nameof(ComponentStateExemption), componentType.Name + " 的元素名列表为 null、为空或含空串");
        }

        if (exemptionsByComponent.ContainsKey(componentType))
        {
            throw Invalid(nameof(ComponentStateExemption), componentType.Name + " 登记两次");
        }

        exemptionsByComponent[componentType] = (string[])elements.Clone();
        names.Add(nameof(ComponentStateExemption) + ":" + componentType.Name);
    }

    /// <summary>登记已回收对象的清扫，引擎在回收节拍上调用。</summary>
    /// <remarks>按 RimRunnerStaleReferenceSweep.IsGone 摘掉失效键；引擎逐个调用登记的清扫，不按 mod 筛选；清扫只处理本产品名单里的 mod；该 mod 启用而无法解析类型或字段时须抛。</remarks>
    public void StaleReferenceSweeper(Action sweep)
    {
        Open(nameof(StaleReferenceSweeper));
        RequireValue(sweep, nameof(StaleReferenceSweeper), nameof(sweep));
        sweepers.Add(sweep);
        names.Add(nameof(StaleReferenceSweeper));
    }

    /// <summary>登记一项能力激活读数：引擎在最外层能力激活前后各读一次，差值按名记进 Activated 观测。</summary>
    /// <remarks>差值为激活后读数减激活前读数；嵌套激活并入最外层，差值覆盖这次激活的全部入账。不登记时 Activated 不带差值，施放结果的差值表为空。无法给出读数时返回 NaN，读数抛出时该次差值为 NaN 并出错误级日志；同名登记两次即抛。</remarks>
    /// <param name="name">读数名，即 ActivationDeltas 的键。</param>
    /// <param name="reading">给正在激活的能力返回当前读数。</param>
    public void AbilityActivationMeter(string name, Func<Ability, float> reading)
    {
        Open(nameof(AbilityActivationMeter));
        RequireText(name, nameof(AbilityActivationMeter), nameof(name));
        RequireValue(reading, nameof(AbilityActivationMeter), nameof(reading));
        if (activationMeters.Any(meter => string.Equals(meter.Key, name, StringComparison.Ordinal)))
        {
            throw Invalid(nameof(AbilityActivationMeter), "读数 " + name + " 登记两次");
        }

        activationMeters.Add(new KeyValuePair<string, Func<Ability, float>>(name, reading));
        names.Add(nameof(AbilityActivationMeter) + ":" + name);
    }

    /// <summary>登记格的画出位置换算：截图取景、场面悬停与机位跟随按它对准。</summary>
    /// <remarks>返回与该格画出处在屏幕上重合的地面点（y 取 0），照逻辑位置画的格返回 null。可多次登记，取第一个非 null；全为 null 即按逻辑位置。物体随所在格平移；RimRunnerCameraFixture.LockOn 不经本换算。</remarks>
    /// <param name="locate">给地图与格返回画出位置，或 null。</param>
    public void DrawnCellCenter(Func<Map, IntVec3, Vector3?> locate)
    {
        Open(nameof(DrawnCellCenter));
        RequireValue(locate, nameof(DrawnCellCenter), nameof(locate));
        drawnCellLocators.Add(locate);
        names.Add(nameof(DrawnCellCenter));
    }

    /// <summary>登记一个设计上允许为空的静态只读反射句柄，HANDLE1 跳过核对该句柄是否为空。</summary>
    /// <remarks>声明成可空引用（?）的句柄、静态构造里按字符串名探测的类型在本进程无法解析的类里的空句柄本来就跳过核对，不必登记。登记项不匹配任何静态只读句柄时 HANDLE1 判红；类型本轮因缺席包被跳过时无法判断登记是否陈旧。</remarks>
    /// <param name="type">声明句柄的类型全名，嵌套类型用 + 连接，与 Type.FullName 相同。</param>
    /// <param name="member">字段名；只读自动属性写属性名。</param>
    /// <param name="reason">为什么允许为空。</param>
    public void OptionalHandle(string type, string member, string reason)
    {
        Open(nameof(OptionalHandle));
        RequireText(type, nameof(OptionalHandle), nameof(type));
        RequireText(member, nameof(OptionalHandle), nameof(member));
        RequireText(reason, nameof(OptionalHandle), nameof(reason));
        string key = type + "." + member;
        if (!optionalHandles.TryAdd(key, reason))
        {
            throw Invalid(nameof(OptionalHandle), key + " 登记两次");
        }

        names.Add(nameof(OptionalHandle) + ":" + key);
    }

    /// <summary>登记一个设计上不经 PatchAll 装上的补丁类，HANDLE1 对该类跳过挂载核对。</summary>
    /// <remarks>登记项不匹配任何带 [HarmonyPatch] 的类时 HANDLE1 判红；类型本轮因缺席包被跳过时无法判断登记是否陈旧。</remarks>
    /// <param name="type">补丁类的类型全名，嵌套类型用 + 连接，与 Type.FullName 相同。</param>
    /// <param name="reason">为什么不装。</param>
    public void InactivePatchClass(string type, string reason)
    {
        Open(nameof(InactivePatchClass));
        RequireText(type, nameof(InactivePatchClass), nameof(type));
        RequireText(reason, nameof(InactivePatchClass), nameof(reason));
        if (!inactivePatchClasses.TryAdd(type, reason))
        {
            throw Invalid(nameof(InactivePatchClass), type + " 登记两次");
        }

        names.Add(nameof(InactivePatchClass) + ":" + type);
    }

    internal void Freeze()
    {
        frozen = true;
    }

    internal IReadOnlyList<string> Names => names;

    internal IReadOnlyDictionary<string, string[]> CaseKeysByFile => caseKeysByFile;

    internal string? ProfilerEntryNodeName { get; private set; }

    internal IReadOnlyList<Type> HealingCompTypes => healingCompTypes;

    internal IReadOnlyList<Type> ScenarioTypes => scenarioTypes;

    internal IReadOnlyDictionary<Type, string[]> ComponentStateExemptions => exemptionsByComponent;

    internal IReadOnlyList<KeyValuePair<string, Func<Ability, float>>> ActivationMeters => activationMeters;

    internal IReadOnlyDictionary<string, string> OptionalHandles => optionalHandles;

    internal IReadOnlyDictionary<string, string> InactivePatchClasses => inactivePatchClasses;

    internal Vector3? LocateDrawnCell(Map map, IntVec3 cell)
    {
        foreach (Func<Map, IntVec3, Vector3?> locate in drawnCellLocators)
        {
            Vector3? drawn;
            try
            {
                drawn = locate(map, cell);
            }
            catch (Exception exception)
            {
                throw DelegateFailed(nameof(DrawnCellCenter), locate, exception);
            }

            if (drawn != null)
            {
                return drawn;
            }
        }

        return null;
    }

    internal string? DescribePawn(Pawn pawn)
    {
        if (pawnDiagnostics == null)
        {
            return null;
        }

        try
        {
            return pawnDiagnostics(pawn);
        }
        catch (Exception exception)
        {
            throw DelegateFailed(nameof(PawnDiagnostics), pawnDiagnostics, exception);
        }
    }

    internal void RunGeneratedHooks(Pawn pawn)
    {
        foreach (Action<Pawn> hook in generatedHooks)
        {
            try
            {
                hook(pawn);
            }
            catch (Exception exception)
            {
                throw DelegateFailed(nameof(OnPawnGenerated), hook, exception);
            }
        }
    }

    internal void RunSpawnedHooks(Pawn pawn)
    {
        foreach (Action<Pawn> hook in spawnedHooks)
        {
            try
            {
                hook(pawn);
            }
            catch (Exception exception)
            {
                throw DelegateFailed(nameof(OnPawnSpawned), hook, exception);
            }
        }
    }

    internal List<RimRunnerFindingEntry> LiveFingerprintFindings(RimRunnerContext ctx)
    {
        List<RimRunnerFindingEntry> findings = [];
        foreach (Func<RimRunnerContext, IEnumerable<RimRunnerLiveFingerprint>> source in fingerprintSources)
        {
            try
            {
                findings.AddRange((source(ctx) ?? []).Select(entry => RimRunnerFindingEntry.Of(entry.Key, entry.Subject, entry.Text)));
            }
            catch (Exception exception)
            {
                throw DelegateFailed(nameof(LiveFingerprints), source, exception);
            }
        }

        return findings;
    }

    internal void AppendWorldPrint(IDictionary<string, string> print)
    {
        foreach (Action<IDictionary<string, string>> append in worldPrinters)
        {
            try
            {
                append(print);
            }
            catch (Exception exception)
            {
                throw DelegateFailed(nameof(WorldPrint), append, exception);
            }
        }
    }

    internal void SweepStaleReferences()
    {
        foreach (Action sweep in sweepers)
        {
            try
            {
                sweep();
            }
            catch (Exception exception)
            {
                throw DelegateFailed(nameof(StaleReferenceSweeper), sweep, exception);
            }
        }
    }

    internal static InvalidOperationException DelegateFailed(string registration, Delegate target, Exception inner)
    {
        return new InvalidOperationException(RimRunnerLog.Prefix + " 产品能力登记 " + registration + " 的委托 "
                                             + DescribeTarget(target) + " 调用时抛出 " + inner.GetType().FullName
                                             + "：" + inner.Message, inner);
    }

    internal static string DescribeTarget(Delegate target)
    {
        return target.Method.DeclaringType?.FullName + "." + target.Method.Name;
    }

    private void Open(string registration)
    {
        if (frozen)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " 产品能力 " + registration
                                                + " 在 Configure 返回之后登记：登记已关闭，只能在 Configure 里登记");
        }
    }

    private static void RequireText(string value, string registration, string parameter)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw Invalid(registration, parameter + " 为 null 或空串");
        }
    }

    private static void RequireValue(object value, string registration, string parameter)
    {
        if (value == null)
        {
            throw Invalid(registration, parameter + " 为 null");
        }
    }

    private static ArgumentException Invalid(string registration, string detail)
    {
        return new ArgumentException(RimRunnerLog.Prefix + " 产品能力 " + registration + " 登记无效：" + detail);
    }
}
