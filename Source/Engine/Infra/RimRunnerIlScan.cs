using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace RimRunner;

/// <summary>扫描用例共用的 IL 扫描层：读方法体、取调用边、建闭包。</summary>
/// <remarks>RimRunnerCallSiteGuard 建在本层。无法读取的方法计进 Stats；产品建的 Stats 没过读取闸的，用例末点名。</remarks>
public static class RimRunnerIlScan
{
    private static ILGenerator SharedGenerator =>
        field ??= new DynamicMethod(
            "RimRunnerIlScan", typeof(void), Type.EmptyTypes).GetILGenerator();

    private static readonly OpCode[] CallOpcodes =
    [
        OpCodes.Call, OpCodes.Callvirt, OpCodes.Newobj,
        OpCodes.Ldftn, OpCodes.Ldvirtftn, OpCodes.Jmp,
    ];

    /// <summary>一趟扫描的规模与失败计数。</summary>
    public sealed class Stats
    {
        /// <summary>进入扫描的方法总数。</summary>
        public int MethodsSeen;
        /// <summary>成功读出至少一条指令的方法数。</summary>
        public int MethodsWithBody;
        /// <summary>无方法体的方法数。</summary>
        public int MethodsNoBody;
        /// <summary>因 P/Invoke 跳过的方法数。</summary>
        public int MethodsSkippedPInvoke;
        /// <summary>读取抛异常的方法数，须为 0。</summary>
        public int MethodsFailed;
        /// <summary>读取失败的明细。</summary>
        public readonly List<string> Failures = [];
        internal int CacheHits;
        /// <summary>因引用本轮缺席包的程序集而无法读取的方法数，不计读取失败。</summary>
        public int MethodsAbsentPackage;
        /// <summary>缺席包不可读的明细（方法 —— 程序集（packageId））。</summary>
        public readonly List<string> AbsentPackageMethods = [];
        internal readonly HashSet<MethodBase> AbsentAttributeMethods = [];
        internal readonly List<string> AbsentPackageIds = [];
        internal bool ReadGated;

        internal readonly Dictionary<Assembly, int> WithBodyByAssembly = [];
        internal readonly HashSet<Assembly> AbsentPackageAssemblies = [];

        private bool authorOwned;
        internal bool AuthorOwned
        {
            get => authorOwned;
            set
            {
                authorOwned = value;
                if (value)
                {
                    RegisterRead(this);
                }
            }
        }

        /// <summary>构造一份空统计。</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0290")]
        public Stats()
        {
            AuthorOwned = !RimRunnerDefScope.IsEngineSide(RimRunnerCallSite.MethodOf(new System.Diagnostics.StackFrame(1, false))?.DeclaringType?.Assembly);
        }
        /// <summary>程序集名 → 本轮缺席的 packageId，不缺席返 null。</summary>
        public Func<string, string?> AbsentPackageOf = RimRunnerPackages.AbsentPackageOfAssembly;

        /// <summary>一行摘要文本。</summary>
        public string Summary =>
            $"扫描 {MethodsSeen} 个方法：核对有体的 {MethodsWithBody} 个，无体 {MethodsNoBody} 个；"
            + $"跳过核对：PInvoke {MethodsSkippedPInvoke} 个 · 缺席包不可读 {MethodsAbsentPackage} 个"
            + (SignatureSkipCount == 0 ? "" : $" · 签名引用缺席包而未枚举 {SignatureSkipCount} 个（进程累计）")
            + $"；读取失败 {MethodsFailed} 个"
            + AbsentPackageLines;

        internal string AbsentPackageLines =>
            MethodsAbsentPackage == 0
                ? string.Empty
                : "\n" + string.Join("\n", AbsentPackageIds
                    .GroupBy(packageId => packageId, StringComparer.Ordinal)
                    .Select(group => "名单缺席 " + group.Key + "：" + group.Count()
                                     + " 个引用它的方法本轮未被契约闸与调用点守卫读取"));
    }

    private readonly struct IlInstruction(OpCode opcode, object operand, Label[] labels)
    {
        public readonly OpCode Opcode = opcode;
        public readonly object Operand = operand;
        public readonly Label[] Labels = labels;

        public bool IsBranchTarget => Labels.Length > 0;
    }

    private static readonly Label[] NoLabels = [];

    private sealed class IlReadResult
    {
        public required IlInstruction[] Instructions;
        public bool CountedPInvoke;
        public bool CountedNoBody;
        public string? FailureMessage;
        public string? MissingAssembly;
    }

