namespace RimRunner;

public static class RimRunnerRegistry
{
    private static readonly Type[] CandidateTypes = RimRunnerDefScope.GaugeTypes()
        .Where(type => !type.IsAbstract
                       && !type.Name.StartsWith("<", StringComparison.Ordinal)
                       && typeof(RimRunnerTestCase).IsAssignableFrom(type))
        .OrderBy(type => type.Name, StringComparer.Ordinal)
        .ToArray();

    internal static IReadOnlyList<Type> CaseTypes => CandidateTypes;

    private static readonly RimRunnerPackagePresence.Judgement Packages = RimRunnerPackages.Judge(CandidateTypes);

    private static readonly List<string> discoveryFailures = [];

    public static readonly RimRunnerTestCase[] All = DiscoverAll();

    internal static IReadOnlyDictionary<string, string> AbsentTypes => Packages.Absent;

    internal static IReadOnlyList<string> PackageFaults => Packages.Faults;

    internal static IReadOnlyList<string> UninstantiatedTypes => Packages.Absent.Keys
        .Concat(Packages.Withheld)
        .Distinct(StringComparer.Ordinal)
        .OrderBy(name => name, StringComparer.Ordinal)
        .ToArray();

    private static RimRunnerTestCase[] DiscoverAll()
    {
        List<RimRunnerTestCase> discovered = Instantiate(CandidateTypes, Packages, discoveryFailures);
        return discovered
            .GroupBy(testCase => testCase.GetType())
            .OrderByDescending(group => group.First().Kind.IsRegression())
            .ThenBy(group => group.Key.Name, StringComparer.Ordinal)
            .SelectMany(group => group)
            .ToArray();
    }

    internal static List<RimRunnerTestCase> Instantiate(IEnumerable<Type> types,
        RimRunnerPackagePresence.Judgement judgement, List<string> failures)
    {
        List<RimRunnerTestCase> discovered = [];
        foreach (Type type in types.Where(type => judgement.Admits(type.FullName!)))
        {
            try
            {
                discovered.AddRange(ExpandType(type));
            }
            catch (Exception exception)
            {
                failures.Add(type.Name + "：" + exception.GetType().Name + " " + exception.Message);
            }
        }

        return discovered;
    }

    private static IEnumerable<RimRunnerTestCase> ExpandType(Type type)
    {
        bool parameterized = type.GetCustomAttributes(
            typeof(RimRunnerParameterizedCaseAttribute), false).Length > 0;
        if (parameterized)
        {
            MethodInfo? expand = type.GetMethod(
                "Expand", BindingFlags.Public | BindingFlags.Static, null,
                Type.EmptyTypes, null);
            if (expand == null || !typeof(IEnumerable<RimRunnerTestCase>).IsAssignableFrom(expand.ReturnType))
            {
                throw new InvalidOperationException(
                    "声明 [RimRunnerParameterizedCase]，缺返回用例集合的 public static Expand()");
            }

            foreach (RimRunnerTestCase expanded in (IEnumerable<RimRunnerTestCase>)expand.Invoke(null, null)!)
            {
                yield return expanded;
            }

            yield break;
        }

        if (type.GetConstructor(Type.EmptyTypes) == null)
        {
            throw new InvalidOperationException(
                "没有无参构造，也未声明 [RimRunnerParameterizedCase]");
        }

        yield return (RimRunnerTestCase)Activator.CreateInstance(type)!;
    }

    private static readonly List<RimRunnerFinding> findings = [];

    internal static IReadOnlyList<RimRunnerFinding> Findings => findings;

    private static void Fact(RimRunnerTestCase testCase, string text) =>
        findings.Add(RimRunnerFinding.Fact(testCase.Id, text));

    private static void Warn(RimRunnerTestCase testCase, string key, string text) =>
        findings.Add(RimRunnerFinding.Warning(testCase.Id, key, text));

    private static void BatchFact(string text) => findings.Add(RimRunnerFinding.Fact(null, text));

    private static void TypeFact(Type type, string text)
    {
        bool any = false;
        foreach (RimRunnerTestCase testCase in All.Where(testCase => testCase.GetType() == type))
        {
            any = true;
            Fact(testCase, text);
        }

        if (!any)
            findings.Add(RimRunnerFinding.Fact(type.FullName, text));
    }

    private static void TypeWarn(Type type, string key, string text)
    {
        bool any = false;
        foreach (RimRunnerTestCase testCase in All.Where(testCase => testCase.GetType() == type))
        {
            any = true;
            Warn(testCase, key, text);
        }

        if (!any)
            findings.Add(RimRunnerFinding.Warning(type.FullName, key, text));
    }

