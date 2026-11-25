using System.Reflection.Emit;

namespace RimRunner;

internal static class RimRunnerPackageAbsenceGate
{
    private const BindingFlags AllDeclared = BindingFlags.Public | BindingFlags.NonPublic
                                             | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly MethodInfo? PresentMethod =
        typeof(RimRunnerPackages).GetMethod(nameof(RimRunnerPackages.Present));

    internal sealed class Hit
    {
        public required MethodBase Method;
        public string? Package;
        public string? Detail;
        public List<Type> Owners = [];
        public bool CaseReached;
        public bool ShowcaseReached;
    }

    internal sealed class LoadTimeHit(LoadTimeKind kind, Type type, string? member, string package)
    {
        internal readonly LoadTimeKind Kind = kind;
        internal readonly Type Type = type;
        internal readonly string? Member = member;
        internal readonly string Package = package;

        internal string Text => Kind switch
        {
            LoadTimeKind.Base => Type.FullName + " 基类（" + Package + "）",
            LoadTimeKind.Interface => Type.FullName + " 接口 " + Member + "（" + Package + "）",
            LoadTimeKind.ValueField => Type.FullName + " 值类型字段 " + Member + "（" + Package + "）",
        };
    }

    internal enum LoadTimeKind
    {
        Base,
        Interface,
        ValueField,
    }

    internal sealed class Result
    {
        public readonly List<LoadTimeHit> LoadTime = [];
        public readonly List<Hit> Unlabeled = [];
        public readonly List<Hit> SelfGuarded = [];
        public readonly List<Hit> ModsConfigActive = [];
    }

    private sealed class Taint
    {
        public readonly SortedSet<string> Packages = new(StringComparer.Ordinal);
        public required string Detail;
    }

    private static readonly Dictionary<string, string> DlcActivePackages = new(StringComparer.Ordinal)
    {
        ["get_RoyaltyActive"] = "ludeon.rimworld.royalty",
        ["get_IdeologyActive"] = "ludeon.rimworld.ideology",
        ["get_BiotechActive"] = "ludeon.rimworld.biotech",
        ["get_AnomalyActive"] = "ludeon.rimworld.anomaly",
        ["get_OdysseyActive"] = "ludeon.rimworld.odyssey",
    };

    private const string SteamSuffix = "_steam";

    internal static List<RimRunnerFinding> Evaluate(RimRunnerIlScan.Stats stats)
    {
        Assembly? test = RimRunnerProduct.TestAssembly;
        if (test == null || test == RimRunnerDefScope.EngineAssembly)
        {
            return [];
        }

        Dictionary<Assembly, string> absentable = RimRunnerPackages.AbsentableAssemblies();
        HashSet<string> tracked = new(StringComparer.OrdinalIgnoreCase);
        foreach (string id in RimRunnerDefScope.OurPackIds)
        {
            if (RimRunnerOutwardPackageIds.BarePackageId(id) is { } bare && bare.Length > 0)
            {
                tracked.Add(NormalizePackageId(bare));
            }
        }

        foreach (string id in RimRunnerPackages.AbsentablePackageIds)
        {
            if (RimRunnerOutwardPackageIds.BarePackageId(id) is { } bare && bare.Length > 0)
            {
                tracked.Add(NormalizePackageId(bare));
            }
        }

        Result result = Scan(test.GetTypes(), RimRunnerIlScan.AllMethods(test).ToList(),
            type => absentable.TryGetValue(type.Assembly, out string? id) ? id : null,
            method => method == PresentMethod,
            packageId => tracked.Contains(NormalizePackageId(packageId)),
            RimRunnerRegistry.CaseTypes, stats);
        return Findings(result, IdsOf);
    }

    private static IEnumerable<string> IdsOf(Type type) =>
        RimRunnerRegistry.All.Where(testCase => testCase.GetType() == type).Select(testCase => testCase.Id);