    private static readonly Dictionary<MethodBase, IlReadResult> ReadCache = [];

    private static readonly IlInstruction[] EmptyInstructions = [];

    private static readonly Dictionary<Assembly, Dictionary<(Module, int), List<MethodInfo>>> OverrideIndex = [];
    private static readonly Dictionary<(Type, Module, int), MethodInfo?> DispatchCache = [];

    /// <summary>清缓存：清掉读过的指令流与派发表。</summary>
    public static void ClearCache()
    {
        ReadCache.Clear();
        OverrideIndex.Clear();
        DispatchCache.Clear();
    }

    /// <summary>产品代码扫描面：描述符 ProbeTypes 各类型所在程序集。</summary>
    public static IEnumerable<Assembly> OurAssemblies() =>
        RimRunnerDefScope.ProbeAssemblies;

    /// <summary>程序集内全部方法与构造函数，含编译器生成类型。</summary>
    /// <remarks>签名引用本轮缺席包类型的方法不交出、计进 SignatureSkipCount；签名无法解析且无法识别缺的是否为本轮缺席包时抛。</remarks>
    public static IEnumerable<MethodBase> AllMethods(Assembly assembly) =>
        AllMethods(assembly, RimRunnerPackages.AbsentPackageOfAssembly, RecordSignatureSkip);

    /// <summary>同 AllMethods，缺席判据与跳过记账由调用方给。</summary>
    public static IEnumerable<MethodBase> AllMethods(
        Assembly assembly, Func<string, string?> absentPackageOf, Action<string> onAbsent)
    {
        foreach (Type type in assembly.GetTypes())
            foreach (MethodBase m in TypeMethods(type, absentPackageOf, (entry, _) => onAbsent(entry)))
                yield return m;
    }

    private static readonly HashSet<string> SignatureSkips = [];

    private static void RecordSignatureSkip(string entry) => SignatureSkips.Add(entry);

    /// <summary>枚举时因签名引用缺席包而跳过的方法数（进程累计）。</summary>
    public static int SignatureSkipCount => SignatureSkips.Count;

    /// <summary>产品代码扫描面（OurAssemblies）内全部方法。</summary>
    public static IEnumerable<MethodBase> AllOurMethods() =>
        OurAssemblies().SelectMany(AllMethods);

    /// <summary>读一个方法的指令流，失败计入 stats 并返回空表。</summary>
    /// <remarks>读的是原始 IL，不含 Harmony 补丁；返回的表与其中元素每次新建，可就地改。无方法体（abstract / interface）返回空表，计入无方法体数，不算读取失败。</remarks>
    public static List<CodeInstruction> Instructions(MethodBase method, Stats stats)
    {
        IlReadResult result = ReadCached(method, out bool cacheHit);
        ReplayStats(result, method, stats, cacheHit);

        List<CodeInstruction> materialized =
            new(result.Instructions.Length);
        foreach (IlInstruction instruction in result.Instructions)
        {
            CodeInstruction copy = new(instruction.Opcode, instruction.Operand);
            copy.labels.AddRange(instruction.Labels);
            materialized.Add(copy);
        }
        return materialized;
    }

    private static IlReadResult ReadCached(MethodBase method, out bool hit)
    {
        if (ReadCache.TryGetValue(method, out IlReadResult? cached))
        {
            hit = true;
            return cached;
        }

        hit = false;
        IlReadResult result = new() { Instructions = EmptyInstructions };

        RimRunnerAbsentReferences.Hit? absent = RimRunnerAbsentReferences.Unreadable(method);
        if (absent != null)
        {
            result.FailureMessage = "引用的程序集 " + absent.Assembly + " 不在本进程（" + absent.Describe() + "）";
            result.MissingAssembly = absent.Assembly;
            ReadCache[method] = result;
            return result;
        }

        try
        {
            if ((method.Attributes & MethodAttributes.PinvokeImpl) != 0)
            {
                result.CountedPInvoke = true;
            }
            else if (method.GetMethodBody() == null)
            {
                result.CountedNoBody = true;
            }
            else
            {
                List<CodeInstruction> read =
                    PatchProcessor.GetOriginalInstructions(method, SharedGenerator);
                IlInstruction[] snapshot = new IlInstruction[read.Count];
                for (int i = 0; i < read.Count; i++)
                    snapshot[i] = new IlInstruction(read[i].opcode, read[i].operand,
                        read[i].labels.Count == 0 ? NoLabels : read[i].labels.ToArray());

                Exception? poisoned = null;
                foreach (IlInstruction instruction in snapshot)
                {
                    if (instruction.Operand is MemberInfo member
                        && (poisoned = SignatureFailure(member)) != null)
                        break;
                }

                if (poisoned != null) throw poisoned;
                result.Instructions = snapshot;
            }
        }
        catch (Exception e)
        {
            result.CountedPInvoke = false;
            result.CountedNoBody = false;
            result.FailureMessage = $"{e.GetType().Name}: {e.Message}";
        }

        ReadCache[method] = result;
        return result;
    }

