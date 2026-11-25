using System.Globalization;

namespace RimRunner;

internal static class RimRunnerCheckpointPlanner
{
    internal const long UnknownTicks = long.MaxValue / 1024;

    internal sealed class Member
    {
        internal required string Id;
        internal required string TypeName;
        internal int Budget;
        internal required IReadOnlyCollection<string> Resources;
        internal bool Tail;
        internal bool Solo;
        internal int[]? PhaseTicks;
        internal int ClockBudget = int.MaxValue;
        internal bool Isolated;

        internal int Effective => Isolated ? ClockBudget : Math.Min(Budget, ClockBudget);

        internal long Cost => PhaseTicks == null
            ? UnknownTicks
            : PhaseTicks.Sum(ticks => (long)ticks) + (Isolated ? PhaseTicks.Length - 1 : 0);
    }

    internal static List<List<Member>> Plan(IEnumerable<Member> members)
    {
        List<List<Member>> batches = [];
        foreach (Member member in members
                     .OrderBy(item => item.Effective)
                     .ThenBy(item => item.TypeName, StringComparer.Ordinal)
                     .ThenBy(item => item.Id, StringComparer.Ordinal))
        {
            bool placed = false;
            for (int index = 0; index < batches.Count && !placed; index++)
            {
                if (member.Tail && batches[index].Any(item => item.Tail))
                {
                    continue;
                }

                if (member.Solo || batches[index].Any(item => item.Solo))
                {
                    continue;
                }

                List<Member> candidate = Arrange(batches[index].Append(member));
                if (Fits(candidate))
                {
                    batches[index] = candidate;
                    placed = true;
                }
            }

            if (!placed)
            {
                batches.Add([member]);
            }
        }

        return batches;
    }