    static RimRunnerRegistry()
    {
        if (discoveryFailures.Count > 0)
        {
            BatchFact("反射发现失败的用例类 " + discoveryFailures.Count + " 个：\n  " + string.Join("\n  ", discoveryFailures));
        }

        foreach (RimRunnerTestCase testCase in All)
            findings.AddRange(RimRunnerLogAcks.DeclarationFindings(testCase.Id, testCase.Kind,
                RimRunnerLogAcks.Declared(testCase.GetType())));

        foreach (RimRunnerTestCase testCase in All)
            foreach (BodyShapeFault fault in BodyShapeFaults(testCase.Id, testCase.GetType()))
                Fact(testCase, fault.Text);

        foreach (RimRunnerTestCase testCase in All)
            foreach (UnboundDeclaration note in UnboundDeclarations(testCase.Id, testCase.GetType()))
                Warn(testCase, RimRunnerWarningKey.InertDeclaration, note.Text);

        foreach (RimRunnerTestCase testCase in All.Where(testCase => testCase is IRimRunnerShardable))
        {
            if (testCase.Kind != RimRunnerKind.Readout)
                Fact(testCase, $"用例 {testCase.Id} 实现 IRimRunnerShardable，"
                               + $"只限 Kind=Readout（实际 {testCase.Kind}）：引擎不合并各片的回归结论");
            double floor = ((IRimRunnerShardable)testCase).MinShardWeight;
            if (double.IsNaN(floor) || double.IsInfinity(floor) || floor <= 0)
                Fact(testCase, $"用例 {testCase.Id} 的 MinShardWeight 须为正有限数，"
                               + $"实际 {floor.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}");
        }

        foreach (RimRunnerTestCase testCase in All)
        {
            foreach (string fault in RimRunnerCheckpointPlanner.DeclarationFaults(testCase.Id,
                         testCase as IRimRunnerPhasedCase, testCase.SitedOnIsolatedMap))
                Fact(testCase, fault);
            foreach (string inert in RimRunnerCheckpointPlanner.InertDeclarations(testCase.Id,
                         testCase as IRimRunnerPhasedCase, testCase.SitedOnIsolatedMap))
                Warn(testCase, RimRunnerWarningKey.InertDeclaration, inert);
            string? missingBudget = RimRunnerCheckpointPlanner.MissingTickBudget(testCase.Id,
                testCase as IRimRunnerPhasedCase, testCase.SitedOnIsolatedMap, testCase.Level);
            if (missingBudget != null)
                Fact(testCase, missingBudget);
            string? note = RimRunnerCheckpointPlanner.DeclarationNote(testCase.Id,
                testCase as IRimRunnerPhasedCase, testCase.SitedOnIsolatedMap);
            if (note != null)
                Warn(testCase, RimRunnerWarningKey.InertDeclaration, note);
            string? mainHook = RimRunnerCheckpointPlanner.MainMapLoadTickHook(testCase.Id,
                RimRunnerCaseBody.WritesBeforeLoadTick(testCase.GetType()), testCase.SitedOnIsolatedMap);
            if (mainHook != null)
                Fact(testCase, mainHook);
        }

        foreach (RimRunnerTestCase testCase in All)
        {
            RimRunnerIsolatedMap.SiteFindings(testCase.Id, testCase.Level, testCase.DeclaredMapSite,
                out List<string> faults, out List<KeyValuePair<string, string>> warnings, out string? note);
            foreach (string fault in faults)
                Fact(testCase, fault);
            foreach (KeyValuePair<string, string> warning in warnings)
                Warn(testCase, warning.Key, warning.Value);
            if (note != null)
                Warn(testCase, RimRunnerWarningKey.InertDeclaration, note);
        }

        foreach (RimRunnerTestCase live in All.Where(testCase => testCase.DeclaresLiveStoryteller))
        {
            if (live.Level != RimRunnerLevel.World)
                Fact(live, $"用例 {live.Id} 声明 LiveStoryteller，须 Level=World（实际 Level={live.Level}）");
        }

        foreach (RimRunnerTestCase testCase in All.Where(testCase => testCase is IRimRunnerRealFrameCase))
        {
            float seconds = ((IRimRunnerRealFrameCase)testCase).WallClockTimeoutSeconds;
            if (seconds <= 0f || float.IsNaN(seconds) || float.IsInfinity(seconds))
                Fact(testCase, $"用例 {testCase.Id} 的 WallClockTimeoutSeconds={seconds}"
                               + "——真实帧墙钟上限须为有限正数");
        }

        foreach (RimRunnerTestCase testCase in All)
            findings.AddRange(RimRunnerWarnings.AckDeclarationFindings(testCase.Id, testCase.GetType()));

        foreach (Type type in CandidateTypes)
        foreach (string fault in RimRunnerPackages.DeclarationFaults(type))
            TypeWarn(type, RimRunnerWarningKey.InertDeclaration, fault);
    }

