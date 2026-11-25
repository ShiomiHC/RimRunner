using System.Runtime.InteropServices;

namespace RimRunnerCli;

public static class BusyCap
{
    public static int Resolve() => RimRunnerConfig.Machine().BusyCap.Value;

    public static int Default() => 0;

    public static int PerformanceCores()
    {
        List<CpuSet>? sets = PerformanceSets();
        return sets == null ? Environment.ProcessorCount : sets.Select(set => (set.Group, set.Core)).Distinct().Count();
    }

    internal readonly record struct CpuSet(int Group, byte Logical, byte Core, byte Efficiency);

    internal static List<CpuSet>? PerformanceSets()
    {
        List<CpuSet>? sets = CpuSets();
        if (sets == null || sets.Count == 0) return null;
        byte top = sets.Max(set => set.Efficiency);
        return [.. sets.Where(set => set.Efficiency == top)];
    }

    private static List<CpuSet>? CpuSets()
    {
        try
        {
            GetSystemCpuSetInformation(IntPtr.Zero, 0, out int length, IntPtr.Zero, 0);
            if (length <= 0) return null;
            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (!GetSystemCpuSetInformation(buffer, length, out length, IntPtr.Zero, 0)) return null;
                var sets = new List<CpuSet>();
                for (int offset = 0; offset + 20 <= length;)
                {
                    int size = Marshal.ReadInt32(buffer, offset);
                    if (size <= 0) break;
                    if (Marshal.ReadInt32(buffer, offset + 4) == 0)
                        sets.Add(new CpuSet(Marshal.ReadInt16(buffer, offset + 12), Marshal.ReadByte(buffer, offset + 14),
                            Marshal.ReadByte(buffer, offset + 15), Marshal.ReadByte(buffer, offset + 18)));
                    offset += size;
                }

                return sets;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemCpuSetInformation(IntPtr information, int bufferLength, out int returnedLength,
        IntPtr process, int flags);
}