    private static List<Member> Arrange(IEnumerable<Member> members)
    {
        return members
            .OrderBy(item => item.Tail ? 1 : 0)
            .ThenByDescending(item => item.Effective)
            .ThenBy(item => item.TypeName, StringComparer.Ordinal)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static bool Fits(List<Member> batch)
    {
        HashSet<string> held = new(StringComparer.Ordinal);
        foreach (Member member in batch)
        {
            foreach (string resource in member.Resources)
            {
                if (!held.Add(resource))
                {
                    return false;
                }
            }
        }

        long after = 0;
        for (int index = batch.Count - 1; index >= 0; index--)
        {
            if (batch[index].Effective != int.MaxValue && after + LoadTicks(batch[index]) > batch[index].Effective)
            {
                return false;
            }

            after = Math.Min(UnknownTicks, after + batch[index].Cost);
        }

        return true;
    }

    internal static int LoadTicks(Member member) => member.Isolated ? 2 : 1;

    internal static Member Describe(RimRunnerTestCase testCase, IReadOnlyList<string>? ledgerRing)
    {
        IRimRunnerPhasedCase phased = (IRimRunnerPhasedCase)testCase;
        return new Member
        {
            Id = testCase.Id,
            TypeName = testCase.GetType().FullName!,
            Budget = DeclaredTickBudget(testCase) ?? int.MaxValue,
            Resources = phased.CheckpointResources ?? [],
            Tail = phased.RequiresBatchTail,
            Solo = phased.RequiresSoloBatch,
            ClockBudget = DeclaredClockBudget(testCase) ?? int.MaxValue,
            Isolated = testCase.OnIsolatedMap,
            PhaseTicks = DeclaredTicks(testCase) ?? EstimateOfRing(ledgerRing, phased.PhaseCount)
        };
    }

    internal static int? DeclaredTickBudget(RimRunnerTestCase testCase)
    {
        return (testCase as IRimRunnerPhasedCase)?.CheckpointTickBudget;
    }

    internal static int EffectiveBudget(RimRunnerTestCase testCase)
    {
        int clockBudget = DeclaredClockBudget(testCase) ?? int.MaxValue;
        return testCase.OnIsolatedMap
            ? clockBudget
            : Math.Min(DeclaredTickBudget(testCase) ?? int.MaxValue, clockBudget);
    }

    internal static int? DeclaredClockBudget(RimRunnerTestCase testCase)
    {
        return (testCase as IRimRunnerPhasedCase)?.CheckpointClockBudget;
    }

    internal static int[]? DeclaredTicks(RimRunnerTestCase testCase)
    {
        return (testCase as IRimRunnerPhasedCase)?.CheckpointTicks;
    }

    internal static int[]? EstimateOfRing(IReadOnlyList<string>? ring, int phaseCount)
    {
        int[]? max = null;
        int[]? min = null;
        foreach (string cell in ring ?? [])
        {
            int[]? parsed = ParseCell(cell, phaseCount);
            if (parsed == null)
            {
                continue;
            }

            if (max == null || min == null)
            {
                max = (int[])parsed.Clone();
                min = (int[])parsed.Clone();
                continue;
            }

            for (int phase = 0; phase < phaseCount; phase++)
            {
                max[phase] = Math.Max(max[phase], parsed[phase]);
                min[phase] = Math.Min(min[phase], parsed[phase]);
            }
        }

        if (max == null || min == null)
        {
            return null;
        }

        for (int phase = 0; phase < phaseCount; phase++)
        {
            max[phase] += max[phase] - min[phase];
        }

        return max;
    }

    internal static string CellOf(IReadOnlyList<int> phaseTicks)
    {
        return string.Join(",", phaseTicks.Select(ticks => ticks.ToString(CultureInfo.InvariantCulture)));
    }

    private static int[]? ParseCell(string cell, int phaseCount)
    {
        string[] parts = (cell ?? "").Split(',');
        if (parts.Length != phaseCount)
        {
            return null;
        }

        int[] parsed = new int[phaseCount];
        for (int phase = 0; phase < phaseCount; phase++)
        {
            if (!int.TryParse(parts[phase], NumberStyles.None, CultureInfo.InvariantCulture, out parsed[phase]))
            {
                return null;
            }
        }

        return parsed;
    }

    internal static List<string> DeclarationFaults(string id, IRimRunnerPhasedCase? phased,
        bool sitedOnIsolatedMap = false)
    {
        List<string> faults = [];
        if (phased == null)
        {
            return faults;
        }

        int[]? declared = phased.CheckpointTicks;
        int? clockBudget = phased.CheckpointClockBudget;
        int? tickBudget = phased.CheckpointTickBudget;

        if (clockBudget < 1)
        {
            faults.Add("用例 " + id + " 的 CheckpointClockBudget = " + clockBudget
                       + "：至少为 1（读档收尾 vanilla 恒补推 1 tick）");
        }
        else if (sitedOnIsolatedMap && clockBudget < 2)
        {
            faults.Add("用例 " + id + " 的 CheckpointClockBudget = " + clockBudget
                       + "：在独立小图上至少为 2（读档补推 1 tick，领回停放的图时再替该图补推 1 tick）");
        }

        if (tickBudget < 1)
        {
            faults.Add("用例 " + id + " 的 CheckpointTickBudget = " + tickBudget
                       + "：至少为 1（读档收尾 vanilla 恒补推 1 tick）");
        }

        if (phased.CheckpointResources == null)
        {
            faults.Add("用例 " + id + " 的 CheckpointResources 为 null");
        }

        if (declared != null
            && (declared.Length != phased.PhaseCount || declared.Any(ticks => ticks < 0)))
        {
            faults.Add("用例 " + id + " 的 CheckpointTicks = [" + string.Join(", ", declared)
                       + "] 与 PhaseCount=" + phased.PhaseCount + " 不匹配或含负数");
        }

        return faults;
    }

    internal static List<string> InertDeclarations(string id, IRimRunnerPhasedCase? phased,
        bool sitedOnIsolatedMap = false)
    {
        List<string> inert = [];
        if (phased == null)
        {
            return inert;
        }

        if (phased.CheckpointResources?.Any(string.IsNullOrWhiteSpace) == true)
        {
            inert.Add("用例 " + id + " 的 CheckpointResources 含空串或空白：空键不与任何资源键相交，等于未声明");
        }

        if (phased.RequiresSoloBatch)
        {
            List<string> idle = [];
            if (phased.RequiresBatchTail)
            {
                idle.Add("RequiresBatchTail");
            }

            if (phased.CheckpointResources?.Any(resource => !string.IsNullOrWhiteSpace(resource)) == true)
            {
                idle.Add("CheckpointResources");
            }

            if (phased.CheckpointTickBudget != null && !sitedOnIsolatedMap)
            {
                idle.Add("CheckpointTickBudget");
            }

            if (phased.CheckpointClockBudget != null)
            {
                idle.Add("CheckpointClockBudget");
            }

            if (idle.Count > 0)
            {
                inert.Add("用例 " + id + " 声明 RequiresSoloBatch，" + string.Join("、", idle)
                          + " 不起作用：独占批里没有别的成员，两 phase 之间只有读档补推的 tick");
            }
        }

        return inert;
    }

    internal static string? MissingTickBudget(string id, IRimRunnerPhasedCase? phased,
        bool sitedOnIsolatedMap, RimRunnerLevel level)
    {
        if (phased == null || phased.RequiresSoloBatch || sitedOnIsolatedMap || level != RimRunnerLevel.World
            || phased.CheckpointTickBudget != null)
        {
            return null;
        }

        return "用例 " + id + " 留在主图上、未声明 CheckpointTickBudget：两 phase 之间同批成员推的 tick 全落在主图上";
    }

    internal static string? DeclarationNote(string id, IRimRunnerPhasedCase? phased, bool sitedOnIsolatedMap)
    {
        int? tickBudget = phased?.CheckpointTickBudget;
        return sitedOnIsolatedMap && tickBudget != null
            ? "用例 " + id + " 的 CheckpointTickBudget = " + tickBudget + " 声明多余：独立小图在两 phase 之间冻结，"
              + "本图只接受领回时补推的 1 拍"
            : null;
    }

    internal static string? MainMapLoadTickHook(string id, bool writesHook, bool sitedOnIsolatedMap)
    {
        return writesHook && !sitedOnIsolatedMap
            ? "用例 " + id + " 留在主图上、写 BeforeLoadTick：主图的读档补推那一拍在 vanilla 载档收尾里就已推，"
              + "只有独立小图上的成员有这个时点"
            : null;
    }

    internal static string? OverBudget(string id, int mapDrift, int budget, int clockDrift, int clockBudget,
        IEnumerable<string> batchMembers)
    {
        string? excess = Excess(id, mapDrift, budget, clockDrift, clockBudget);
        return excess == null
            ? null
            : excess + "：屏障批编排有误，本条结论不可用。同批成员（phase 0 序）：" + string.Join("、", batchMembers);
    }

    internal static string OverBudgetRetried(string id, int mapDrift, int budget, int clockDrift, int clockBudget,
        IEnumerable<string> batchMembers)
    {
        return id + "：" + Excess(id, mapDrift, budget, clockDrift, clockBudget)
               + "，已独占一批重新执行。同批成员（phase 0 序）：" + string.Join("、", batchMembers);
    }

    internal static string BatchRedRetried(string id, string firstRed, int clockDrift, IEnumerable<string> batchMembers)
    {
        return id + "：屏障批中未通过（" + firstRed + "）。本图位未声明预算，已独占一批从 phase 0 重新执行。两 phase 之间时钟差 "
               + clockDrift + " tick；同批成员（phase 0 序）：" + string.Join("、", batchMembers);
    }

    internal static string BatchRedSoloGreen(string id, string firstRed, int batchDrift, int soloDrift, bool onIsolatedMap,
        IEnumerable<string> batchMembers)
    {
        return id + " 在屏障批中未通过，独占一批重新执行后通过，两程结论不匹配，本条结论不可用。两 phase 之间时钟差：批内 " + batchDrift
               + " tick、独占 " + soloDrift + " tick。批内那程的首条失败：" + firstRed
               + "。同批成员（phase 0 序）：" + string.Join("、", batchMembers);
    }

    private static string? Excess(string id, int mapDrift, int budget, int clockDrift, int clockBudget)
    {
        if (budget != int.MaxValue && mapDrift > budget)
        {
            return "两 phase 之间 " + id + " 的图被推进 " + mapDrift + " 拍，超出 CheckpointTickBudget " + budget;
        }

        if (clockBudget != int.MaxValue && clockDrift > clockBudget)
        {
            return "两 phase 之间时钟推进 " + clockDrift + " tick，超出 " + id + " 的 CheckpointClockBudget "
                   + clockBudget;
        }

        return null;
    }

    internal static string? OverDeclared(string id, int phase, int actual, int[]? declared)
    {
        if (declared == null || phase >= declared.Length || actual <= declared[phase])
        {
            return null;
        }

        return id + " 的 phase " + phase + " 实际推进 " + actual + " tick，超出 CheckpointTicks 声明的 "
               + declared[phase] + "。声明失实，据此为同批成员算出的外来 tick 偏少";
    }
}
