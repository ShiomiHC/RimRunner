using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace RimRunnerCli;

public static class GpuMemory
{
    public sealed record Snapshot(string AdapterName, long Total, long AdapterUsed,
        IReadOnlyDictionary<int, long> ProcessUsed)
    {
        public IReadOnlyDictionary<int, long> ProcessShared { get; init; } = new Dictionary<int, long>();

        public IReadOnlyDictionary<int, long> EngineDemand { get; init; } = new Dictionary<int, long>();

        public long Shared(int pid) => ProcessShared.GetValueOrDefault(pid);

        public long Demand(int pid) => EngineDemand.TryGetValue(pid, out long demand)
            ? demand
            : ProcessUsed.GetValueOrDefault(pid) + Shared(pid);

        public Dictionary<int, long> Demands() =>
            ProcessUsed.Keys.Concat(ProcessShared.Keys).Concat(EngineDemand.Keys).Distinct()
                .ToDictionary(pid => pid, Demand);
    }

    public static Snapshot? TryRead(out string? reason)
    {
        reason = null;
        Dxgi.Adapter? adapter;
        try
        {
            adapter = Dxgi.PrimaryAdapter();
        }
        catch (Exception exception) when (exception is COMException or DllNotFoundException
                                              or EntryPointNotFoundException or InvalidCastException)
        {
            reason = $"DXGI 枚举适配器失败：{exception.Message}";
            return null;
        }

        if (adapter == null || adapter.DedicatedVideoMemory <= 0)
        {
            reason = "DXGI 没有带专用显存的 0 号适配器";
            return null;
        }

        string luid = $"luid_0x{adapter.LuidHigh:x8}_0x{adapter.LuidLow:x8}_";
        Dictionary<string, long>? adapters = Pdh.ReadWildcard(@"\GPU Adapter Memory(*)\Dedicated Usage", out string? error);
        if (adapters == null)
        {
            reason = $"无法读取 GPU Adapter Memory 计数器：{error}";
            return null;
        }

        long[] mine =
        [
            .. adapters.Where(pair => pair.Key.StartsWith(luid, StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Value)
        ];
        if (mine.Length == 0)
        {
            reason = $"计数器里没有 {adapter.Name} 的实例（{luid}*）";
            return null;
        }

        Dictionary<string, long>? processes = Pdh.ReadWildcard(@"\GPU Process Memory(*)\Dedicated Usage", out error);
        if (processes == null)
        {
            reason = $"无法读取 GPU Process Memory 计数器：{error}";
            return null;
        }

        Dictionary<string, long>? shared = Pdh.ReadWildcard(@"\GPU Process Memory(*)\Shared Usage", out _);
        return new Snapshot(adapter.Name, adapter.DedicatedVideoMemory, mine.Sum(), ProcessUsage(processes, luid))
        {
            ProcessShared = shared == null ? [] : ProcessUsage(shared, luid),
        };
    }

    internal static Dictionary<int, long> ProcessUsage(IReadOnlyDictionary<string, long> raw, string luidPrefix)
    {
        var result = new Dictionary<int, long>();
        foreach ((string name, long value) in raw)
        {
            Match match = Regex.Match(name, @"^pid_(\d+)_(luid_.*)$", RegexOptions.IgnoreCase);
            if (!match.Success || !match.Groups[2].Value.StartsWith(luidPrefix, StringComparison.OrdinalIgnoreCase))
                continue;
            int pid = int.Parse(match.Groups[1].Value);
            result[pid] = result.GetValueOrDefault(pid) + value;
        }

        return result;
    }

    private static class Pdh
    {
        private const uint FmtLarge = 0x00000400;
        private const uint MoreData = 0x800007D2;

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

        [DllImport("pdh.dll")]
        private static extern uint PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format,
            ref uint bufferSize, out uint itemCount, IntPtr buffer);

        [DllImport("pdh.dll")]
        private static extern uint PdhCloseQuery(IntPtr query);

        private const int ItemSize = 24;

        public static Dictionary<string, long>? ReadWildcard(string path, out string? error)
        {
            error = null;
            uint status = PdhOpenQueryW(null, IntPtr.Zero, out IntPtr query);
            if (status != 0)
            {
                error = $"PdhOpenQuery 0x{status:X8}";
                return null;
            }

            try
            {
                status = PdhAddEnglishCounterW(query, path, IntPtr.Zero, out IntPtr counter);
                if (status == 0) status = PdhCollectQueryData(query);
                if (status != 0)
                {
                    error = $"0x{status:X8}";
                    return null;
                }

                uint size = 0;
                status = PdhGetFormattedCounterArrayW(counter, FmtLarge, ref size, out uint count, IntPtr.Zero);
                if (status != MoreData)
                {
                    if (status == 0) return [];
                    error = $"0x{status:X8}";
                    return null;
                }

                IntPtr buffer = Marshal.AllocHGlobal((int)size);
                try
                {
                    status = PdhGetFormattedCounterArrayW(counter, FmtLarge, ref size, out count, buffer);
                    if (status != 0)
                    {
                        error = $"0x{status:X8}";
                        return null;
                    }

                    var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr item = buffer + i * ItemSize;
                        string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "";
                        if (Marshal.ReadInt32(item, 8) is not (0 or 1)) continue;
                        result[name] = result.GetValueOrDefault(name) + Marshal.ReadInt64(item, 16);
                    }

                    return result;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                PdhCloseQuery(query);
            }
        }
    }

    private static class Dxgi
    {
        public sealed record Adapter(string Name, long DedicatedVideoMemory, uint LuidLow, int LuidHigh);

        [DllImport("dxgi.dll")]
        private static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IDXGIFactory1 factory);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct AdapterDesc1
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
            public uint VendorId, DeviceId, SubSysId, Revision;
            public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
            public uint LuidLow;
            public int LuidHigh;
            public uint Flags;
        }

        [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIFactory1
        {
            void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
            void EnumAdapters(); void MakeWindowAssociation(); void GetWindowAssociation();
            void CreateSwapChain(); void CreateSoftwareAdapter();
            [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
            [PreserveSig] int IsCurrent();
        }

        [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIAdapter1
        {
            void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
            void EnumOutputs(); void GetDesc(); void CheckInterfaceSupport();
            [PreserveSig] int GetDesc1(out AdapterDesc1 desc);
        }

        public static Adapter? PrimaryAdapter()
        {
            Guid iid = typeof(IDXGIFactory1).GUID;
            Marshal.ThrowExceptionForHR(CreateDXGIFactory1(ref iid, out IDXGIFactory1 factory));
            try
            {
                if (factory.EnumAdapters1(0, out IDXGIAdapter1 adapter) != 0) return null;
                try
                {
                    Marshal.ThrowExceptionForHR(adapter.GetDesc1(out AdapterDesc1 desc));
                    return new Adapter(desc.Description, (long)desc.DedicatedVideoMemory, desc.LuidLow, desc.LuidHigh);
                }
                finally
                {
                    Marshal.ReleaseComObject(adapter);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
        }
    }
}
