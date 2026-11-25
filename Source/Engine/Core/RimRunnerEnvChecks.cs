namespace RimRunner;

internal enum RimRunnerFacility
{
    AgencyPin,
    IsolatedMap,
    OldAgeInjurySkip
}

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
internal sealed class RimRunnerEnvCheckAttribute : Attribute
{
    internal readonly RimRunnerFacility? Facility;

    internal RimRunnerEnvCheckAttribute()
    {
    }

    internal RimRunnerEnvCheckAttribute(RimRunnerFacility facility)
    {
        Facility = facility;
    }
}

internal static class RimRunnerEnvChecks
{
    private static readonly Dictionary<RimRunnerFacility, List<string>> usage = [];

    internal static void ResetForRun()
    {
        usage.Clear();
    }

    internal static void NoteUse(RimRunnerFacility facility)
    {
        string? caseId = RimRunnerSession.CurrentCaseId;
        if (caseId == null)
        {
            return;
        }

        if (!usage.TryGetValue(facility, out List<string>? users))
        {
            users = [];
            usage[facility] = users;
        }

        if (!users.Contains(caseId))
        {
            users.Add(caseId);
        }
    }

    internal static string Describe(RimRunnerFacility facility) => facility switch
    {
        RimRunnerFacility.AgencyPin => "被钉住的 pawn",
        RimRunnerFacility.IsolatedMap => "独立小图",
        RimRunnerFacility.OldAgeInjurySkip => "生成期老年病跳过",
        _ => facility.ToString()
    };

    private static RimRunnerEnvCheckAttribute? AttributeOf(string caseId)
    {
        RimRunnerTestCase? testCase = RimRunnerRegistry.All.FirstOrDefault(
            item => string.Equals(item.Id, caseId, StringComparison.Ordinal));
        return testCase?.GetType().GetCustomAttribute<RimRunnerEnvCheckAttribute>(false);
    }

    internal static bool IsEnvCheck(RimRunnerTestCase testCase) =>
        testCase.GetType().GetCustomAttribute<RimRunnerEnvCheckAttribute>(false) != null;

    internal static bool? Clean(RimRunnerCaseSelector? selector, RimRunnerReport report)
    {
        if (selector == null || selector.EnvChecks == RimRunnerCaseSelector.EnvCheckMode.Off)
        {
            return null;
        }

        List<string> merged = selector.ResolveEnvChecks().Select(testCase => testCase.Id).ToList();
        if (merged.Count == 0)
        {
            return null;
        }

        List<RimRunnerResultEntry> entries = report.regressions.Concat(report.readouts).ToList();
        return merged.All(id => entries.Any(entry =>
            string.Equals(entry.id, id, StringComparison.Ordinal)
            && entry.status == RimRunnerOutcomes.Pass && entry.demotedAsserts == 0));
    }

    internal static List<string> DeclarableIds() =>
        RimRunnerRegistry.All
            .Where(testCase => testCase.GetType().GetCustomAttribute<RimRunnerEnvCheckAttribute>(false) is { Facility: null })
            .Select(testCase => testCase.Id)
            .ToList();

    private static bool FailsOnAssertsOnly(RimRunnerResultEntry entry) =>
        entry.exception == null && entry.cleanupErrors.Count == 0 && entry.newErrors.Count == 0
        && entry.sentinelFaults.Count == 0 && entry.asserts.Any(assertEntry => !assertEntry.pass);

    internal static bool ShouldPark(RimRunnerResultEntry entry) =>
        AttributeOf(entry.id)?.Facility != null
        && FailsOnAssertsOnly(entry);

