namespace RimRunner;

internal static class RimRunnerForcedMainMap
{
    internal static readonly bool Enabled = RimRunnerExperiments.IsOn(RimRunnerExperiments.ForceMainMap);

    internal static bool Covers(RimRunnerTestCase testCase) =>
        Enabled && testCase.DeclaredMapSite.IsDefault && testCase.DefaultsToIsolatedMap;
}

internal static class RimRunnerMapSiteLedger
{
    private static readonly List<string> mainMap = [];
    private static readonly List<string> forced = [];
    private static readonly HashSet<string> recorded = new(StringComparer.Ordinal);

    internal static void ResetRun()
    {
        mainMap.Clear();
        forced.Clear();
        recorded.Clear();
    }

    internal static void Record(RimRunnerTestCase testCase)
    {
        if (testCase.Level != RimRunnerLevel.World || !recorded.Add(testCase.Id))
        {
            return;
        }

        string? reason = testCase.MainMapReason;
        if (reason != null)
        {
            mainMap.Add(testCase.Id + " " + reason);
        }

        if (RimRunnerForcedMainMap.Covers(testCase))
        {
            forced.Add(testCase.Id);
        }
    }

    internal static IReadOnlyList<string> MainMapLines() => mainMap;

    internal static IReadOnlyList<string> ForcedLines() => forced;
}
