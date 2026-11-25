using System.Diagnostics;
using System.Globalization;

namespace RimRunnerCli;

internal sealed class LoadTrace(Process? process, string logPath, InstanceLink? link = null)
{
    internal readonly record struct Sample(DateTime At, long? LogBytes, double? UserSeconds, double? KernelSeconds,
        long? EngineLogEvents = null);

    internal const string Tag = "[载档轨迹]";
    private const int TailLines = 10;
    private const double CpuWindowSeconds = 60;

    private readonly List<Sample> samples = [];

    internal IReadOnlyList<Sample> Samples => samples;

    public void Take() => Add(new Sample(DateTime.UtcNow, LogBytes(), Cpu(p => p.UserProcessorTime),
        Cpu(p => p.PrivilegedProcessorTime), link is { Connected: true } ? link.LogEvents : null));

    internal void Add(Sample sample) => samples.Add(sample);

    private long? LogBytes()
    {
        try
        {
            var info = new FileInfo(logPath);
            return info.Exists ? info.Length : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private double? Cpu(Func<Process, TimeSpan> read)
    {
        if (process == null) return null;
        try
        {
            return read(process).TotalSeconds;
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException
                                              or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    internal (double Seconds, double StartsAt)? LongestStall()
    {
        if (samples.Count < 2) return null;
        double longest = 0, startsAt = 0;
        int runStart = 0;
        for (int index = 1; index <= samples.Count; index++)
        {
            if (index < samples.Count && samples[index].LogBytes == samples[index - 1].LogBytes) continue;
            double length = (samples[index - 1].At - samples[runStart].At).TotalSeconds;
            if (length > longest)
            {
                longest = length;
                startsAt = (samples[runStart].At - samples[0].At).TotalSeconds;
            }

            runStart = index;
        }

        return (longest, startsAt);
    }

    internal (double Window, double User, double Kernel)? CpuTail(double windowSeconds = CpuWindowSeconds)
    {
        if (samples.Count < 2) return null;
        Sample last = samples[^1];
        Sample first = samples.LastOrDefault(s => (last.At - s.At).TotalSeconds >= windowSeconds,
            samples[0]);
        double wall = (last.At - first.At).TotalSeconds;
        if (wall <= 0 || last.UserSeconds is not double u1 || first.UserSeconds is not double u0
            || last.KernelSeconds is not double k1 || first.KernelSeconds is not double k0)
            return null;
        return (wall, (u1 - u0) / wall, (k1 - k0) / wall);
    }

    public IReadOnlyList<string> TimeoutLines()
    {
        List<string> lines = [];
        if (samples.Count == 0) return lines;
        double span = (samples[^1].At - samples[0].At).TotalSeconds;
        string log = samples[^1].LogBytes is long bytes ? $"Player.log {bytes} 字节" : "Player.log 不存在";
        string stall = LongestStall() is var (seconds, startsAt)
            ? $"；最长不变 {F(seconds)}s（第一拍后 {F(startsAt)}s 起）"
            : "";
        lines.Add($"{Tag} {samples.Count} 拍，{F(span)}s：{log}，{Growth(s => s.LogBytes)}{stall}");
        lines.Add(samples[^1].EngineLogEvents is long engineEvents
            ? $"{Tag} 引擎日志 {engineEvents} 条，{Growth(s => s.EngineLogEvents)}"
            : $"{Tag} 实例管道未连接");
        lines.Add(CpuTail() is var (window, user, kernel)
            ? $"{Tag} 末 {F(window)}s 进程 CPU：用户态 {user:0.00} 核 · 内核态 {kernel:0.00} 核"
            : $"{Tag} 进程 CPU：无法读取");
        lines.AddRange(Tail().Select(line => $"{Tag}   {line}"));
        return lines;
    }

    private string Growth(Func<Sample, long?> bytes)
    {
        int lastChange = Enumerable.Range(1, samples.Count - 1)
            .LastOrDefault(i => bytes(samples[i]) != bytes(samples[i - 1]), -1);
        return lastChange < 0
            ? "全程未变"
            : $"最后一次变化距末拍 {F((samples[^1].At - samples[lastChange].At).TotalSeconds)}s";
    }

    public object Summary()
    {
        var stall = LongestStall();
        var cpu = CpuTail(double.MaxValue);
        return new
        {
            samples = samples.Count,
            loadSeconds = samples.Count == 0 ? (double?)null : Round((samples[^1].At - samples[0].At).TotalSeconds),
            logBytes = samples.Count == 0 ? null : samples[^1].LogBytes,
            engineLogEvents = samples.Count == 0 ? null : samples[^1].EngineLogEvents,
            longestStallSeconds = stall is var (s, _) ? Round(s) : (double?)null,
            longestStallStartsAt = stall is var (_, a) ? Round(a) : (double?)null,
            userCores = cpu is var (_, u, _) ? Round(u) : (double?)null,
            kernelCores = cpu is var (_, _, k) ? Round(k) : (double?)null,
        };
    }

    private IEnumerable<string> Tail()
    {
        string full = CommandContext.FullPath(logPath);
        if (RimTooling.IO.PathPresence.Of(full) != RimTooling.IO.PathPresenceKind.Present)
        {
            return [];
        }

        IReadOnlyList<string> lines = RimTooling.RimWorld.GameLauncher.ReadLogTail(
            full,
            TailLines,
            skipWaylithStackFrames: true);
        return lines.Count == 0
            ? []
            : [$"Player.log 末 {lines.Count} 行（不计栈帧）：", .. lines];
    }

    private static double Round(double value) => Math.Round(value, 2);

    private static string F(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);
}