    internal enum BodyShapeFaultKind
    {
        PhasedAndRealFrame,
        PhasedNotBound,
        PhasedWritesRun,
        PhasedWritesSteps,
        RealFrameWritesRun,
        RealFrameNoBody,
        RunAndSteps,
        NoBody,
    }

    internal readonly record struct BodyShapeFault(BodyShapeFaultKind Kind, string Text);

    internal static List<BodyShapeFault> BodyShapeFaults(string id, Type type)
    {
        List<BodyShapeFault> faults = [];
        if (RimRunnerCaseBody.KindOf(type) is not var (_, kindBase, contextType))
        {
            return faults;
        }

        bool phased = typeof(IRimRunnerPhasedCase).IsAssignableFrom(type);
        bool realFrame = typeof(IRimRunnerRealFrameCase).IsAssignableFrom(type);
        bool writesRun = RimRunnerCaseBody.WritesRun(type, kindBase, contextType);
        bool writesSteps = RimRunnerCaseBody.WritesSteps(type, kindBase, contextType);
        string context = contextType.Name;

        if (phased && realFrame)
            faults.Add(new(BodyShapeFaultKind.PhasedAndRealFrame, $"用例 {id} 同时实现多阶段与真实帧接口：一条用例只有一份正文"));
        if (phased && !typeof(IRimRunnerPhasedCase<>).MakeGenericType(contextType).IsAssignableFrom(type))
            faults.Add(new(BodyShapeFaultKind.PhasedNotBound, $"用例 {id} 的多阶段正文未接上：未实现 IRimRunnerPhasedCase<{context}>，引擎无法调用 RunPhase"));
        if (phased && writesRun)
            faults.Add(new(BodyShapeFaultKind.PhasedWritesRun, $"用例 {id} 是多阶段用例又写 Run：正文只在 RunPhase"));
        if (phased && writesSteps)
            faults.Add(new(BodyShapeFaultKind.PhasedWritesSteps, $"用例 {id} 是多阶段用例又写 RunSteps：正文只在 RunPhase"));
        if (realFrame && writesRun)
            faults.Add(new(BodyShapeFaultKind.RealFrameWritesRun, $"用例 {id} 是真实帧用例又写 Run：正文只在 RunSteps({context} ctx)"));
        if (realFrame && !writesRun && !writesSteps)
            faults.Add(new(BodyShapeFaultKind.RealFrameNoBody, $"用例 {id} 声明真实帧却没有正文：真实帧用例的正文是 RunSteps({context} ctx)"));
        if (!phased && !realFrame && writesRun && writesSteps)
            faults.Add(new(BodyShapeFaultKind.RunAndSteps, $"用例 {id} 同时写 Run 与 RunSteps：一条用例只有一份正文"));
        if (!phased && !realFrame && !writesRun && !writesSteps)
            faults.Add(new(BodyShapeFaultKind.NoBody, $"用例 {id} 没有正文：未覆写 Run({context} ctx) 或 RunSteps({context} ctx)，也未实现 IRimRunnerPhasedCase<{context}>"));
        return faults;
    }

    private static readonly (Type Interface, string Member)[] DefaultedDeclarations =
    [
        (typeof(IRimRunnerPhasedCase), nameof(IRimRunnerPhasedCase.CheckpointTicks)),
        (typeof(IRimRunnerPhasedCase), nameof(IRimRunnerPhasedCase.CheckpointTickBudget)),
        (typeof(IRimRunnerPhasedCase), nameof(IRimRunnerPhasedCase.CheckpointClockBudget)),
        (typeof(IRimRunnerPhasedCase), nameof(IRimRunnerPhasedCase.RequiresSoloBatch)),
        (typeof(IRimRunnerRealFrameCase), nameof(IRimRunnerRealFrameCase.LiveStoryteller))
    ];

    internal enum UnboundDeclarationKind
    {
        MemberWithoutContract,
        MemberNotBound,
        HookNotPhased,
        HookNotBound,
        HookWrongContext,
    }

    internal readonly record struct UnboundDeclaration(UnboundDeclarationKind Kind, Type? Contract, string Text);

