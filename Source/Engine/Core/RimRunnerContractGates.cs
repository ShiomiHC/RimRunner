using System.Diagnostics;
using System.Reflection.Emit;
using Verse.AI;

namespace RimRunner;

internal static class RimRunnerContractGates
{
    private static Dictionary<string, List<RimRunnerFinding>>? findings;
    private static List<RimRunnerFinding>? packageFindings;
    private static HashSet<string>? screenshotCases;

    private static Dictionary<string, List<string>>? needReadsById;

    internal static long ScanMs { get; private set; }

    internal static bool Ready => findings != null;

    internal static bool IlScanIncomplete { get; private set; }

    internal static string? ScanFault { get; private set; }

    internal static IEnumerable<RimRunnerFinding> FindingsInRegistryOrder()
    {
        foreach (RimRunnerTestCase testCase in RimRunnerRegistry.All)
        {
            if (findings != null && findings.TryGetValue(testCase.Id, out List<RimRunnerFinding>? found))
            {
                foreach (RimRunnerFinding finding in found)
                {
                    yield return finding;
                }
            }
        }

        if (packageFindings != null)
        {
            foreach (RimRunnerFinding finding in packageFindings)
            {
                yield return finding;
            }
        }
    }

    private static readonly string[] WorldPropertyNames =
        ["get_WorldGrid", "get_FactionManager", "get_WorldPawns"];

    private static readonly Lazy<HashSet<string>> GameFindMembers = new(ComputeGameFindMembers);

    private static readonly string[] DefsTypeNameHeads = ["DefDatabase", "LoadedModManager", "GenDefDatabase"];

    private const string DefOfTypeNameTail = "DefOf";
    private static readonly string[] DefsMethodNames =
        ["GetAllPatchedMethods", "GetPatchInfo", "OwnDefs", "OwnAndOfficialDefs"];

    internal static bool EnsureRan()
    {
        if (findings != null)
        {
            return false;
        }

        var pendingFindings = new Dictionary<string, List<RimRunnerFinding>>(StringComparer.Ordinal);
        var pendingImage = new HashSet<string>(StringComparer.Ordinal);
        var pendingNeedReads = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HashSet<Assembly> scope = [.. RimRunnerDefScope.ProbeAssemblies];
        scope.UnionWith(RimRunnerDefScope.GaugeAssemblies);

        RimRunnerIlScan.ClearCache();
        RimRunnerIlScan.Stats stats = new();
        Stopwatch stopwatch = Stopwatch.StartNew();
        foreach (RimRunnerTestCase testCase in RimRunnerRegistry.All)
        {
            scope.Add(testCase.GetType().Assembly);
            ScanCase(testCase, stats, scope, pendingFindings, pendingImage, pendingNeedReads);
        }

        List<RimRunnerFinding> pendingPackage = RimRunnerPackageAbsenceGate.Evaluate(stats);

        stopwatch.Stop();
        ScanMs = stopwatch.ElapsedMilliseconds;
        screenshotCases = pendingImage;
        needReadsById = pendingNeedReads;
        packageFindings = pendingPackage;
        findings = pendingFindings;
        IlScanIncomplete = stats.MethodsFailed > 0 || stats.MethodsAbsentPackage > 0;

        if (stats.MethodsFailed > 0)
        {
            ScanFault = "契约闸 IL 扫描读取失败 " + stats.MethodsFailed
                        + " 个方法，Level/Kind 判定不完整：\n" + stats.Summary;
            RimRunnerEngineLog.Warn(ScanFault);
        }

        return true;
    }

    internal static bool CallsScreenshot(string id)
    {
        EnsureRan();
        return ScanFault != null || screenshotCases!.Contains(id);
    }

    private static void ScanCase(
        RimRunnerTestCase testCase, RimRunnerIlScan.Stats stats, IEnumerable<Assembly> scope,
        Dictionary<string, List<RimRunnerFinding>> findingsOut, HashSet<string> imageOut,
        Dictionary<string, List<string>> needReadsOut)
    {
        List<RimRunnerFinding> found = Evaluate(
            testCase.GetType(), testCase.Kind, testCase.Level, testCase.Id, stats, scope, EnterCaseSideTypes,
            out bool callsImage, out List<string> timingHits, out List<string> needReads);
        needReadsOut[testCase.Id] = needReads;
        if (callsImage)
        {
            imageOut.Add(testCase.Id);
            if (testCase.MinTextures < RimRunnerTextures.Native)
            {
                found.Add(RimRunnerFinding.Warning(testCase.Id, RimRunnerWarningKey.ScreenshotTexturesUndeclared,
                    "用例 " + testCase.Id + " 的调用闭包里有 ctx.AddScreenshot，MinTextures 申报的是 "
                    + RimRunnerTextureTier.Name(testCase.MinTextures)));
            }
        }

        string? timingNote = TimingNote(testCase.Kind, testCase.DeclaresQuietMachine, timingHits);
        if (timingNote != null)
        {
            found.Add(RimRunnerFinding.Warning(testCase.Id, RimRunnerWarningKey.TimingUndeclared, timingNote));
        }

        if (found.Count > 0)
        {
            findingsOut[testCase.Id] = found;
        }
    }