    internal static void SettleByUsage(RimRunnerResultEntry entry)
    {
        RimRunnerFacility facility = AttributeOf(entry.id)!.Facility!.Value;
        List<string> users = usage.TryGetValue(facility, out List<string>? noted)
            ? noted.Where(id => !string.Equals(id, entry.id, StringComparison.Ordinal)).ToList()
            : [];
        if (users.Count > 0)
        {
            foreach (RimRunnerAssertEntry failed in entry.asserts.Where(assertEntry => !assertEntry.pass))
            {
                failed.explanation = Append(failed.explanation,
                    "本批用到" + Describe(facility) + "的用例：" + string.Join("、", users) + "。这些用例的结论建立在这项设施上");
            }

            return;
        }

        Demote(entry, RimRunnerWarningKey.EnvCheckUnused,
            "本批没有用例用到" + Describe(facility) + "；有用例用到时本条判失败");
    }

    internal static Dictionary<string, string> KnownFailing(IReadOnlyList<RimRunnerRunClaims.Ack> acks)
    {
        List<string> declarable = DeclarableIds();
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        foreach (RimRunnerRunClaims.Ack ack in acks)
        {
            if (declarable.Contains(ack.Target) && !string.IsNullOrWhiteSpace(ack.Reason) && !result.ContainsKey(ack.Target))
            {
                result[ack.Target] = ack.Reason.Trim();
            }
        }

        return result;
    }

    internal static List<RimRunnerFinding> KnownFailingFindings(IReadOnlyList<RimRunnerRunClaims.Ack> acks)
    {
        List<string> declarable = DeclarableIds();
        List<RimRunnerFinding> findings = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (RimRunnerRunClaims.Ack ack in acks)
        {
            if (!declarable.Contains(ack.Target))
            {
                findings.Add(RimRunnerFinding.Warning(null, RimRunnerWarningKey.InertDeclaration,
                    "mod 名单的 [rimrunner].known_failing 指定 " + ack.Target + "，该 Id 不是可由名单许可的环境体检；可许可的："
                    + string.Join("、", declarable)));
                continue;
            }

            if (string.IsNullOrWhiteSpace(ack.Reason))
            {
                findings.Add(RimRunnerFinding.Warning(null, RimRunnerWarningKey.EmptyReason,
                    "mod 名单的 [rimrunner].known_failing 对 " + ack.Target + " 的理由为空，这条许可不生效"));
            }

            if (!seen.Add(ack.Target))
            {
                findings.Add(RimRunnerFinding.Warning(null, RimRunnerWarningKey.EmptyReason,
                    "mod 名单的 [rimrunner].known_failing 对 " + ack.Target + " 写不止一条，只取第一条的理由"));
            }
        }

        return findings;
    }

    internal static void SettleByDeclaration(RimRunnerResultEntry entry, IReadOnlyDictionary<string, string>? declared)
    {
        RimRunnerEnvCheckAttribute? attribute = AttributeOf(entry.id);
        if (attribute == null || attribute.Facility != null)
        {
            return;
        }

        if (declared == null || !declared.TryGetValue(entry.id, out string? reason) || !FailsOnAssertsOnly(entry))
        {
            return;
        }

        Demote(entry, RimRunnerWarningKey.EnvCheckKnownFailing,
            "mod 名单的 [rimrunner].known_failing 许可这条：" + reason);
    }

    private static void Demote(RimRunnerResultEntry entry, string key, string why)
    {
        List<RimRunnerAssertEntry> failed = entry.asserts.Where(assertEntry => !assertEntry.pass).ToList();
        foreach (RimRunnerAssertEntry assertEntry in failed)
        {
            entry.RemoveAssert(assertEntry);
            entry.demotedAsserts++;
            RimRunnerSession.AddRuntimeWarning(entry, key,
                assertEntry.label + "："
                + (assertEntry.expected == "true" ? assertEntry.ActualInFull : "期望 " + assertEntry.expected + "，实际 " + assertEntry.ActualInFull)
                + (string.IsNullOrEmpty(assertEntry.explanation) ? "" : " ← " + assertEntry.explanation)
                + "。" + why);
        }
    }

    private static string Append(string? text, string line) =>
        string.IsNullOrEmpty(text) ? line : text + "；" + line;
}
