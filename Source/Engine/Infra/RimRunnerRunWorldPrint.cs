namespace RimRunner;

internal sealed class RimRunnerRunWorldPrint
{
    private const int ProximityGoodwillInterval = 900000;

    private readonly Func<IReadOnlyCollection<int>, Dictionary<string, string>> capture;
    private readonly IReadOnlyCollection<int> mapIds;
    private readonly Dictionary<string, string> start;
    private readonly int startTick;

    internal RimRunnerRunWorldPrint(Func<IReadOnlyCollection<int>, Dictionary<string, string>>? capture = null,
        IReadOnlyCollection<int>? mapIds = null)
    {
        this.capture = capture ?? RimRunnerWorldPrint.CaptureForRun;
        this.mapIds = mapIds ?? RimRunnerWorldPrint.RunMapIds();
        start = this.capture(this.mapIds);
        startTick = Find.TickManager?.TicksGame ?? 0;
    }

    internal List<WorldNoteEntry> Close()
    {
        Dictionary<string, string> now = capture(mapIds);
        int endTick = Find.TickManager?.TicksGame ?? startTick;
        return now.Select(pair => Entry(pair.Key, start.GetValueOrDefault(pair.Key), pair.Value, startTick, endTick))
            .Where(entry => entry != null)
            .Select(entry => entry!)
            .OrderBy(entry => entry.Text, StringComparer.Ordinal)
            .ToList();
    }

    internal static string? Drift(string key, string? before, string after, int startTick, int endTick)
        => Entry(key, before, after, startTick, endTick)?.Text;

    internal sealed class WorldNoteEntry(string key, string? before, string after)
    {
        internal readonly string Key = key;
        internal readonly string? Before = before;
        internal readonly string After = after;
        internal string Text => Key + "：" + (Before ?? "无") + " → " + After;
    }

    internal static WorldNoteEntry? Entry(string key, string? before, string after, int startTick, int endTick)
    {
        if (before == after)
        {
            return null;
        }

        if (before != null && NaturalDrift(key, before, after, startTick, endTick))
        {
            return null;
        }

        return new WorldNoteEntry(key, before, after);
    }

    private static bool NaturalDrift(string key, string before, string after, int startTick, int endTick)
    {
        if (key.StartsWith(RimRunnerWorldPrint.WorldConditionPrefix, StringComparison.Ordinal)
            || key.Contains(RimRunnerWorldPrint.ConditionInfix, StringComparison.Ordinal))
        {
            return Count(before) > Count(after);
        }

        if (key.StartsWith(RimRunnerWorldPrint.FactionPrefix, StringComparison.Ordinal)
            && endTick / ProximityGoodwillInterval > startTick / ProximityGoodwillInterval)
        {
            int cut = before.LastIndexOf(' ');
            if (cut > 0 && cut == after.LastIndexOf(' ')
                        && string.Equals(before[..cut], after[..cut], StringComparison.Ordinal))
            {
                return int.TryParse(before[(cut + 1)..], out int was)
                       && int.TryParse(after[(cut + 1)..], out int now) && was != now;
            }
        }

        return false;
    }

    private static int Count(string value)
    {
        int cut = value.IndexOf(' ');
        return cut > 0 && int.TryParse(value[..cut], out int count) ? count : 0;
    }
}
