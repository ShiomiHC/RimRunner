namespace RimRunner;

internal static class RimRunnerSaveProbe
{
    private static readonly SortedDictionary<string, SortedSet<string>> fills = new(StringComparer.Ordinal);

    internal static bool Armed { get; private set; }

    internal static void Arm()
    {
        fills.Clear();
        Armed = true;
    }

    internal static void Disarm()
    {
        Armed = false;
        fills.Clear();
    }

    internal static void NoteFirstFill(string componentType, IEnumerable<string> elements)
    {
        if (!fills.TryGetValue(componentType, out SortedSet<string>? known))
        {
            known = new SortedSet<string>(StringComparer.Ordinal);
            fills[componentType] = known;
        }

        known.UnionWith(elements);
    }

    internal static SortedDictionary<string, string> Fills()
    {
        SortedDictionary<string, string> result = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, SortedSet<string>> pair in fills)
        {
            result[pair.Key] = string.Join(",", pair.Value);
        }

        return result;
    }
}
