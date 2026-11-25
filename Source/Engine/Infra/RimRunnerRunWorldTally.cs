namespace RimRunner;

internal sealed class RimRunnerRunWorldTally
{
    internal enum Status
    {
        Compared,
        Missed,
        Unentered
    }

    private readonly HashSet<string> eligible = new(StringComparer.Ordinal);
    private readonly HashSet<string> compared = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> missed = new(StringComparer.Ordinal);
    private readonly List<string> order = [];

    internal static bool ShouldCompare(RimRunnerTestCase testCase) =>
        RimRunnerTraceTier.On && testCase.Level == RimRunnerLevel.World && testCase is not IRimRunnerRealFrameCase;

    internal void NoteEligible(string id)
    {
        if (eligible.Add(id))
        {
            order.Add(id);
        }
    }

    internal void NoteClosed(RimRunnerMapRestore restore, IEnumerable<string> members, string missedReason)
    {
        if (!restore.ComparesWorld)
        {
            return;
        }

        if (restore.WorldCompared)
        {
            compared.UnionWith(members);
        }
        else
        {
            NoteMissed(members, missedReason);
        }
    }

    internal void NoteMissed(IEnumerable<string> members, string reason)
    {
        foreach (string id in members)
        {
            missed[id] = reason;
        }
    }

    internal int ComparedCount => order.Count(compared.Contains);

    internal Status StatusOf(string id) =>
        compared.Contains(id) ? Status.Compared
        : missed.ContainsKey(id) ? Status.Missed
        : Status.Unentered;

    internal IEnumerable<(bool warn, string text)> Lines()
    {
        yield return (false, "世界层已比对 " + ComparedCount + " 条用例");
        foreach (string id in order.Where(id => !compared.Contains(id)))
        {
            yield return (true, missed.TryGetValue(id, out string? reason)
                ? id + " 世界层未比对：" + reason
                : "[engine] " + id + " 世界层未比对：本条未进入任何建立世界比对的段");
        }
    }
}
