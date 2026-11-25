using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RimRunnerCli;

public static class CpuHeadroom
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    public static Func<double?>? NewProbe()
    {
        Probe? probe = Probe.Create(out _);
        return probe == null ? null : probe.Idle;
    }

    public static string? ProbeFailure() => Probe.Create(out string? failure) == null ? failure : null;

    private sealed class Probe
    {
        private readonly int[][] cores;
        private readonly int groups;
        private readonly int[] offsets;
        private readonly List<(TimeSpan At, Times[] Times)> snapshots = [];
        private readonly Stopwatch clock = Stopwatch.StartNew();

        private Probe(int[][] cores, int groups, int[] offsets, Times[] first)
        {
            this.cores = cores;
            this.groups = groups;
            this.offsets = offsets;
            snapshots.Add((clock.Elapsed, first));
        }

        public static Probe? Create(out string? failure)
        {
            List<BusyCap.CpuSet>? sets = BusyCap.PerformanceSets();
            if (sets == null)
            {
                failure = "无法读取 P 核的 CPU 集";
                return null;
            }

            int groups;
            try
            {
                groups = GetActiveProcessorGroupCount();
            }
            catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException)
            {
                failure = "无法读取处理器组";
                return null;
            }

            if (groups <= 0)
            {
                failure = "无法读取处理器组";
                return null;
            }

            int[] offsets = new int[groups + 1];
            int total = 0;
            try
            {
                for (int group = 0; group < groups; group++)
                {
                    uint count = GetActiveProcessorCount((ushort)group);
                    if (count == 0)
                    {
                        failure = $"处理器组 {group} 没有活动处理器";
                        return null;
                    }

                    offsets[group] = total;
                    total += (int)count;
                }
            }
            catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException)
            {
                failure = "无法读取处理器组";
                return null;
            }

            offsets[groups] = total;
            if (sets.Any(set => set.Group < 0 || set.Group >= groups))
            {
                failure = "CPU 集与处理器组不匹配";
                return null;
            }

            int[][] cores =
            [
                .. sets.GroupBy(set => (set.Group, set.Core))
                    .Select(core => core.Select(set => offsets[set.Group] + set.Logical).ToArray()),
            ];
            Times[]? first = Read(groups, offsets);
            failure = first == null ? "无法读取系统处理器计数"
                : cores.Any(core => core.Any(index => index >= first.Length)) ? "CPU 集与系统处理器计数不匹配"
                : null;
            return failure == null ? new Probe(cores, groups, offsets, first!) : null;
        }

        public double? Idle()
        {
            Times[]? now = Read(groups, offsets);
            if (now == null) return null;
            TimeSpan at = clock.Elapsed;
            snapshots.Add((at, now));
            int baseIndex = snapshots.FindLastIndex(s => at - s.At >= Window);
            if (baseIndex < 0) return null;
            snapshots.RemoveRange(0, baseIndex);
            return Free(cores, snapshots[0].Times, now);
        }
    }

    internal readonly record struct Times(long Idle, long Total);

    internal static double Free(int[][] cores, Times[] before, Times[] after)
    {
        double free = 0;
        foreach (int[] core in cores)
        {
            double busiest = 0;
            foreach (int index in core)
            {
                long total = after[index].Total - before[index].Total;
                if (total <= 0) continue;
                double busy = 1 - (double)(after[index].Idle - before[index].Idle) / total;
                busiest = Math.Max(busiest, Math.Clamp(busy, 0, 1));
            }

            free += 1 - busiest;
        }

        return free;
    }

    private static Times[]? Read(int groups, int[] offsets)
    {
        const int SystemProcessorPerformanceInformation = 8;
        const int Size = 48;
        try
        {
            var all = new Times[offsets[groups]];
            for (int group = 0; group < groups; group++)
            {
                int count = offsets[group + 1] - offsets[group];
                IntPtr buffer = Marshal.AllocHGlobal(Size * count);
                try
                {
                    ushort number = (ushort)group;
                    int status = NtQuerySystemInformationEx(SystemProcessorPerformanceInformation, ref number,
                        sizeof(ushort), buffer, Size * count, out int length);
                    if (status != 0 || length != Size * count) return null;
                    for (int i = 0; i < count; i++)
                    {
                        long idle = Marshal.ReadInt64(buffer, i * Size);
                        long kernel = Marshal.ReadInt64(buffer, i * Size + 8);
                        long user = Marshal.ReadInt64(buffer, i * Size + 16);
                        all[offsets[group] + i] = new Times(idle, kernel + user);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            return all;
        }
        catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException)
        {
            return groups == 1 ? ReadSingleGroup(offsets[1]) : null;
        }
    }

    private static Times[]? ReadSingleGroup(int count)
    {
        const int SystemProcessorPerformanceInformation = 8;
        const int Size = 48;
        const int Max = 64;
        IntPtr buffer = Marshal.AllocHGlobal(Size * Max);
        try
        {
            if (NtQuerySystemInformation(SystemProcessorPerformanceInformation, buffer, Size * Max, out int length) != 0)
                return null;
            int returned = length / Size;
            if (returned < count || returned > Max) return null;
            var times = new Times[count];
            for (int i = 0; i < count; i++)
            {
                long idle = Marshal.ReadInt64(buffer, i * Size);
                long kernel = Marshal.ReadInt64(buffer, i * Size + 8);
                long user = Marshal.ReadInt64(buffer, i * Size + 16);
                times[i] = new Times(idle, kernel + user);
            }

            return times;
        }
        catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern ushort GetActiveProcessorGroupCount();

    [DllImport("kernel32.dll")]
    private static extern uint GetActiveProcessorCount(ushort groupNumber);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformationEx(int informationClass, ref ushort input, int inputLength,
        IntPtr information, int length, out int returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int informationClass, IntPtr information, int length,
        out int returnLength);
}
