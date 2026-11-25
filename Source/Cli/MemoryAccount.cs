using System.Diagnostics;

namespace RimRunnerCli;

public static class MemoryAccount
{
    public const long DefaultInstanceMb = 4_000;
    public const long DefaultHeadroomMb = 2_000;

    public sealed record Resident(int Pid, long PrivateBytes, string? CommandLine);

    public sealed record Snapshot(long Total, long Available, IReadOnlyList<Resident> Residents, string? ResidentsFault = null)
    {
        public long PrivateOf(int pid) => Residents.FirstOrDefault(r => r.Pid == pid)?.PrivateBytes ?? 0;
    }

    public static long InstanceDefault() => RimRunnerConfig.Bytes(RimRunnerConfig.Machine().InstanceMemMb.Value);

    public static long Headroom() => RimRunnerConfig.Bytes(RimRunnerConfig.Machine().MemHeadroomMb.Value);

    public static string Key(string product, string? modList, string purpose, string tier, string game) =>
        $"{product}|{modList ?? "(无名单)"}|{purpose}|{tier}|{game}";

    internal static Snapshot? Read()
    {
        if (PhysicalMemory.Read() is not { } status) return null;
        List<GameProcessProbe.GameProcess> processes;
        string? fault = null;
        try
        {
            processes = GameProcessProbe.Enumerate();
        }
        catch (StoreException exception)
        {
            processes = [];
            fault = exception.Message;
        }

        return new Snapshot(status.Total, status.Available,
            [.. processes.Select(p => new Resident(p.Pid, PrivateBytes(p.Pid), p.CommandLine))], fault);
    }

    private static long PrivateBytes(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return process.PrivateMemorySize64;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
                                              or System.ComponentModel.Win32Exception)
        {
            return 0;
        }
    }
}