    internal static List<string> EvaluateProbe(Type probeType, RimRunnerKind kind, RimRunnerLevel level)
    {
        return EvaluateProbeFindings(probeType, kind, level)
            .Where(finding => finding.Key != RimRunnerWarningKey.LevelOver)
            .Select(finding => finding.Text)
            .ToList();
    }

    internal static List<RimRunnerFinding> EvaluateProbeFindings(
        Type probeType, RimRunnerKind kind, RimRunnerLevel level)
    {
        HashSet<Assembly> scope =
        [
.. RimRunnerDefScope.ProbeAssemblies,             probeType.Assembly
        ];
        return Evaluate(
            probeType, kind, level, probeType.Name,
            new RimRunnerIlScan.Stats(), scope, EnterCaseSideTypes, out _, out _, out _);
    }

    internal static List<string> NeedReadsProbe(Type probeType, RimRunnerKind kind, RimRunnerLevel level)
    {
        HashSet<Assembly> scope =
        [
.. RimRunnerDefScope.ProbeAssemblies,             probeType.Assembly
        ];
        Evaluate(probeType, kind, level, probeType.Name,
            new RimRunnerIlScan.Stats(), scope, EnterCaseSideTypes, out _, out _, out List<string> needReads);
        return needReads;
    }

    internal static List<string> NeedReadsOf(string id)
    {
        return needReadsById != null && needReadsById.TryGetValue(id, out List<string>? reads)
            ? reads
            : [];
    }

    internal static string? TimingNoteProbe(Type probeType, RimRunnerKind kind, RimRunnerLevel level, bool quietMachine)
    {
        HashSet<Assembly> scope =
        [
.. RimRunnerDefScope.ProbeAssemblies,             probeType.Assembly
        ];
        Evaluate(probeType, kind, level, probeType.Name,
            new RimRunnerIlScan.Stats(), scope, EnterCaseSideTypes, out _, out List<string> timingHits, out _);
        return TimingNote(kind, quietMachine, timingHits);
    }

    private static string? TimingNote(RimRunnerKind kind, bool quietMachine, List<string> timingHits)
    {
        if (kind != RimRunnerKind.Readout || quietMachine || timingHits.Count == 0)
        {
            return null;
        }

        return "含计时调用（" + string.Join("、", timingHits.Distinct()) + "），未声明 QuietMachine";
    }

    private static bool EnterCaseSideTypes(Type declaring)
    {
        if (typeof(RimRunnerTestCase).IsAssignableFrom(declaring))
        {
            return true;
        }

        for (Type? outer = declaring.DeclaringType; outer != null; outer = outer.DeclaringType)
        {
            if (typeof(RimRunnerTestCase).IsAssignableFrom(outer))
            {
                return true;
            }
        }

        string ns = declaring.Namespace ?? "";
        return !ns.StartsWith("RimRunner", StringComparison.Ordinal);
    }

    internal static bool IsCaseSide(Type type, Type caseType)
    {
        Assembly caseAssembly = caseType.Assembly;
        if (type.Assembly != caseAssembly)
        {
            return false;
        }

        if (!RimRunnerDefScope.IsEngineSide(caseAssembly))
        {
            return true;
        }

        for (Type? current = type; current != null; current = current.DeclaringType)
        {
            if (current == caseType)
            {
                return true;
            }
        }

        return false;
    }

