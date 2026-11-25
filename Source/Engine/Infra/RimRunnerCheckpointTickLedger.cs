namespace RimRunner;

internal static class RimRunnerCheckpointTickLedger
{
    private const int Cap = 5;
    private const int FlushThreshold = 32;

    private static RimRunnerRingLedger? ledger;

    internal static IReadOnlyList<string> Ring(string caseId)
    {
        return EnsureLoaded().Ring(caseId);
    }

    internal static void Record(string caseId, IReadOnlyList<int> phaseTicks)
    {
        EnsureLoaded().Append(caseId, RimRunnerCheckpointPlanner.CellOf(phaseTicks));
    }

    internal static void Flush()
    {
        ledger?.Flush();
    }

    private static RimRunnerRingLedger EnsureLoaded()
    {
        if (ledger != null)
        {
            return ledger;
        }

        HashSet<string> eligible = new(
            RimRunnerRegistry.All
                .Where(testCase => testCase is IRimRunnerPhasedCase)
                .Select(testCase => testCase.Id),
            StringComparer.Ordinal);
        return ledger = RimRunnerRingLedger.ForHistory(RimRunnerLedgerFold.CheckpointTicksStem, RimRunnerPassRate.ResolveLedgerKey(),
            "推进量台账", Cap, FlushThreshold, eligible.Contains);
    }
}
