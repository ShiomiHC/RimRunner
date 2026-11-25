namespace RimRunner;

public static class RimRunnerVerdict
{
    public static bool IsAttention(IReadOnlyList<string?> verdictStatuses, IReadOnlyList<string?> diagnosticStatuses,
        bool batchFaults = false)
    {
        if (batchFaults)
        {
            return true;
        }

        if (verdictStatuses.Count + diagnosticStatuses.Count == 0)
        {
            return true;
        }

        for (int i = 0; i < verdictStatuses.Count; i++)
        {
            if (IsVerdictAttention(verdictStatuses[i]))
            {
                return true;
            }
        }

        for (int i = 0; i < diagnosticStatuses.Count; i++)
        {
            if (IsDiagnosticAttention(diagnosticStatuses[i]))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsVerdictAttention(string? status) =>
        status is RimRunnerOutcomes.Fail
            or RimRunnerOutcomes.NoAssertions
            or RimRunnerOutcomes.UnclaimedFault
            or RimRunnerOutcomes.SentinelFault
            or RimRunnerOutcomes.EngineFault;

    public static bool IsDiagnosticAttention(string? status) =>
        status is RimRunnerOutcomes.Incomplete
            or RimRunnerOutcomes.UnclaimedFault
            or RimRunnerOutcomes.SentinelFault
            or RimRunnerOutcomes.EngineFault;
}