    private static List<RimRunnerFinding> Evaluate(
        Type caseType, RimRunnerKind kind, RimRunnerLevel level, string id,
        RimRunnerIlScan.Stats stats, IEnumerable<Assembly> scope,
        Func<Type, bool> enterType, out bool callsImage, out List<string> timingHits,
        out List<string> needReads)
    {
        string rootName = typeof(IRimRunnerPhasedCase).IsAssignableFrom(caseType)
            ? "RunPhase"
            : RimRunnerCaseBody.KindOf(caseType) is var (_, kindBase, contextType)
              && RimRunnerCaseBody.WritesSteps(caseType, kindBase, contextType)
                ? "RunSteps"
                : "Run";
        MethodInfo? root = caseType.GetMethod(rootName);
        List<MethodBase> roots = root != null
            ? [root]
            : [];
        if (RimRunnerCaseBody.BeforeLoadTickBody(caseType) is MethodInfo beforeLoadTick)
        {
            roots.Add(beforeLoadTick);
        }

        HashSet<MethodBase> closure = RimRunnerIlScan.Closure(
            roots, stats, scope, enterType, followDispatch: true);

        ClosureHits hits = new();
        foreach (MethodBase method in closure)
        {
            bool readerIsCaseSide = method.DeclaringType != null && IsCaseSide(method.DeclaringType, caseType);
            List<CodeInstruction> body = RimRunnerIlScan.Instructions(method, stats);
            for (int index = 0; index < body.Count; index++)
            {
                CodeInstruction instruction = body[index];
                ClassifyOperand(instruction.operand, readerIsCaseSide, hits);

                if (instruction.operand is MethodBase callee
                    && callee.DeclaringType == typeof(RimRunnerRegressionContext)
                    && callee.Name == nameof(RimRunnerRegressionContext.AssertTrue)
                    && RimRunnerIlScan.IsConstantTrueArgument(method, body, index, callee, 1))
                {
                    hits.ConstantAssert.Add((method.DeclaringType?.Name ?? "?") + "." + method.Name);
                }
            }
        }

        callsImage = hits.Image.Count > 0;
        timingHits = hits.Timing;
        needReads = hits.NeedReads.Distinct().OrderBy(name => name, StringComparer.Ordinal).ToList();
        List<string> worldHits = hits.World;
        List<string> defsHits = hits.Defs;
        List<string> tickHits = hits.Tick;

        RimRunnerLevel closureLevel = worldHits.Count > 0 || tickHits.Count > 0
            ? RimRunnerLevel.World
            : defsHits.Count > 0
                ? RimRunnerLevel.Defs
                : RimRunnerLevel.Assembly;

        List<RimRunnerFinding> found = [];
        string head = "用例 " + id + " ";
        if (closureLevel == RimRunnerLevel.World && level < RimRunnerLevel.World)
        {
            found.Add(RimRunnerFinding.Warning(id, RimRunnerWarningKey.LevelUnder,
                head + "Level 申报 " + level + "，调用闭包触碰到 " + closureLevel + " 层 API："
                + string.Join("、", worldHits.Concat(tickHits).Distinct())));
        }
        else if (closureLevel < level)
        {
            found.Add(RimRunnerFinding.Warning(id, RimRunnerWarningKey.LevelOver,
                head + "Level 申报 " + level + "，调用闭包只触碰到 " + closureLevel + " 层 API"));
        }

        if (hits.ConstantAssert.Count > 0)
        {
            found.Add(RimRunnerFinding.UnackableWarning(id, RimRunnerWarningKey.AssertConstantTrue,
                head + "有 " + hits.ConstantAssert.Count + " 处断言的条件实参是字面量 true："
                + string.Join("、", hits.ConstantAssert.Distinct())));
        }

        if (hits.ForceWait.Count > 0)
        {
            found.Add(RimRunnerFinding.Warning(id, RimRunnerWarningKey.ForceWaitWindow,
                head + "调用 PawnUtility.ForceWait：到期拍数固定"));
        }

        return found;
    }

    private sealed class ClosureHits
    {
        internal readonly List<string> World = [];
        internal readonly List<string> Defs = [];
        internal readonly List<string> Tick = [];
        internal readonly List<string> Image = [];
        internal readonly List<string> Timing = [];
        internal readonly List<string> ConstantAssert = [];
        internal readonly List<string> ForceWait = [];
        internal readonly List<string> NeedReads = [];
    }