    internal static bool ReadSucceeded(MethodBase method) =>
        ReadCache.TryGetValue(method, out IlReadResult? result) && result.FailureMessage == null;

    private static readonly Dictionary<MemberInfo, Exception?> SignatureCache = [];

    private static Exception? SignatureFailure(MemberInfo member)
    {
        if (SignatureCache.TryGetValue(member, out Exception? cached)) return cached;

        Exception? failure = null;
        try
        {
            _ = member.DeclaringType;
            switch (member)
            {
                case MethodInfo m:
                    _ = m.ReturnType;
                    m.GetParameters();
                    break;
                case ConstructorInfo c:
                    c.GetParameters();
                    break;
                case FieldInfo f:
                    _ = f.FieldType;
                    break;
            }
        }
        catch (Exception e)
        {
            failure = e;
        }

        SignatureCache[member] = failure;
        return failure;
    }

    private static IEnumerable<MethodBase> Resolvable(
        Type type, IEnumerable<MethodBase> methods, Func<string, string?> absentPackageOf, Action<string, string> onAbsent)
    {
        foreach (MethodBase m in methods)
        {
            RimRunnerAbsentReferences.Hit? absent = RimRunnerAbsentReferences.SignatureOf(m);
            string where = type.FullName + "::" + SafeName(m);
            if (absent == null)
            {
                Exception? failure = SignatureFailure(m);
                if (failure != null)
                {
                    throw new InvalidOperationException("无法解析 " + where + " 的签名", failure);
                }

                yield return m;
                continue;
            }

            string package = absentPackageOf(absent.Assembly) ?? throw new InvalidOperationException(where + " 的签名引用的程序集 "
                + absent.Assembly + RimRunnerAbsentReferences.NotAttributed(absent.Assembly) + "（" + absent.Describe() + "）" + OwnerNote(absent.Assembly));

            onAbsent(where + " —— " + absent.Assembly + "（" + package + "）", package);
        }
    }

    private static string OwnerNote(string assembly) =>
        RimRunnerModAssemblies.OwnerText(assembly) is { } owner ? "。" + owner : "";

    private static string SafeName(MethodBase method)
    {
        try
        {
            return method.Name;
        }
        catch (Exception)
        {
            return "<token 0x" + method.MetadataToken.ToString("x8") + ">";
        }
    }

    private static string Describe(MethodBase method)
    {
        string? type;
        try
        {
            type = method.DeclaringType?.FullName;
        }
        catch (Exception)
        {
            type = "<签名不可解析>";
        }

        return type + "::" + SafeName(method);
    }

    private static readonly HashSet<Stats> readThisCase = [];

    internal static void BeginCase() => readThisCase.Clear();

    private static void RegisterRead(Stats stats)
    {
        if (RimRunnerContext.AmbientPhase == RimRunnerPhase.Case)
        {
            readThisCase.Add(stats);
        }
    }

    internal static bool IsUngatedThisCase(Stats stats) =>
        readThisCase.Contains(stats) && stats.MethodsFailed > 0 && !stats.ReadGated;

    internal static List<Stats> TakeRegisteredThisCase()
    {
        List<Stats> registered = [.. readThisCase];
        readThisCase.Clear();
        return registered;
    }