    internal static List<UnboundDeclaration> UnboundDeclarations(string id, Type type)
    {
        List<UnboundDeclaration> faults = [];
        foreach ((Type contract, string member) in DefaultedDeclarations)
        {
            MethodInfo[] getters = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.Name == member && property.GetMethod != null)
                .Select(property => property.GetMethod!)
                .ToArray();
            if (getters.Length == 0)
            {
                continue;
            }

            if (!contract.IsAssignableFrom(type))
            {
                faults.Add(new(UnboundDeclarationKind.MemberWithoutContract, contract,
                    $"用例 {id} 写 {member}，但未实现 {contract.Name}：这条声明不起作用"));
                continue;
            }

            InterfaceMapping map = type.GetInterfaceMap(contract);
            MethodInfo bound = map.TargetMethods[Array.IndexOf(map.InterfaceMethods, contract.GetProperty(member)!.GetMethod)];
            if (!getters.Contains(bound))
            {
                faults.Add(new(UnboundDeclarationKind.MemberNotBound, contract,
                    $"用例 {id} 写 {member}，引擎经 {contract.Name} 读到的却不是它：基类列接口而本类没有再列"));
            }
        }

        const string hook = nameof(IRimRunnerPhasedCase<RimRunnerContext>.BeforeLoadTick);
        MethodInfo? boundHook = RimRunnerCaseBody.BeforeLoadTickBody(type);
        List<Type> writtenContexts =
        [
            .. type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(method => method.Name == hook && method != boundHook && method.GetParameters() is var parameters
                                 && parameters.Length == 2 && typeof(RimRunnerContext).IsAssignableFrom(parameters[0].ParameterType)
                                 && parameters[1].ParameterType == typeof(int))
                .Select(method => method.GetParameters()[0].ParameterType),
        ];
        if (writtenContexts.Count > 0 && boundHook == null)
        {
            List<Type> contexts =
            [
                .. type.GetInterfaces()
                    .Where(contract => contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IRimRunnerPhasedCase<>))
                    .Select(contract => contract.GetGenericArguments()[0]),
            ];
            faults.Add(!typeof(IRimRunnerPhasedCase).IsAssignableFrom(type)
                ? new(UnboundDeclarationKind.HookNotPhased, null,
                    $"用例 {id} 写 {hook}，但不是多阶段用例：该方法不会被调用")
                : contexts.Count == 0 || writtenContexts.Any(contexts.Contains)
                    ? new(UnboundDeclarationKind.HookNotBound, null,
                        $"用例 {id} 写 {hook}，引擎经 IRimRunnerPhasedCase<TContext> 调到的却不是它：基类列接口而本类没有再列")
                    : new(UnboundDeclarationKind.HookWrongContext, null,
                        $"用例 {id} 写 {hook}，参数取 {string.Join("、", writtenContexts.Select(context => context.Name))}，"
                        + $"用例的 IRimRunnerPhasedCase<TContext> 取 {string.Join("、", contexts.Select(context => context.Name))}"));
        }

        return faults;
    }

    internal static IReadOnlyList<KeyValuePair<string, string>> DuplicateIdFaults =>
        DuplicateIds(All.Select(testCase => new KeyValuePair<string, Type>(testCase.Id, testCase.GetType())))
            .Select(fault => new KeyValuePair<string, string>(fault.Id, fault.Text))
            .ToList();

    internal sealed record DuplicateId(string Id, int Count, IReadOnlyList<string?> Types)
    {
        internal string Text =>
            $"用例 Id 重复：{Id} 登记 {Count} 次（{string.Join(" / ", Types)}）";
    }

    internal static List<DuplicateId> DuplicateIds(IEnumerable<KeyValuePair<string, Type>> registrations)
    {
        return registrations
            .GroupBy(registration => registration.Key, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => new DuplicateId(group.Key, group.Count(),
                group.Select(registration => registration.Value.FullName).ToList()))
            .ToList();
    }

    internal static IEnumerable<RimRunnerTestCase> FirstPerId(IEnumerable<RimRunnerTestCase> cases)
    {
        return cases
            .GroupBy(testCase => testCase.Id, StringComparer.Ordinal)
            .Select(group => group.First());
    }

    public static RimRunnerTestCase? FindCase(string id)
    {
        return All.FirstOrDefault(testCase =>
            string.Equals(testCase.Id, id, StringComparison.Ordinal));
    }

    internal static bool CountsAsRegression(string id) =>
        FindCase(id)?.Kind.IsRegression() ?? true;
}