    private static readonly (Type Type, string Member)[] NeedReadMembers =
    [
        (typeof(Need), "get_CurLevel"),
        (typeof(Need), "get_CurInstantLevel"),
        (typeof(Need), "get_CurLevelPercentage"),
        (typeof(Need), "get_CurInstantLevelPercentage"),
        (typeof(Need_Food), "get_CurCategory"),
        (typeof(Need_Rest), "get_CurCategory"),
        (typeof(Need_Joy), "get_CurCategory"),
        (typeof(Need_Chemical), "get_CurCategory"),
        (typeof(Need_Comfort), "get_CurCategory"),
        (typeof(Need_Beauty), "get_CurCategory"),
        (typeof(Need_Outdoors), "get_CurCategory"),
        (typeof(Need_Indoors), "get_CurCategory"),
        (typeof(Need_RoomSize), "get_CurCategory"),
        (typeof(Need_Play), "get_CurCategory"),
        (typeof(Need_Learning), "get_CurCategory"),
        (typeof(Need_Authority), "get_CurCategory"),
        (typeof(Need_Food), "get_Starving"),
        (typeof(Need_Mood), "get_CurInstantLevel"),
        (typeof(Need_Mood), "get_MoodString"),
        (typeof(MentalBreaker), "get_CurMood"),
        (typeof(MentalBreaker), "get_BreakThresholdMinor"),
        (typeof(MentalBreaker), "get_BreakThresholdMajor"),
        (typeof(MentalBreaker), "get_BreakThresholdExtreme"),
        (typeof(MentalBreaker), "get_BreakMinorIsImminent"),
        (typeof(MentalBreaker), "get_BreakMajorIsImminent"),
        (typeof(MentalBreaker), "get_BreakExtremeIsImminent"),
        (typeof(ThoughtHandler), "TotalMoodOffset"),
        (typeof(ThoughtHandler), "MoodOffsetOfGroup"),
        (typeof(ThoughtHandler), "GetAllMoodThoughts"),
        (typeof(ThoughtHandler), "GetMoodThoughtsFor"),
        (typeof(ThoughtHandler), "GetMoodThoughts"),
        (typeof(ThoughtHandler), "GetSocialThoughts"),
        (typeof(ThoughtHandler), "TotalOpinionOffset"),
        (typeof(ThoughtHandler), "OpinionOffsetOfGroup"),
        (typeof(ThoughtHandler), "GetDistinctMoodThoughtGroups"),
        (typeof(ThoughtHandler), "GetDistinctSocialThoughtGroups"),
        (typeof(MemoryThoughtHandler), "get_Memories"),
        (typeof(MemoryThoughtHandler), "GetFirstMemoryOfDef"),
        (typeof(MemoryThoughtHandler), "OldestMemoryOfDef"),
        (typeof(MemoryThoughtHandler), "NumMemoriesOfDef"),
        (typeof(MemoryThoughtHandler), "NumMemoriesInGroup"),
        (typeof(MemoryThoughtHandler), "AnyMemoryConcerns"),
        (typeof(SituationalThoughtHandler), "AppendMoodThoughts"),
        (typeof(SituationalThoughtHandler), "AppendSocialThoughts"),
    ];

    private static readonly Dictionary<Type, HashSet<string>> NeedReadsByType = NeedReadMembers
        .GroupBy(entry => entry.Type)
        .ToDictionary(group => group.Key, group => group.Select(entry => entry.Member)
            .ToHashSet(StringComparer.Ordinal));

    private static void ClassifyOperand(object operand, bool readerIsCaseSide, ClosureHits hits)
    {
        switch (operand)
        {
            case MethodBase method:
                ClassifyMember(method.DeclaringType?.Name, method.Name,
                    method.DeclaringType, readerIsCaseSide, hits);
                break;
            case FieldInfo field:
                ClassifyMember(field.DeclaringType?.Name, field.Name,
                    field.DeclaringType, readerIsCaseSide, hits);
                break;
        }
    }