    internal static Result Scan(IReadOnlyList<Type> types, IReadOnlyList<MethodBase> methods,
        Func<Type, string?> packageOf, Func<MethodBase, bool> isPresenceCheck, Func<string, bool> isTrackedPackage,
        IReadOnlyList<Type> caseTypes, RimRunnerIlScan.Stats stats)
    {
        Result result = new();
        HashSet<Type> scope = [.. types];
        foreach (Type type in types)
        {
            CollectLoadTime(type, packageOf, result.LoadTime);
        }

        var taint = new Dictionary<MethodBase, Taint>();
        var callers = new Dictionary<MethodBase, List<MethodBase>>();

        void AddCaller(MethodBase callee, MethodBase caller)
        {
            if (!callers.TryGetValue(callee, out List<MethodBase>? list))
            {
                list = [];
                callers[callee] = list;
            }

            list.Add(caller);
        }

        foreach (MethodBase method in methods)
        {
            List<CodeInstruction> instructions = RimRunnerIlScan.Instructions(method, stats);
            RimRunnerAbsentReferences.Hit? absent = RimRunnerAbsentReferences.Unreadable(method);
            if (absent != null)
            {
                string? absentPackage = stats.AbsentPackageOf(absent.Assembly);
                if (absentPackage != null)
                {
                    Taint seeded = new() { Detail = "本进程缺程序集 " + absent.Assembly + "，" + absent.Describe() };
                    seeded.Packages.Add(absentPackage);
                    taint[method] = seeded;
                }

                continue;
            }

            string? direct = DirectHit(method, instructions, packageOf, out string? package);
            if (direct != null)
            {
                Taint entry = new() { Detail = direct };
                entry.Packages.Add(package!);
                taint[method] = entry;
            }

            for (int index = 0; index < instructions.Count; index++)
            {
                if (instructions[index].operand is MethodBase callee && IsModsConfigActive(callee))
                {
                    string? target = ModsConfigTarget(callee, instructions, index);
                    if (target == null || isTrackedPackage(target))
                    {
                        result.ModsConfigActive.Add(new Hit { Method = method, Detail = "ModsConfig." + MemberName(callee) });
                    }
                }
            }

            foreach (MethodBase callee in RimRunnerIlScan.CallEdges(method, stats))
            {
                if (callee.DeclaringType == null || !scope.Contains(callee.DeclaringType))
                {
                    continue;
                }

                AddCaller(callee, method);
                if (callee is ConstructorInfo && IsCompilerGenerated(callee.DeclaringType))
                {
                    foreach (MethodInfo body in callee.DeclaringType.GetMethods(AllDeclared))
                    {
                        AddCaller(body, method);
                    }
                }
            }
        }

        Stack<MethodBase> pending = new(taint.Keys);
        while (pending.Count > 0)
        {
            MethodBase callee = pending.Pop();
            if (IsIsolated(callee, packageOf) || !callers.TryGetValue(callee, out List<MethodBase>? list))
            {
                continue;
            }

            Taint source = taint[callee];
            foreach (MethodBase caller in list)
            {
                if (!taint.TryGetValue(caller, out Taint? target))
                {
                    target = new Taint { Detail = "调 " + Describe(callee) };
                    taint[caller] = target;
                }

                int before = target.Packages.Count;
                target.Packages.UnionWith(source.Packages);
                if (target.Packages.Count != before)
                {
                    pending.Push(caller);
                }
            }
        }

        Dictionary<MethodBase, PresenceSummary> presenceMemo = [];

        PresenceSummary SummarizePresence(MethodBase callee)
        {
            if (presenceMemo.TryGetValue(callee, out PresenceSummary? cached))
            {
                return cached;
            }

            List<CodeInstruction> inner = RimRunnerIlScan.Instructions(callee, stats);
            bool calls = false;
            bool unknown = false;
            bool forwardsArg0 = false;
            List<string> fixedTargets = [];
            bool singleStaticArg = callee.IsStatic && callee.GetParameters().Length == 1;
            for (int j = 0; j < inner.Count; j++)
            {
                if (inner[j].operand is MethodBase innerCallee && isPresenceCheck(innerCallee))
                {
                    calls = true;
                    CodeInstruction? prev = PrecedingNonNop(inner, j);
                    if (prev != null && prev.opcode == OpCodes.Ldstr && prev.operand is string literal)
                    {
                        fixedTargets.Add(literal);
                    }
                    else if (singleStaticArg && prev != null && IsLdarg0(prev))
                    {
                        forwardsArg0 = true;
                    }
                    else
                    {
                        unknown = true;
                    }
                }
            }

            PresenceSummary built = new(calls, unknown, forwardsArg0, fixedTargets);
            presenceMemo[callee] = built;
            return built;
        }

        bool ChecksProviderPresence(MethodBase isolatedMethod, string providers)
        {
            List<CodeInstruction> body = RimRunnerIlScan.Instructions(isolatedMethod, stats);
            for (int index = 0; index < body.Count; index++)
            {
                if (body[index].operand is not MethodBase callee)
                {
                    continue;
                }

                if (isPresenceCheck(callee))
                {
                    CodeInstruction? prev = PrecedingNonNop(body, index);
                    if (prev != null && prev.opcode == OpCodes.Ldstr && prev.operand is string literal)
                    {
                        if (PackageMatchesProviders(literal, providers))
                        {
                            return true;
                        }
                    }
                    else
                    {
                        return true;
                    }

                    continue;
                }

                PresenceSummary summary = SummarizePresence(callee);
                if (!summary.CallsPresence)
                {
                    continue;
                }

                if (summary.HasUnknown || summary.FixedTargets.Any(target => PackageMatchesProviders(target, providers)))
                {
                    return true;
                }

                if (summary.ForwardsArg0)
                {
                    CodeInstruction? prev = PrecedingNonNop(body, index);
                    if (prev == null || prev.opcode != OpCodes.Ldstr || prev.operand is not string literal
                        || PackageMatchesProviders(literal, providers))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        foreach (KeyValuePair<MethodBase, Taint> pair in taint.OrderBy(pair => Describe(pair.Key), StringComparer.Ordinal))
        {
            MethodBase method = pair.Key;
            bool isolated = IsIsolated(method, packageOf);
            foreach (string package in pair.Value.Packages)
            {
                if (DeclaresInChain(method.DeclaringType, package))
                {
                    continue;
                }

                if (!isolated)
                {
                    result.Unlabeled.Add(Attribute(new Hit { Method = method, Package = package, Detail = pair.Value.Detail },
                        callers, caseTypes));
                }
                else if (ChecksProviderPresence(method, package))
                {
                    result.SelfGuarded.Add(Attribute(new Hit { Method = method, Package = package }, callers, caseTypes));
                }
            }
        }

        foreach (Hit hit in result.ModsConfigActive)
        {
            Attribute(hit, callers, caseTypes);
        }

        return result;
    }

    private static Hit Attribute(Hit hit, Dictionary<MethodBase, List<MethodBase>> callers, IReadOnlyList<Type> caseTypes)
    {
        HashSet<Type> owners = [];
        HashSet<MethodBase> seen = [hit.Method];
        Queue<MethodBase> queue = new();
        queue.Enqueue(hit.Method);
        while (queue.Count > 0)
        {
            MethodBase method = queue.Dequeue();
            for (Type? current = method.DeclaringType; current != null; current = current.DeclaringType)
            {
                if (typeof(RimRunnerTestCase).IsAssignableFrom(current))
                {
                    owners.UnionWith(caseTypes.Where(current.IsAssignableFrom));
                }
                else if (typeof(RimRunnerScene).IsAssignableFrom(current))
                {
                    hit.ShowcaseReached = true;
                }
            }

            if (callers.TryGetValue(method, out List<MethodBase>? list))
            {
                foreach (MethodBase caller in list)
                {
                    if (seen.Add(caller))
                    {
                        queue.Enqueue(caller);
                    }
                }
            }
        }

        hit.CaseReached = owners.Count > 0;
        hit.Owners = owners
            .Where(owner => hit.Package == null || !DeclaresInChain(owner, hit.Package))
            .OrderBy(owner => owner.FullName, StringComparer.Ordinal)
            .ToList();
        return hit;
    }

    internal static List<RimRunnerFinding> Findings(Result result, Func<Type, IEnumerable<string>> idsOf)
    {
        var findings = new List<RimRunnerFinding>();
        if (result.LoadTime.Count > 0)
        {
            findings.Add(RimRunnerFinding.Warning(null, RimRunnerWarningKey.AbsentableLoadTime,
                "测试程序集的基类、接口或值类型字段用下列类型，本进程里提供它们所在程序集的只有括号里的可缺席包。"
                + "缺这个程序集的名单下整个测试程序集被剔除：\n  "
                + string.Join("\n  ", result.LoadTime.Select(hit => hit.Text))));
        }

        void PerOwner(IEnumerable<Hit> hits, Func<Type, List<Hit>, RimRunnerFinding[]> make)
        {
            foreach (IGrouping<Type, Hit> group in hits
                         .SelectMany(hit => hit.Owners.Select(owner => (owner, hit)))
                         .GroupBy(pair => pair.owner, pair => pair.hit)
                         .OrderBy(group => group.Key.FullName, StringComparer.Ordinal))
            {
                findings.AddRange(make(group.Key, group.ToList()));
            }
        }

        string[] Subjects(Type owner)
        {
            string[] ids = idsOf(owner).ToArray();
            return ids.Length > 0 ? ids : [owner.FullName!];
        }

        PerOwner(result.Unlabeled, (owner, hits) => Subjects(owner)
            .Select(subject => RimRunnerFinding.Warning(subject, RimRunnerWarningKey.AbsentableUnlabeled,
                "用例 " + subject + " 够得着的方法引用只由可缺席包提供的程序集而未声明依赖、未隔离，缺这个程序集的名单下被调用即 JIT 失败：\n  "
                + string.Join("\n  ", hits.Select(UnlabeledLine))))
            .ToArray());
        List<Hit> orphanUnlabeled = result.Unlabeled.Where(hit => !hit.CaseReached && !hit.ShowcaseReached).ToList();
        if (orphanUnlabeled.Count > 0)
        {
            findings.Add(RimRunnerFinding.Warning(null, RimRunnerWarningKey.AbsentableUnlabeled,
                "以下方法引用只由可缺席包提供的程序集而未声明依赖、未隔离，IL 调用边上没有用例调到它们，缺这个程序集的名单下被调用即 JIT 失败：\n  "
                + string.Join("\n  ", orphanUnlabeled.Select(UnlabeledLine))));
        }

        PerOwner(result.SelfGuarded, (owner, hits) => Subjects(owner)
            .Select(subject => RimRunnerFinding.Warning(subject, RimRunnerWarningKey.PresenceInIsolated,
                "用例 " + subject + " 够得着的隔离方法在方法体内判包在场：" + string.Join("、", hits.Select(SelfGuardedLine))
                + "。该方法 JIT 时即解析包里的类型，早于体内判定"))
            .ToArray());
        foreach (Hit hit in result.SelfGuarded.Where(hit => !hit.CaseReached && !hit.ShowcaseReached))
        {
            findings.Add(RimRunnerFinding.Warning(null, RimRunnerWarningKey.PresenceInIsolated,
                "隔离方法在方法体内判包在场：" + SelfGuardedLine(hit) + "。该方法 JIT 时即解析包里的类型，早于体内判定"));
        }

        PerOwner(result.ModsConfigActive, (owner, hits) => Subjects(owner)
            .Select(subject => RimRunnerFinding.Warning(subject, RimRunnerWarningKey.ModsConfigActive,
                "用例 " + subject + " 够得着的方法调 " + string.Join("、", hits.Select(ModsConfigLine))
                + "。" + ModsConfigNote))
            .ToArray());
        return findings;
    }

    private const string ModsConfigNote =
        "该调用不对照名单声明，名单与装载不匹配时仍返回";

    private static string UnlabeledLine(Hit hit) => Describe(hit.Method) + "（" + hit.Package + "；" + hit.Detail + "）";

    private static string SelfGuardedLine(Hit hit) => Describe(hit.Method) + "（" + hit.Package + "）";

    private static string ModsConfigLine(Hit hit) => Describe(hit.Method) + " 调 " + hit.Detail;

    internal static string Describe(MethodBase method) => method.DeclaringType?.FullName + "::" + method.Name;

    private static string MemberName(MethodBase method) =>
        method.Name.StartsWith("get_", StringComparison.Ordinal) ? method.Name[4..] : method.Name;

    private sealed record PresenceSummary(bool CallsPresence, bool HasUnknown, bool ForwardsArg0, List<string> FixedTargets);

    private static string NormalizePackageId(string id) =>
        id.EndsWith(SteamSuffix, StringComparison.OrdinalIgnoreCase) ? id[..^SteamSuffix.Length] : id;

    private static bool PackageMatchesProviders(string target, string providers)
    {
        string normalized = NormalizePackageId(target);
        return RimRunnerPackages.SplitProviders(providers)
            .Any(provider => string.Equals(provider, normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ModsConfigTarget(MethodBase callee, List<CodeInstruction> instructions, int index)
    {
        if (callee.Name == nameof(ModsConfig.IsActive))
        {
            ParameterInfo[] parameters = callee.GetParameters();
            if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string)
                && PrecedingNonNop(instructions, index) is { } prev && prev.opcode == OpCodes.Ldstr
                && prev.operand is string literal)
            {
                return literal;
            }

            return null;
        }

        return DlcActivePackages.TryGetValue(callee.Name, out string? packageId) ? packageId : null;
    }

    private static CodeInstruction? PrecedingNonNop(List<CodeInstruction> instructions, int index)
    {
        for (int i = index - 1; i >= 0; i--)
        {
            if (instructions[i].opcode != OpCodes.Nop)
            {
                return instructions[i];
            }
        }

        return null;
    }

    private static bool IsLdarg0(CodeInstruction instruction)
    {
        if (instruction.opcode == OpCodes.Ldarg_0)
        {
            return true;
        }

        return (instruction.opcode == OpCodes.Ldarg_S || instruction.opcode == OpCodes.Ldarg)
               && instruction.operand switch
               {
                   byte b => b == 0,
                   sbyte sb => sb == 0,
                   short s => s == 0,
                   int n => n == 0,
                   _ => false,
               };
    }

    private static bool IsModsConfigActive(MethodBase method) =>
        method.DeclaringType == typeof(ModsConfig)
        && (method.Name == nameof(ModsConfig.IsActive)
            || method.Name.StartsWith("get_", StringComparison.Ordinal) && method.Name.EndsWith("Active", StringComparison.Ordinal));

    private static bool IsCompilerGenerated(Type type) => type.Name.StartsWith("<", StringComparison.Ordinal);

    private static void CollectLoadTime(Type type, Func<Type, string?> packageOf, List<LoadTimeHit> sink)
    {
        string? hit = Hits(type.BaseType, packageOf);
        if (hit != null)
        {
            sink.Add(new LoadTimeHit(LoadTimeKind.Base, type, null, hit));
        }

        foreach (Type iface in type.GetInterfaces())
        {
            hit = Hits(iface, packageOf);
            if (hit != null)
            {
                sink.Add(new LoadTimeHit(LoadTimeKind.Interface, type, iface.Name, hit));
            }
        }

        foreach (FieldInfo field in RimRunnerAbsentReferences.ReadableFields(type, AllDeclared))
        {
            if (!field.FieldType.IsValueType)
            {
                continue;
            }

            hit = Hits(field.FieldType, packageOf);
            if (hit != null)
            {
                sink.Add(new LoadTimeHit(LoadTimeKind.ValueField, type, field.Name, hit));
            }
        }
    }

    private static string? DirectHit(MethodBase method, List<CodeInstruction> instructions, Func<Type, string?> packageOf,
        out string? package)
    {
        package = SignatureHits(method, packageOf);
        if (package != null)
        {
            return "签名含该包类型";
        }

        MethodBody? body = RimRunnerIlScan.ReadSucceeded(method) ? method.GetMethodBody() : null;
        if (body != null)
        {
            foreach (LocalVariableInfo local in body.LocalVariables)
            {
                package = Hits(local.LocalType, packageOf);
                if (package != null)
                {
                    return "局部变量 " + local.LocalType.Name;
                }
            }
        }

        foreach (CodeInstruction instruction in instructions)
        {
            package = OperandHits(instruction.operand, packageOf);
            if (package != null)
            {
                return "引用 " + OperandName(instruction.operand);
            }
        }

        return null;
    }

    private static string? OperandName(object? operand)
    {
        return operand switch
        {
            Type t => t.FullName,
            MemberInfo m => m.DeclaringType?.FullName + "::" + m.Name,
            _ => operand?.ToString(),
        };
    }

    private static string? OperandHits(object operand, Func<Type, string?> packageOf)
    {
        return operand switch
        {
            Type t => Hits(t, packageOf),
            FieldInfo f => Hits(f.DeclaringType, packageOf) ?? Hits(f.FieldType, packageOf),
            MethodBase m => Hits(m.DeclaringType, packageOf) ?? SignatureHits(m, packageOf),
            _ => null,
        };
    }

    private static string? SignatureHits(MethodBase method, Func<Type, string?> packageOf)
    {
        string? hit;
        if (method is MethodInfo info && (hit = Hits(info.ReturnType, packageOf)) != null)
        {
            return hit;
        }

        foreach (ParameterInfo parameter in method.GetParameters())
        {
            if ((hit = Hits(parameter.ParameterType, packageOf)) != null)
            {
                return hit;
            }
        }

        if (method.IsGenericMethod && !method.IsGenericMethodDefinition)
        {
            foreach (Type arg in method.GetGenericArguments())
            {
                if ((hit = Hits(arg, packageOf)) != null)
                {
                    return hit;
                }
            }
        }

        return null;
    }

    private static string? Hits(Type? type, Func<Type, string?> packageOf)
    {
        if (type == null || type.IsGenericParameter)
        {
            return null;
        }

        if (type.HasElementType)
        {
            return Hits(type.GetElementType(), packageOf);
        }

        string? package = packageOf(type);
        if (package != null)
        {
            return package;
        }

        if (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            foreach (Type arg in type.GetGenericArguments())
            {
                if ((package = Hits(arg, packageOf)) != null)
                {
                    return package;
                }
            }
        }

        return null;
    }

    private static bool IsIsolated(MethodBase method, Func<Type, string?> packageOf) =>
        (method.MethodImplementationFlags & MethodImplAttributes.NoInlining) != 0
        && RimRunnerAbsentReferences.SignatureOf(method) == null
        && SignatureHits(method, packageOf) == null;

    private static bool DeclaresInChain(Type? type, string providers)
    {
        string[] ids = RimRunnerPackages.SplitProviders(providers);
        for (Type? current = type; current != null; current = current.DeclaringType)
        {
            if (RimRunnerPackages.Requirements(current).Any(id => ids.Contains(id, StringComparer.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }
}
