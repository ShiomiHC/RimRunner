namespace RimRunnerCli;

public sealed class IdleEviction(ClaimStore store, string requester,
    Func<List<GameProcessProbe.GameProcess>, Action<string>?, string, List<int>> stop)
{
    public const string Memory = "内存";
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(15);

    private readonly ClaimStore store = store;
    private readonly string requester = requester;
    private readonly Func<List<GameProcessProbe.GameProcess>, Action<string>?, string, List<int>> stop = stop;
    private DateTimeOffset lastEviction = DateTimeOffset.MinValue;

    internal sealed record Resident(int Pid, long Bytes, string? CommandLine);

    public sealed record Candidate(int Number, int Pid, long Bytes, double IdleSeconds, string Label);

    internal sealed record Observed(Resident Resident, int? Number, string? HeldBy, InstanceProbe.PingSnapshot? Ping)
    {
        internal bool Held => HeldBy != null;
    }

    internal static string? HolderOf(ColdStartLock.Owners owners) => (owners.Starting, owners.Group) switch
    {
        (not null, not null) => "冷启动锁与分片组",
        (not null, null) => "冷启动锁",
        (null, not null) => "分片组",
        _ => null,
    };

    internal sealed record Snapshot(IReadOnlyList<Observed> Instances)
    {
        public string? StatusOf(int pid) =>
            Instances.FirstOrDefault(o => o.Resident.Pid == pid) is { } observed ? Status(observed) : null;
    }

    internal static bool Eligible(InstanceProbe.PingSnapshot? ping) =>
        ping is { InGame: true, RunState: "idle", Hold: null, IdleSeconds: double idle, IdleLimitMinutes: double minutes }
        && minutes > 0 && idle >= EvictableSeconds(minutes);

    internal static double EvictableSeconds(double idleLimitMinutes) => IdleExit.EvictableMinutes(idleLimitMinutes) * 60;

    private static string IneligibleReason(InstanceProbe.PingSnapshot? ping)
    {
        if (ping == null) return "无应答";
        if (!ping.InGame) return "未进档";
        if (ping.RunState != "idle") return $"runState={ping.RunState ?? "无"}";
        if (ping.Hold is { } hold) return hold.Describe();
        if (ping.IdleLimitMinutes is not double minutes || minutes <= 0) return "不因空闲停止";
        return ping.IdleSeconds is double idle
            ? $"空闲 {(long)idle} 秒，未满可停时限 {(long)EvictableSeconds(minutes)} 秒"
            : "无空闲秒数";
    }

    internal static string? Status(Observed observed)
    {
        if (observed.Number == null) return null;
        if (observed.HeldBy is { } holder) return holder + "持有，不停止";
        InstanceProbe.PingSnapshot? ping = observed.Ping;
        if (ping == null) return "无应答";
        if (!ping.InGame) return "未进档";
        if (ping.RunState != "idle") return $"正在执行（runState={ping.RunState ?? "无"}）";
        if (ping.Hold is { } hold) return hold.Describe() + "，不停止";
        if (ping.IdleSeconds is not double idle) return "空闲";
        string idleText = "空闲 " + ReportPresentation.Elapsed((long)(idle * 1000));
        if (ping.IdleLimitMinutes is not double minutes || minutes <= 0) return idleText + "，不因空闲停止";
        string limit = IdleExit.Minutes(IdleExit.EvictableMinutes(minutes));
        return idle >= EvictableSeconds(minutes)
            ? $"{idleText}，满可停时限 {limit}"
            : $"{idleText}，可停时限 {limit}";
    }

    internal Snapshot Observe(IReadOnlyList<Resident> residents, int myNumber)
    {
        var observed = new List<Observed>();
        foreach (Resident resident in residents)
        {
            int? number = GameProcessProbe.ClaimedNumber(resident.CommandLine, RimRunner.RimRunnerEngineArgs.Instance);
            if (number is not int claimed || claimed == myNumber)
            {
                observed.Add(new Observed(resident, number == myNumber ? null : number, null, null));
                continue;
            }

            string? heldBy = HolderOf(ColdStartLock.OwnersOf(store.Root, claimed));
            InstanceProbe.PingSnapshot? ping = heldBy != null ? null : InstanceProbe.Ping(claimed);
            observed.Add(new Observed(resident, claimed, heldBy, ping));
        }

        return new Snapshot(observed);
    }

    internal static List<Candidate> Select(IEnumerable<Candidate> candidates, long need, long available)
    {
        var picked = new List<Candidate>();
        long freed = 0;
        foreach (Candidate candidate in candidates.OrderByDescending(c => c.IdleSeconds))
        {
            if (available + freed >= need) break;
            if (candidate.Bytes <= 0) continue;
            picked.Add(candidate);
            freed += candidate.Bytes;
        }

        return available + freed >= need ? picked : [];
    }

    internal bool TryMemory(int myNumber, long need, long available, Snapshot snapshot, Action<string>? log) =>
        DateTimeOffset.Now - lastEviction >= Settle
        && Evict(Memory, myNumber, need, available, snapshot, log);

    private bool Evict(string resource, int myNumber, long need, long available, Snapshot snapshot,
        Action<string>? log)
    {
        var candidates = new List<Candidate>();
        foreach (Observed instance in snapshot.Instances)
        {
            if (instance.Number is not int number || number == myNumber)
                continue;
            if (instance.HeldBy is { } holder)
            {
                CliLog.Info($"[{resource}] 实例 {store.Name(number)} 不作候选：{holder}持有该实例");
                continue;
            }

            if (!Eligible(instance.Ping))
            {
                CliLog.Info($"[{resource}] 实例 {store.Name(number)} 不作候选："
                            + IneligibleReason(instance.Ping));
                continue;
            }

            candidates.Add(new Candidate(number, instance.Resident.Pid, instance.Resident.Bytes,
                instance.Ping!.IdleSeconds!.Value, Label(number, instance.Resident.CommandLine)));
        }

        List<Candidate> picked = Select(candidates, need, available);
        if (picked.Count == 0) return false;

        bool stopped = false;
        foreach (Candidate candidate in picked)
        {
            InstanceProbe.PingSnapshot? ping = InstanceProbe.Ping(candidate.Number);
            if (!Eligible(ping) || ping!.IdleSeconds is not double idle || ping.IdleLimitMinutes is not double minutes)
            {
                CliLog.Info($"[{resource}] 实例 {store.Name(candidate.Number)} 不停止：复查时{IneligibleReason(ping)}");
                continue;
            }

            if (HolderOf(ColdStartLock.OwnersOf(store.Root, candidate.Number)) is { } recheckHolder)
            {
                CliLog.Info($"[{resource}] 实例 {store.Name(candidate.Number)} 不停止：复查时{recheckHolder}持有该实例");
                continue;
            }

            List<GameProcessProbe.GameProcess> process =
                [.. GameProcessProbe.Enumerate().Where(p => p.Pid == candidate.Pid)];
            if (process.Count == 0)
            {
                CliLog.Info($"[{resource}] 实例 {store.Name(candidate.Number)} 不停止：PID {candidate.Pid} 已退出");
                continue;
            }

            log?.Invoke($"[{resource}] 停止空闲实例 {candidate.Label} · PID {candidate.Pid}：空闲 "
                        + $"{ReportPresentation.Elapsed((long)(idle * 1000))}，满可停时限 {IdleExit.Minutes(IdleExit.EvictableMinutes(minutes))}，"
                        + $"腾出约 {AdmissionGate.Gb(candidate.Bytes)}");
            if (stop(process, log, StopReason(requester, resource, idle)).Count > 0)
            {
                CliLog.Info($"[{resource}] 实例 {store.Name(candidate.Number)} PID {candidate.Pid} 停止未成功");
                continue;
            }

            stopped = true;
        }

        if (stopped) lastEviction = DateTimeOffset.Now;
        return stopped;
    }

    internal static string StopReason(string requester, string resource, double idleSeconds) =>
        $"被 {requester} 停止以腾出{resource}，当时空闲 {ReportPresentation.Elapsed((long)(idleSeconds * 1000))}";

    private string Label(int number, string? commandLine)
    {
        string modList = GameProcessProbe.ArgValue(commandLine, RimRunner.RimRunnerEngineArgs.ModList) ?? "(无名单)";
        ClaimRecord? claim = null;
        try
        {
            claim = store.Read(number);
        }
        catch (StoreException)
        {
        }

        return claim == null
            ? $"{ClaimStore.GoneName} · {modList}"
            : $"实例 {claim.DisplayName} · {modList}";
    }
}