    private static void ClassifyMember(
        string? declaringTypeName, string memberName, Type? declaringType,
        bool readerIsCaseSide, ClosureHits hits)
    {
        if (declaringType != null
            && NeedReadsByType.TryGetValue(declaringType, out HashSet<string>? needMembers)
            && needMembers.Contains(memberName))
        {
            hits.NeedReads.Add(declaringType.Name + "." + memberName);
        }

        if (readerIsCaseSide && declaringType == typeof(PawnUtility) && memberName == nameof(PawnUtility.ForceWait))
        {
            hits.ForceWait.Add("PawnUtility." + memberName);
        }

        if (declaringType == typeof(RimRunnerReadoutContext)
            && memberName == nameof(RimRunnerReadoutContext.AddScreenshot))
        {
            hits.Image.Add("ctx." + memberName);
        }

        if (declaringType == typeof(Stopwatch)
            || declaringType == typeof(RimRunnerProfilerBridge))
        {
            hits.Timing.Add(declaringTypeName + "." + memberName);
        }
        else if (declaringType == typeof(RimRunnerTimeDriver) && memberName == nameof(RimRunnerTimeDriver.MeasureRawTicks))
        {
            hits.Timing.Add("RimRunnerTimeDriver." + memberName);
        }
        else if (declaringType == typeof(RimRunnerContext) && memberName == nameof(RimRunnerContext.OpenMapCostMeter))
        {
            hits.Timing.Add("ctx." + memberName);
        }

        if (declaringType == typeof(RimRunnerTimeDriver))
        {
            hits.Tick.Add("RimRunnerTimeDriver." + memberName);
            return;
        }

        if (declaringType == typeof(RimRunnerWait)
            && memberName is nameof(RimRunnerWait.Ticks) or nameof(RimRunnerWait.RealSeconds))
        {
            hits.Tick.Add("RimRunnerWait." + memberName);
            return;
        }

        if (declaringType == typeof(RimRunnerContext)
            && (memberName == "get_Map" || memberName == "Map"))
        {
            hits.World.Add("ctx.Map");
            return;
        }

        if (declaringType == typeof(RimRunnerPawnGen))
        {
            hits.World.Add("RimRunnerPawnGen." + memberName);
            return;
        }

        if ((declaringType == typeof(RimRunnerContext) && memberName == nameof(RimRunnerContext.GetPawn))
            || (declaringType == typeof(GenSpawn) && memberName.StartsWith("Spawn", StringComparison.Ordinal))
            || (declaringType == typeof(RimRunnerPawns)
                && (memberName.StartsWith("Spawn", StringComparison.Ordinal)
                    || memberName.StartsWith("Generate", StringComparison.Ordinal)))
            || (declaringType == typeof(SkyfallerMaker) && memberName.StartsWith("Spawn", StringComparison.Ordinal))
            || (declaringType == typeof(Find) && WorldPropertyNames.Contains(memberName))
            || (readerIsCaseSide && declaringType == typeof(Find) && GameFindMembers.Value.Contains(memberName))
            || (readerIsCaseSide && declaringType == typeof(Current) && memberName == "get_Game")
            || (readerIsCaseSide && declaringType == typeof(PawnGenerator)
                                 && memberName.StartsWith("GeneratePawn", StringComparison.Ordinal)))
        {
            hits.World.Add((declaringTypeName ?? "?") + "." + memberName);
            return;
        }

        if (DefsMethodNames.Contains(memberName)
            || declaringType == typeof(Translator)
            || declaringType == typeof(TranslatorFormattedStringExtensions)
            || (declaringTypeName != null
                && (DefsTypeNameHeads.Any(head =>
                        declaringTypeName.StartsWith(head, StringComparison.Ordinal))
                    || declaringTypeName.EndsWith(DefOfTypeNameTail, StringComparison.Ordinal))))
        {
            hits.Defs.Add((declaringTypeName ?? "?") + "." + memberName);
        }
    }
    private static HashSet<string> ComputeGameFindMembers()
    {
        RimRunnerIlScan.Stats stats = new();
        MethodInfo game = AccessTools.PropertyGetter(typeof(Current), nameof(Current.Game));
        Dictionary<MethodBase, bool> memo = [];

        bool Reaches(MethodBase method)
        {
            if (memo.TryGetValue(method, out bool known))
            {
                return known;
            }

            memo[method] = false;
            bool reached = false;
            foreach (CodeInstruction instruction in RimRunnerIlScan.Instructions(method, stats))
            {
                if (instruction.operand is Type type && typeof(UIRoot_Play).IsAssignableFrom(type))
                {
                    reached = true;
                }
                else if (instruction.operand is MethodBase target)
                {
                    reached = target == game
                              || ((target.DeclaringType == typeof(Find) || target.DeclaringType == typeof(Current))
                                  && Reaches(target));
                }

                if (reached)
                {
                    break;
                }
            }

            memo[method] = reached;
            return reached;
        }

        HashSet<string> members = typeof(Find)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(Reaches)
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (stats.MethodsFailed > 0 || !members.Contains("get_World"))
        {
            throw new InvalidOperationException(
                "契约闸无法推出 Find 上要已载档游戏的成员（读取失败 " + stats.MethodsFailed + " 个；Find.World 在集内="
                + members.Contains("get_World") + "）" + string.Concat(stats.Failures.Select(failure => "\n  " + failure)));
        }

        return members;
    }
}