    private static void ReplayStats(IlReadResult result, MethodBase method, Stats stats, bool cacheHit)
    {
        if (stats.AuthorOwned) RegisterRead(stats);
        if (cacheHit) stats.CacheHits++;
        stats.MethodsSeen++;

        if (result.CountedPInvoke)
        {
            stats.MethodsSkippedPInvoke++;
            return;
        }

        if (result.CountedNoBody)
        {
            stats.MethodsNoBody++;
            return;
        }

        if (result.FailureMessage != null)
        {
            string where = Describe(method);
            string? package = result.MissingAssembly == null ? null : stats.AbsentPackageOf(result.MissingAssembly);
            if (package != null)
            {
                stats.MethodsAbsentPackage++;
                stats.AbsentPackageMethods.Add($"{where} —— {result.MissingAssembly}（{package}）");
                stats.AbsentPackageIds.Add(package);
                stats.AbsentPackageAssemblies.Add(method.Module.Assembly);
                return;
            }

            stats.MethodsFailed++;
            stats.Failures.Add(result.MissingAssembly == null
                ? $"{where} —— {result.FailureMessage}"
                : $"{where} —— 引用的程序集 {result.MissingAssembly}{RimRunnerAbsentReferences.NotAttributed(result.MissingAssembly)}{OwnerNote(result.MissingAssembly)} —— {result.FailureMessage}");
            return;
        }

        if (result.Instructions.Length == 0) return;
        stats.MethodsWithBody++;
        Assembly owner = method.Module.Assembly;
        stats.WithBodyByAssembly[owner] = stats.WithBodyByAssembly.TryGetValue(owner, out int count) ? count + 1 : 1;
    }

    /// <summary>一个方法直接引出的调用边（含 ldftn）。</summary>
    /// <remarks>取 call、callvirt、newobj、ldftn、ldvirtftn、jmp 的目标，方法级泛型实例归一到定义；callvirt 交出 operand 所指方法，不展开到 override。</remarks>
    public static IEnumerable<MethodBase> CallEdges(MethodBase method, Stats stats)
    {
        IlReadResult result = ReadCached(method, out bool cacheHit);
        ReplayStats(result, method, stats, cacheHit);

        foreach (IlInstruction instruction in result.Instructions)
        {
            if (!CallOpcodes.Contains(instruction.Opcode)) continue;
            if (instruction.Operand is MethodBase target) yield return Normalize(target);
        }
    }

    /// <summary>归一：泛型方法实例换成其泛型定义，其余原样返回。</summary>
    /// <remarks>只归一方法级泛型；宿主类型是构造泛型（如 List&lt;int&gt;.Add）时原样返回。</remarks>
    public static MethodBase Normalize(MethodBase method)
    {
        if (method is MethodInfo mi && mi.IsGenericMethod && !mi.IsGenericMethodDefinition)
            return mi.GetGenericMethodDefinition();
        return method;
    }

    /// <summary>从根集合出发、限定 scope 内的传递调用闭包。</summary>
    /// <remarks>迭代器与 async 状态机体一并展开；followDispatch 只在判得出接收者静态类型时补派发：该类型上生效的 override，该类型在 scope 内时另加其 scope 内子类的 override，接口方法不补。</remarks>
    /// <param name="roots">闭包起点，不受 scope 限制。</param>
    /// <param name="stats">读取计数累加进这份统计。</param>
    /// <param name="scope">只跟落在这些程序集内的边；null 取 OurAssemblies。</param>
    /// <param name="enterType">为假的类型不进方法体；null 即都进。</param>
    /// <param name="followDispatch">是否补 virtual 派发，缺省不补。</param>
    public static HashSet<MethodBase> Closure(
        IEnumerable<MethodBase> roots, Stats stats, IEnumerable<Assembly>? scope = null,
        Func<Type, bool>? enterType = null, bool followDispatch = false)
    {
        HashSet<Assembly> ours = [.. scope ?? OurAssemblies()];
        HashSet<MethodBase> seen = [];
        Stack<MethodBase> pending = new();

        foreach (MethodBase root in roots)
            if (seen.Add(root)) pending.Push(root);

        void Enter(MethodBase edge)
        {
            Type? declaring = edge.DeclaringType;
            if (declaring == null || !ours.Contains(declaring.Assembly)) return;
            if (enterType != null && !enterType(declaring)) return;
            if (seen.Add(edge)) pending.Push(edge);

            if (edge is ConstructorInfo && IsCompilerGenerated(declaring, stats))
                foreach (MethodBase m in TypeMethods(declaring, stats.AbsentPackageOf, (entry, package) => CountAbsent(stats, declaring, entry, package)))
                    if (seen.Add(m)) pending.Push(m);
        }

        while (pending.Count > 0)
        {
            MethodBase current = pending.Pop();

            foreach (MethodBase machineMethod in StateMachineMethods(current, stats))
                if (seen.Add(machineMethod)) pending.Push(machineMethod);

            IlReadResult result = ReadCached(current, out bool cacheHit);
            ReplayStats(result, current, stats, cacheHit);
            IlInstruction[] body = result.Instructions;
            for (int i = 0; i < body.Length; i++)
            {
                if (!CallOpcodes.Contains(body[i].Opcode)) continue;
                if (body[i].Operand is not MethodBase target) continue;

                Enter(Normalize(target));
                if (followDispatch)
                    foreach (MethodInfo implementation in DispatchTargets(current, body, i, ours))
                        Enter(implementation);
            }
        }

        return seen;
    }

