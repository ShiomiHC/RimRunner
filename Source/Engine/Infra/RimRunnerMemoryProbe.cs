using System.Diagnostics;

namespace RimRunner;

internal static class RimRunnerMemoryProbe
{
    internal static SortedDictionary<string, long> Snapshot()
    {
        var values = new SortedDictionary<string, long>(StringComparer.Ordinal)
        {
            [RimRunnerWireKeys.Report.MemoryPrivateBytes] = Read(PrivateBytes),
            [RimRunnerWireKeys.Report.MemoryManagedCommitted] = Read(() => GC.GetGCMemoryInfo().TotalCommittedBytes),
            [RimRunnerWireKeys.Report.MemoryManagedUsed] = Read(() => GC.GetTotalMemory(false)),
        };
        return values;
    }

    private static long PrivateBytes()
    {
        using Process process = Process.GetCurrentProcess();
        return process.PrivateMemorySize64;
    }

    private static long Read(Func<long> read)
    {
        try
        {
            long value = read();
            return value > 0 ? value : -1;
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException
                                              or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
        {
            return -1;
        }
    }
}
