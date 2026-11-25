using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RimRunner;

internal static class RimRunnerMachineLoadProbe
{
    private readonly struct Snapshot(long machineBusy100ns, TimeSpan ownCpu, long timestamp)
    {
        internal readonly long MachineBusy100ns = machineBusy100ns;
        internal readonly TimeSpan OwnCpu = ownCpu;
        internal readonly long Timestamp = timestamp;
    }

    private static Snapshot? open;

    internal static void Open(RimRunnerTestCase testCase)
    {
        open = testCase.DeclaresQuietMachine ? Take() : null;
    }

    internal static void Close(RimRunnerResultEntry entry)
    {
        if (open is not { } start)
        {
            return;
        }

        open = null;
        if (Take() is not { } end)
        {
            return;
        }

        double wallMs = (end.Timestamp - start.Timestamp) * 1000.0 / Stopwatch.Frequency;
        double othersMs = (end.MachineBusy100ns - start.MachineBusy100ns) / 10_000.0
                          - (end.OwnCpu - start.OwnCpu).TotalMilliseconds;
        entry.otherBusyCoreMs += Math.Max(0, othersMs);
        entry.otherBusyWallMs += wallMs;
        if (entry.otherBusyWallMs <= 0)
        {
            return;
        }

        entry.otherBusyCores = Math.Round(entry.otherBusyCoreMs / entry.otherBusyWallMs, 2);
        entry.otherBusyWindowMs = (long)entry.otherBusyWallMs;
        RimRunnerEngineLog.Info(
            $"[{entry.id}] 同机负载：本窗 {wallMs / 1000:F1} 秒，本实例以外的忙核数 {Math.Max(0, othersMs) / Math.Max(wallMs, 1):F2}；"
            + $"本条累计 {entry.otherBusyWallMs / 1000:F1} 秒，均值 {entry.otherBusyCores:F2}");
    }

    private static Snapshot? Take()
    {
        if (!GetSystemTimes(out long idle, out long kernel, out long user))
        {
            return null;
        }

        using Process self = Process.GetCurrentProcess();
        return new Snapshot(kernel + user - idle, self.TotalProcessorTime, Stopwatch.GetTimestamp());
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);
}