    private static IEnumerable<MethodInfo> DispatchTargets(
        MethodBase host, IlInstruction[] body, int index, HashSet<Assembly> ours)
    {
        IlInstruction call = body[index];
        if (call.Opcode != OpCodes.Callvirt && call.Opcode != OpCodes.Ldvirtftn) yield break;
        if (call.Operand is not MethodInfo target || !target.IsVirtual || target.IsFinal) yield break;
        Type? declaring = target.DeclaringType;
        if (declaring == null || declaring.IsInterface) yield break;

        int depth = call.Opcode == OpCodes.Ldvirtftn ? 1 : target.GetParameters().Length + 1;
        Type? receiver = ResolveStackOperand(host, body, index, depth);
        if (receiver == null || receiver.IsGenericParameter || receiver.IsInterface
            || !declaring.IsAssignableFrom(receiver)) yield break;

        (Module, int) root = DispatchKey(target.GetBaseDefinition());
        MethodInfo? resolved = ResolveDispatch(receiver, declaring, root);
        if (resolved != null) yield return resolved;

        if (!ours.Contains(receiver.Assembly)) yield break;
        foreach (Assembly assembly in ours)
        {
            if (!OverridesIn(assembly).TryGetValue(root, out List<MethodInfo>? overrides)) continue;
            foreach (MethodInfo m in overrides)
                if (m.DeclaringType != receiver && receiver.IsAssignableFrom(m.DeclaringType))
                    yield return m;
        }
    }

    private static (Module, int) DispatchKey(MethodInfo baseDefinition) =>
        (baseDefinition.Module, baseDefinition.MetadataToken);

    private static MethodInfo? ResolveDispatch(Type receiver, Type declaring, (Module, int) root)
    {
        if (DispatchCache.TryGetValue((receiver, root.Item1, root.Item2), out MethodInfo? cached))
            return cached;

        MethodInfo? found = null;
        for (Type? t = receiver; t != null && t != declaring && found == null; t = t.BaseType)
            foreach (MethodInfo m in DeclaredInstanceMethods(t))
                if (m.IsVirtual && DispatchKey(m.GetBaseDefinition()) == root)
                {
                    found = m;
                    break;
                }

        DispatchCache[(receiver, root.Item1, root.Item2)] = found;
        return found;
    }

    private static Dictionary<(Module, int), List<MethodInfo>> OverridesIn(Assembly assembly)
    {
        if (OverrideIndex.TryGetValue(assembly, out Dictionary<(Module, int), List<MethodInfo>>? index))
            return index;

        index = [];
        foreach (Type type in assembly.GetTypes())
        {
            if (type.IsInterface) continue;
            foreach (MethodInfo m in DeclaredInstanceMethods(type))
            {
                if (!m.IsVirtual || m.IsAbstract) continue;
                MethodInfo baseDefinition = m.GetBaseDefinition();
                if (baseDefinition.DeclaringType == m.DeclaringType) continue;
                (Module, int) key = DispatchKey(baseDefinition);
                if (!index.TryGetValue(key, out List<MethodInfo>? list))
                    index[key] = list = [];
                list.Add(m);
            }
        }

        OverrideIndex[assembly] = index;
        return index;
    }

