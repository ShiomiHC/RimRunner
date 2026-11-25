namespace RimRunnerCli;

public static class BusyGate
{
    public static string Verdict(int processCount, string? runState, bool forceBusy)
    {
        if (processCount <= 0) return "no-instance";
        if (string.IsNullOrWhiteSpace(runState)) return "stop-unresponsive";
        if (string.Equals(runState, "idle", StringComparison.Ordinal)) return "stop-idle";
        return forceBusy ? "stop-preempted" : "refuse";
    }
}
