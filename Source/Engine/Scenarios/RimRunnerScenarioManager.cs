namespace RimRunner;

public static class RimRunnerScenarioManager
{
    private static List<RimRunnerScenario>? roster;

    internal static int EnsureAllCallCount { get; private set; }

    internal static void ResetForRun()
    {
        EnsureAllCallCount = 0;
    }

    internal static bool Assembling { get; private set; }

    internal static bool InEnsure { get; private set; }

    internal static IDisposable EnsureScopeForSelfTest()
    {
        bool previous = InEnsure;
        InEnsure = true;
        return new RestoreOnDispose(() => InEnsure = previous);
    }

    private sealed class RestoreOnDispose(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    internal static string? LastSweepCaseId { get; private set; }
    internal static string? LastSweepWindowOwner { get; private set; }
    internal static string? LastEnsureWindowOwner { get; private set; }

    internal const string UnregisteredReason =
        "场景子类未登记进名册：整轮不装配，"
        + "依赖该场景子类的用例在其余前置下照常通过";

    internal const string UnreflectedReason =
        "名册项的类型不在测试程序集的场景子类集里：漏登记闸的结论不可信";

    internal static void SweepBeforeCase(string caseId)
    {
        LastSweepCaseId = caseId;
        LastSweepWindowOwner = RimRunnerLogMonitor.ActiveOwner;
        using (RimRunnerLogMonitor.GapSegment("用例 " + caseId + " 开始执行前的环境清扫"))
        {
            Assembling = true;
            try
            {
                RimRunnerEnvironmentSweep.SweepAll(RimRunnerCurrentMapProbe.EngineCurrentMap()!,
                    thing => RimRunnerSession.IsHeldByCheckpointBatch(thing.ThingID));
            }
            catch (Exception exception)
            {
                RimRunnerEngineFault.Raise("环境清扫失败（用例 " + caseId + " 开始执行前）：" + exception);
            }
            finally
            {
                Assembling = false;
            }
        }
    }

    internal static void EnsureAll(RimRunnerContext ctx)
    {
        EnsureAllCallCount++;
        LastEnsureWindowOwner = RimRunnerLogMonitor.ActiveOwner;
        if (EnsureAllCallCount == 1)
        {
            RaiseRosterFaults(LiveRosterFaults());
        }

        Assembling = true;
        InEnsure = true;
        try
        {
            foreach (RimRunnerScenario scenario in Roster().OrderBy(item => item.Stage))
            {
                scenario.Ensure(ctx);
            }
        }
        finally
        {
            InEnsure = false;
            Assembling = false;
        }
    }

    internal static void ResumeAll(RimRunnerContext ctx)
    {
        foreach (RimRunnerScenario scenario in Roster().OrderBy(item => item.Stage))
        {
            scenario.Resume(ctx);
        }
    }

    private static List<RimRunnerScenario> Roster() =>
        roster ??= RimRunnerProduct.Capabilities.ScenarioTypes
            .Select(type => (RimRunnerScenario)Activator.CreateInstance(type)!)
            .ToList();

    internal static void SweepAndEnsureForShowcase(RimRunnerContext ctx)
    {
        Assembling = true;
        try
        {
            RimRunnerEnvironmentSweep.SweepAll(ctx);
        }
        finally
        {
            Assembling = false;
        }

        EnsureAll(ctx);
    }

    internal static List<Type> DiscoverScenarioTypes()
    {
        return RimRunnerDefScope.GaugeTypes()
            .Where(type => !type.IsAbstract
                           && typeof(RimRunnerScenario).IsAssignableFrom(type)
                           && !IsReplica(type))
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToList();
    }

    internal static bool IsReplica(Type type)
    {
        return type.IsNested && typeof(RimRunnerTestCase).IsAssignableFrom(type.DeclaringType);
    }

    internal static List<KeyValuePair<string, string>> LiveRosterFaults()
    {
        return RosterDiff(DiscoverScenarioTypes(), RimRunnerProduct.Capabilities.ScenarioTypes);
    }

    internal static List<KeyValuePair<string, string>> RosterDiff(
        IEnumerable<Type> found, IEnumerable<Type> registered)
    {
        List<Type> foundList = found.ToList();
        List<Type> rosterTypes = registered.ToList();
        List<KeyValuePair<string, string>> faults = [];
        foreach (Type type in foundList)
        {
            if (!rosterTypes.Contains(type))
            {
                faults.Add(new KeyValuePair<string, string>(UnregisteredReason, type.Name));
            }
        }

        foreach (Type type in rosterTypes)
        {
            if (!foundList.Contains(type))
            {
                faults.Add(new KeyValuePair<string, string>(UnreflectedReason, type.Name));
            }
        }

        return faults;
    }

    internal static void RaiseRosterFaults(List<KeyValuePair<string, string>> faults)
    {
        foreach (KeyValuePair<string, string> fault in faults)
        {
            RimRunnerSentinel.Raise(fault.Key, fault.Value);
        }
    }
}