    /// <summary>源方法名下的编译器生成方法体（lambda / 局部函数 / 状态机）。</summary>
    /// <remarks>只沿编译器生成的边收，不跟进被调用的普通方法。</remarks>
    public static HashSet<MethodBase> OwnedBodies(MethodBase source, Stats stats)
    {
        HashSet<MethodBase> owned = [];
        Stack<MethodBase> pending = new();
        Assembly? assembly = source.DeclaringType?.Assembly;

        void Add(MethodBase method)
        {
            if (method != source && owned.Add(method)) pending.Push(method);
        }

        foreach (MethodBase machineMethod in StateMachineMethods(source, stats)) Add(machineMethod);
        pending.Push(source);

        while (pending.Count > 0)
        {
            MethodBase current = pending.Pop();
            foreach (MethodBase machineMethod in StateMachineMethods(current, stats)) Add(machineMethod);
            foreach (MethodBase edge in CallEdges(current, stats))
            {
                Type? declaring = edge.DeclaringType;
                if (declaring == null || declaring.Assembly != assembly) continue;
                if (IsCompilerGenerated(declaring, stats))
                {
                    if (edge is ConstructorInfo)
                        foreach (MethodBase m in TypeMethods(declaring, stats.AbsentPackageOf, (entry, package) => CountAbsent(stats, declaring, entry, package))) Add(m);
                    else Add(edge);
                }
                else if (!MethodAttributeAbsent(edge, stats)
                         && edge.GetCustomAttribute<CompilerGeneratedAttribute>() != null
                         && edge.Name.StartsWith("<"))
                {
                    Add(edge);
                }
            }
        }

        return owned;
    }

    private static IEnumerable<MethodBase> StateMachineMethods(MethodBase method, Stats stats)
    {
        if (MethodAttributeAbsent(method, stats)) return [];
        StateMachineAttribute? attribute = method.GetCustomAttribute<StateMachineAttribute>();
        if (attribute?.StateMachineType == null) return [];
        return TypeMethods(attribute.StateMachineType, stats.AbsentPackageOf, (entry, package) => CountAbsent(stats, attribute.StateMachineType, entry, package));
    }

    private static bool MethodAttributeAbsent(MethodBase method, Stats stats)
    {
        RimRunnerAbsentReferences.Hit? absent = RimRunnerAbsentReferences.AttributeOf(method);
        if (absent == null) return false;
        string? package = stats.AbsentPackageOf(absent.Assembly);
        if (package == null)
        {
            throw new InvalidOperationException(Describe(method) + " 的特性引用的程序集 " + absent.Assembly
                + RimRunnerAbsentReferences.NotAttributed(absent.Assembly) + "（" + absent.Describe() + "）" + OwnerNote(absent.Assembly));
        }

        if (stats.AbsentAttributeMethods.Add(method))
        {
            CountAbsent(stats, method.DeclaringType!,
                Describe(method) + " —— " + absent.Assembly + "（" + package + "）", package);
        }
        return true;
    }

    private static void CountAbsent(Stats stats, Type declaring, string entry, string package)
    {
        stats.AbsentPackageAssemblies.Add(declaring.Assembly);
        stats.MethodsSeen++;
        stats.MethodsAbsentPackage++;
        stats.AbsentPackageMethods.Add(entry);
        stats.AbsentPackageIds.Add(package);
    }

    private static IEnumerable<MethodBase> TypeMethods(
        Type type, Func<string, string?> absentPackageOf, Action<string, string> onAbsent)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                                                       | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        return Resolvable(type, type.GetMethods(flags).Cast<MethodBase>().Concat(type.GetConstructors(flags)),
            absentPackageOf, onAbsent);
    }

    private static IEnumerable<MethodInfo> DeclaredInstanceMethods(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                                                       | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        return Resolvable(type, type.GetMethods(flags), RimRunnerPackages.AbsentPackageOfAssembly,
                (entry, _) => RecordSignatureSkip(entry))
            .Cast<MethodInfo>();
    }

    private static bool IsCompilerGenerated(Type type, Stats stats)
    {
        RimRunnerAbsentReferences.Hit? absent = RimRunnerAbsentReferences.AttributeOf(type);
        if (absent == null) return type.GetCustomAttribute<CompilerGeneratedAttribute>() != null;
        if (stats.AbsentPackageOf(absent.Assembly) == null)
        {
            throw new InvalidOperationException(type.FullName + " 的特性引用的程序集 " + absent.Assembly
                + RimRunnerAbsentReferences.NotAttributed(absent.Assembly) + "（" + absent.Describe() + "）" + OwnerNote(absent.Assembly));
        }
        return false;
    }

    /// <summary>回溯某调用点第 parameterIndex 个形参收到的实参的静态类型。</summary>
    /// <remarks>按 target 的声明形参从 0 计，实例方法的接收者不占位；无法判断时返回 null；body 须带分支 label，Instructions 取出的指令流已带。</remarks>
    /// <exception cref="ArgumentOutOfRangeException">parameterIndex 越出 target 的形参表时抛。</exception>
    public static Type? ResolveArgumentType(
        MethodBase host, List<CodeInstruction> body, int callIndex, MethodBase target, int parameterIndex)
    {
        int parameterCount = target.GetParameters().Length;
        if (parameterIndex < 0 || parameterIndex >= parameterCount)
        {
            throw new ArgumentOutOfRangeException(nameof(parameterIndex), parameterIndex,
                RimRunnerLog.Prefix + " " + target.Name + " 只有 " + parameterCount + " 个形参");
        }

        return ResolveStackOperand(host, Snapshot(body), callIndex, parameterCount - parameterIndex);
    }

    /// <summary>回溯某实例方法调用点的接收者静态类型。</summary>
    /// <remarks>无法判断时返回 null；body 须带分支 label，Instructions 取出的指令流已带。</remarks>
    /// <exception cref="ArgumentException">target 为静态方法或该调用点是 newobj 时抛。</exception>
    public static Type? ResolveReceiverType(
        MethodBase host, List<CodeInstruction> body, int callIndex, MethodBase target)
    {
        if (target.IsStatic || body[callIndex].opcode == OpCodes.Newobj)
        {
            throw new ArgumentException(
                RimRunnerLog.Prefix + " " + target.Name + " 在该调用点没有接收者（" + (target.IsStatic ? "静态方法" : "newobj") + "）", nameof(target));
        }

        return ResolveStackOperand(host, Snapshot(body), callIndex, target.GetParameters().Length + 1);
    }

    internal static bool IsConstantTrueArgument(
        MethodBase host, List<CodeInstruction> body, int callIndex, MethodBase target, int parameterIndex)
    {
        int parameterCount = target.GetParameters().Length;
        if (parameterIndex < 0 || parameterIndex >= parameterCount)
        {
            throw new ArgumentOutOfRangeException(nameof(parameterIndex), parameterIndex,
                RimRunnerLog.Prefix + " " + target.Name + " 只有 " + parameterCount + " 个形参");
        }

        IlInstruction? producer =
            ResolveStackInstruction(Snapshot(body), callIndex, parameterCount - parameterIndex);
        return producer is { } instruction
               && (instruction.Opcode == OpCodes.Ldc_I4_1
                   || instruction.Opcode == OpCodes.Ldc_I4 && instruction.Operand is int value && value == 1);
    }

    private static IlInstruction[] Snapshot(List<CodeInstruction> body)
    {
        IlInstruction[] snapshot = new IlInstruction[body.Count];
        for (int i = 0; i < body.Count; i++)
        {
            List<Label> labels = body[i].labels;
            snapshot[i] = new IlInstruction(body[i].opcode, body[i].operand,
                labels == null || labels.Count == 0 ? NoLabels : labels.ToArray());
        }

        return snapshot;
    }

    private static Type? ResolveStackOperand(
        MethodBase host, IlInstruction[] body, int callIndex, int depth)
    {
        IlInstruction? producer = ResolveStackInstruction(body, callIndex, depth);
        return producer is { } ci ? ResolveProducedType(host, ci.Opcode, ci.Operand) : null;
    }

    private static IlInstruction? ResolveStackInstruction(
        IlInstruction[] body, int callIndex, int depth)
    {
        if (body[callIndex].IsBranchTarget) return null;

        int need = depth;
        for (int i = callIndex - 1; i >= 0; i--)
        {
            IlInstruction ci = body[i];
            int push = PushCount(ci.Opcode, ci.Operand);
            int pop = PopCount(ci.Opcode, ci.Operand);
            if (push < 0 || pop < 0) return null;

            if (push >= need)
            {
                return push == 1 ? ci : null;
            }

            if (ci.IsBranchTarget) return null;

            need -= push;
            need += pop;
        }

        return null;
    }

    /// <summary>产值指令的结果类型。</summary>
    public static Type? ResolveProducedType(MethodBase host, CodeInstruction ci) =>
        ResolveProducedType(host, ci.opcode, ci.operand);

    private static Type? ResolveProducedType(MethodBase host, OpCode opcode, object operand)
    {
        if (opcode == OpCodes.Newobj) return (operand as ConstructorInfo)?.DeclaringType;
        if (opcode == OpCodes.Box || opcode == OpCodes.Castclass
                                  || opcode == OpCodes.Isinst) return operand as Type;
        if (opcode == OpCodes.Ldstr) return typeof(string);
        if (opcode == OpCodes.Ldnull) return null;

        if (operand is LocalVariableInfo local) return local.LocalType;
        if (operand is FieldInfo field) return field.FieldType;
        if (operand is MethodInfo method) return method.ReturnType;

        int shortIndex = ShortFormLocalIndex(opcode);
        if (shortIndex >= 0)
        {
            IList<LocalVariableInfo>? locals = host.GetMethodBody()?.LocalVariables;
            if (locals != null && shortIndex < locals.Count) return locals[shortIndex].LocalType;
        }

        int argIndex = ShortFormArgIndex(opcode);
        if (argIndex < 0 && (opcode == OpCodes.Ldarg || opcode == OpCodes.Ldarg_S))
        {
            if (operand is ParameterInfo parameter) return parameter.ParameterType;
            if (operand is int operandIndex) argIndex = operandIndex;
        }
        if (argIndex >= 0)
        {
            if (!host.IsStatic)
            {
                if (argIndex == 0) return host.DeclaringType;
                argIndex--;
            }
            ParameterInfo[] parameters = host.GetParameters();
            if (argIndex < parameters.Length) return parameters[argIndex].ParameterType;
        }

        return null;
    }

    private static int ShortFormArgIndex(OpCode opcode)
    {
        if (opcode == OpCodes.Ldarg_0) return 0;
        if (opcode == OpCodes.Ldarg_1) return 1;
        if (opcode == OpCodes.Ldarg_2) return 2;
        if (opcode == OpCodes.Ldarg_3) return 3;
        return -1;
    }

    private static int ShortFormLocalIndex(OpCode opcode)
    {
        if (opcode == OpCodes.Ldloc_0) return 0;
        if (opcode == OpCodes.Ldloc_1) return 1;
        if (opcode == OpCodes.Ldloc_2) return 2;
        if (opcode == OpCodes.Ldloc_3) return 3;
        return -1;
    }

    /// <summary>指令压栈数。</summary>
    /// <remarks>无法识别的 opcode 或操作数形态返回 -1；-1 表示未确定，不是 0。</remarks>
    public static int PushCount(CodeInstruction ci) => PushCount(ci.opcode, ci.operand);

    /// <summary>指令弹栈数。</summary>
    /// <remarks>无法识别的 opcode 或操作数形态返回 -1；-1 表示未确定，不是 0。</remarks>
    public static int PopCount(CodeInstruction ci) => PopCount(ci.opcode, ci.operand);

    private static int PushCount(OpCode opcode, object operand)
    {
        return opcode.StackBehaviourPush switch
        {
            StackBehaviour.Push0 => 0,
            StackBehaviour.Push1 or StackBehaviour.Pushi or StackBehaviour.Pushi8 or StackBehaviour.Pushr4 or StackBehaviour.Pushr8 or StackBehaviour.Pushref => 1,
            StackBehaviour.Push1_push1 => 2,
            StackBehaviour.Varpush => operand is MethodInfo mi && mi.ReturnType != typeof(void) ? 1 : 0,
            _ => -1,
        };
    }

    private static int PopCount(OpCode opcode, object operand)
    {
        switch (opcode.StackBehaviourPop)
        {
            case StackBehaviour.Pop0: return 0;
            case StackBehaviour.Pop1:
            case StackBehaviour.Popi:
            case StackBehaviour.Popref: return 1;
            case StackBehaviour.Pop1_pop1:
            case StackBehaviour.Popi_pop1:
            case StackBehaviour.Popi_popi:
            case StackBehaviour.Popi_popi8:
            case StackBehaviour.Popi_popr4:
            case StackBehaviour.Popi_popr8:
            case StackBehaviour.Popref_pop1:
            case StackBehaviour.Popref_popi: return 2;
            case StackBehaviour.Popi_popi_popi:
            case StackBehaviour.Popref_popi_popi:
            case StackBehaviour.Popref_popi_popi8:
            case StackBehaviour.Popref_popi_popr4:
            case StackBehaviour.Popref_popi_popr8:
            case StackBehaviour.Popref_popi_popref: return 3;
            case StackBehaviour.Varpop:
                if (opcode == OpCodes.Newobj)
                {
                    return (operand as ConstructorInfo)?.GetParameters().Length ?? -1;
                }

                if (operand is MethodBase mb)
                {
                    return mb.GetParameters().Length + (mb.IsStatic ? 0 : 1);
                }

                return -1;
            default: return -1;
        }
    }
}
