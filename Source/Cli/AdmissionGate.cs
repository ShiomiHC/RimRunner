using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace RimRunnerCli;

public sealed class AdmissionGate(AdmissionLedger ledger, Func<(GpuMemory.Snapshot?, string?)>? read = null,
    Func<MemoryAccount.Snapshot?>? readMemory = null, TimeSpan? grace = null)
{
    public const long DefaultHeadroomMb = 2_000;
    public const long DefaultLoadBudgetMb = 1_000;

    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Reprint = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(60);

    private readonly AdmissionLedger ledger = ledger;
    private readonly TimeSpan grace = grace ?? QueuePolicy.Grace;
    private readonly Func<(GpuMemory.Snapshot? Snapshot, string? Reason)> read = read ?? ReadWithEngine;
    private readonly Func<MemoryAccount.Snapshot?> readMemory = readMemory ?? MemoryAccount.Read;

    public static string Key(string product, string? modList, string purpose, string tier, string game) =>
        $"{product}|{modList ?? "(无名单)"}|{purpose}|{tier}|{game}";

    public static long Headroom() => RimRunnerConfig.Bytes(RimRunnerConfig.Machine().VramHeadroomMb.Value);

    public static long LoadBudget() => RimRunnerConfig.Bytes(RimRunnerConfig.Machine().LoadBudgetMb.Value);

    internal static bool FitsLoadBudget(IReadOnlyList<AdmissionLedger.Ticket> loading, long need, long budget) =>
        loading.Count == 0
        || (need > 0 && loading.All(t => t.Expected > 0) && loading.Sum(t => t.Expected) + need <= budget);

    public (long Bytes, string Source) Estimate(string key, IReadOnlyList<string> activeIds,
        Func<IReadOnlyDictionary<string, string>> modDirs, string? gameVersion, string tier,
        Action<string>? log)
    {
        using (ledger.Lock(LockTimeout))
        {
            AdmissionLedger.History history = ledger.ReadHistory();
            if (history.Unreadable != null)
                log?.Invoke($"[显存] 准入历史无法读取（{history.Unreadable}），本轮按无实测估算");
            if (AdmissionLedger.FromHistory(history, key) is (long bytes, int count))
            {
                CliLog.Info($"[显存] 估算取历史峰值，{count} 次实测：{Gb(bytes)}");
                return (bytes, $"历史峰值，{count} 次实测");
            }

            string ids = IdsHash(activeIds);
            if (history.Models.TryGetValue(key, out AdmissionLedger.ModelEstimate? cached) && cached.Ids == ids)
                return (cached.Bytes, "贴图模型，无实测");
        }

        if (gameVersion == null)
            return (VramModel.FixedOverheadBytes, "无法读取游戏版本，只计固定开销");
        CliLog.Info("[显存] 本名单无实测，按贴图模型估算……");
        Stopwatch watch = Stopwatch.StartNew();
        long modelled = VramModel.Estimate(VramModel.Scan(activeIds, modDirs(), gameVersion), tier);
        using (ledger.Lock(LockTimeout))
        {
            AdmissionLedger.History history = ledger.ReadHistory();
            AdmissionLedger.DropOtherGames(history.Models, key);
            history.Models[key] = new AdmissionLedger.ModelEstimate { Ids = IdsHash(activeIds), Bytes = modelled };
            ledger.WriteHistory(history);
        }

        return (modelled, $"贴图模型，无实测，扫描 {watch.Elapsed.TotalSeconds:0.0}s");
    }

    public long KnownVram(string key)
    {
        using (ledger.Lock(LockTimeout))
        {
            AdmissionLedger.History history = ledger.ReadHistory();
            if (AdmissionLedger.FromHistory(history, key) is (long bytes, int _)) return bytes;
            return history.Models.TryGetValue(key, out AdmissionLedger.ModelEstimate? cached) ? cached.Bytes : 0;
        }
    }

    public void RecordSourceQuietly(string key, IReadOnlyList<string> paths)
    {
        try
        {
            using (ledger.Lock(LockTimeout))
            {
                AdmissionLedger.History history = ledger.ReadHistory();
                if (AdmissionLedger.RecordSource(history, AdmissionLedger.SourceKey(key), paths))
                    ledger.WriteHistory(history);
            }
        }
        catch (Exception exception) when (exception is StoreException or IOException or UnauthorizedAccessException)
        {
        }
    }

    public MemoryEstimate EstimateMemory(string key)
    {
        long fallback = MemoryAccount.InstanceDefault();
        if (fallback == 0)
            return new(0, "已配置不做内存准入");
        using (ledger.Lock(LockTimeout))
        {
            AdmissionLedger.History history = ledger.ReadHistory();
            if (AdmissionLedger.FromMemoryHistory(history, key) is (long bytes, int count))
                return new(bytes, $"历史峰值，{count} 次实测");
            if (AdmissionLedger.FromOtherGamesMemory(history, key) is (long earlier, int earlierCount))
                return new(earlier, $"上一游戏版本的峰值，{earlierCount} 次实测", fallback);
        }

        return new(fallback, DefaultMemorySource);
    }

    private static string DefaultMemorySource =>
        "缺省估值，无实测";

    public sealed record MemoryEstimate(long Bytes, string Source, long Fallback = 0);

    public void ObserveMemoryQuietly(int number, long privateBytes)
    {
        if (privateBytes <= 0) return;
        (long Peak, int Instances)? high = null;
        AdmissionLedger.Ticket? ticket = null;
        try
        {
            using (ledger.Lock(TimeSpan.FromSeconds(5)))
            {
                ticket = ledger.LiveTickets().FirstOrDefault(t => t.Number == number);
                if (ticket == null) return;
                AdmissionLedger.History history = ledger.ReadHistory();
                high = AdmissionLedger.MemoryHigh(history, ticket, privateBytes);
                if (AdmissionLedger.ObserveMemory(history, ticket, privateBytes))
                    ledger.WriteHistory(history);
            }
        }
        catch (Exception exception) when (exception is StoreException or IOException or UnauthorizedAccessException)
        {
        }

        if (high is not { } found || ticket == null) return;
        CliLog.Info($"[内存] 本批收尾私有字节 {Gb(privateBytes)}，同组此前最高 {Gb(found.Peak)}（{found.Instances} 个实例，{ticket.MemoryKey}）");
        ActivityLog.MemoryHigh(number, ticket.GamePid, ticket.MemoryKey!, privateBytes, found.Peak, found.Instances);
    }

    public sealed class Reservation : IDisposable
    {
        private readonly AdmissionLedger ledger;
        private readonly AdmissionLedger.Ticket ticket;
        private bool attached;

        internal Reservation(AdmissionLedger ledger, AdmissionLedger.Ticket ticket)
        {
            this.ledger = ledger;
            this.ticket = ticket;
        }

        public void Attach(ProcessIdentity.Stamp game)
        {
            ticket.GamePid = game.Pid;
            ticket.GameStartedAt = game.StartedAt;
            using (ledger.Lock(LockTimeout)) ledger.Adopt(ticket);
            attached = true;
        }

        public void MarkStartupLoaded()
        {
            ticket.StartupLoadedAt = DateTimeOffset.Now;
            TryWrite(endLoading: true);
        }

        public void MarkLoaded(bool skipHistory = false)
        {
            ticket.LoadedAt = DateTimeOffset.Now;
            ticket.SkipHistory = skipHistory;
            TryWrite(endLoading: true);
        }

        private void TryWrite(bool endLoading)
        {
            try
            {
                using (ledger.Lock(LockTimeout))
                {
                    ledger.WriteTicket(ticket);
                    if (endLoading) ledger.EndLoading(ticket);
                }
            }
            catch (Exception exception) when (exception is StoreException or IOException or UnauthorizedAccessException)
            {
            }
        }

        public void Dispose()
        {
            if (attached) return;
            using (ledger.Lock(LockTimeout)) ledger.DeleteTicket(ticket);
        }
    }

    internal sealed record MemoryCheck(AdmissionLedger.Decision Decision, MemoryAccount.Snapshot Snapshot,
        IReadOnlyList<AdmissionLedger.Ticket> Tickets, long Headroom, Spill Spill)
    {
        public long External => Math.Max(0, Snapshot.Total - Snapshot.Available - Snapshot.Residents.Sum(r => r.PrivateBytes));
    }

    internal sealed record Spill(long Pending, long Mine, long Alone, long Need, long Cap)
    {
        public static readonly Spill None = new(0, 0, 0, 0, 0);
    }

    internal static Spill SpillOf(long need, GpuMemory.Snapshot snapshot, IReadOnlyList<AdmissionLedger.Ticket> tickets,
        int number, long headroom)
    {
        long cap = Math.Max(0, snapshot.Total - headroom);
        long pending = tickets.Where(t => t.Number != number)
            .Sum(t => Math.Max(0, t.Expected - (t.GamePid is int pid ? snapshot.ProcessUsed.GetValueOrDefault(pid) : 0)));
        long used = snapshot.AdapterUsed;
        long before = Math.Max(0, used - cap);
        long withPending = Math.Max(0, used + pending - cap);
        long withMine = Math.Max(0, used + pending + need - cap);
        return new Spill(withPending - before, withMine - withPending, Math.Max(0, need - cap), need, cap);
    }

    internal static MemoryCheck CheckMemory(long need, MemoryAccount.Snapshot snapshot,
        IReadOnlyList<AdmissionLedger.Ticket> tickets, int number, long headroom, bool aheadInQueue, Spill? spill = null)
    {
        spill ??= Spill.None;
        long pending = tickets.Where(t => t.Number != number && t.MemoryExpected > 0)
            .Sum(t => Math.Max(0, t.MemoryExpected - (t.GamePid is int pid ? snapshot.PrivateOf(pid) : 0)));
        long used = snapshot.Total - snapshot.Available;
        long rimWorldUsed = snapshot.Residents.Sum(r => r.PrivateBytes);
        AdmissionLedger.Decision decision = AdmissionLedger.Decide(need + spill.Mine, snapshot.Total, used,
            rimWorldUsed, pending + spill.Pending, headroom, aheadInQueue);
        bool impossible = snapshot.ResidentsFault == null
                          && AdmissionLedger.Decide(need + spill.Alone, snapshot.Total, used, rimWorldUsed, 0, headroom,
                              aheadInQueue).Impossible;
        return new MemoryCheck(decision with { Impossible = impossible }, snapshot, tickets, headroom, spill);
    }

    public async Task<Reservation> AdmitAsync(int number, string key, string label, long need, string source,
        int timeoutMinutes, Action<string>? log, CancellationToken token, IdleEviction? eviction = null,
        string? memoryKey = null, long memoryNeed = 0, string memorySource = "", long memoryFallback = 0)
    {
        long headroom = Headroom();
        long memoryHeadroom = MemoryAccount.Headroom();
        string me = Guid.NewGuid().ToString("N");
        Stopwatch watch = Stopwatch.StartNew();
        TimeSpan? lastPrint = null;
        string? lastBlocker = null;
        string? lastLogged = null;
        string lastPrefix = "[内存]";
        bool unreadableNoted = false;
        bool memoryUnreadableNoted = false;
        bool residentsUnreadableNoted = false;
        bool ledgerUnreadableNoted = false;
        DateTimeOffset enqueuedAt = DateTimeOffset.Now;
        Action? activity = null;
        string waiting = "";
        try
        {
            while (true)
            {
                (GpuMemory.Snapshot? snapshot, string? reason) = read();
                if (snapshot == null && !unreadableNoted)
                {
                    log?.Invoke($"[显存] 无法读取（{reason}），按显存不溢出准入");
                    unreadableNoted = true;
                }

                MemoryAccount.Snapshot? memory = memoryNeed > 0 ? readMemory() : null;
                if (memoryNeed > 0 && memory == null && !memoryUnreadableNoted)
                {
                    log?.Invoke("[内存] 无法读取物理内存，本轮只按排队顺序准入");
                    memoryUnreadableNoted = true;
                }

                if (memory?.ResidentsFault is string residentsFault && !residentsUnreadableNoted)
                {
                    log?.Invoke($"[内存] 无法枚举 RimWorld 进程（{residentsFault}），本轮装不下即排队");
                    residentsUnreadableNoted = true;
                }

                Func<IdleEviction.Snapshot?, string> render;
                List<IdleEviction.Resident> residents = [];
                string blocker;
                activity = null;
                (long Need, long Available)? evictMemory = null;
                using (ledger.Lock(LockTimeout))
                {
                    List<AdmissionLedger.Ticket> tickets = ledger.LiveTickets(out int unreadableTickets);
                    if (snapshot != null) ObserveLocked(tickets, snapshot);
                    List<AdmissionLedger.Ticket> loading = tickets
                        .Where(t => t.Number != number && ledger.Loading(t) && !ledger.LoadedByMe(t))
                        .OrderBy(t => t.CreatedAt).ToList();
                    bool loadFits = true;
                    List<AdmissionLedger.QueueEntry> queue = ledger.LiveQueue(out int unreadableQueue);
                    if (unreadableTickets + unreadableQueue > 0 && !ledgerUnreadableNoted)
                    {
                        log?.Invoke($"[内存] 准入账有 {unreadableTickets} 条在途记录、{unreadableQueue} 个排队行无法读取，按不存在计");
                        ledgerUnreadableNoted = true;
                    }
                    AdmissionLedger.QueueEntry? ahead = queue.FirstOrDefault(entry => entry.Id != me
                        && (entry.EnqueuedAt, entry.Id).CompareTo((enqueuedAt, me)) < 0);

                    Spill spill = snapshot == null ? Spill.None : SpillOf(need, snapshot, tickets, number, headroom);
                    MemoryCheck? memoryCheck = memory == null ? null
                        : CheckMemory(memoryNeed, memory, tickets, number, memoryHeadroom, ahead != null, spill);
                    if (memoryCheck is { Decision.Impossible: true } && memoryFallback > 0 && memoryFallback < memoryNeed)
                    {
                        log?.Invoke($"[内存] 上一游戏版本的峰值 {Gb(memoryNeed)} 本机装不下，改按缺省估值 {Gb(memoryFallback)}");
                        memoryNeed = memoryFallback;
                        memorySource = DefaultMemorySource;
                        memoryFallback = 0;
                        continue;
                    }

                    if (memoryCheck is { Decision.Impossible: true })
                    {
                        throw new StoreException(
                            $"物理内存装不下，排队也等不到：需 {NeedText(memoryNeed, memorySource, spill with { Mine = spill.Alone })}，"
                            + $"而物理内存 {Gb(memory!.Total)} − 预留 {Gb(memoryHeadroom)}"
                            + RimRunnerConfig.SourceTag(RimRunnerConfig.Machine().MemHeadroomMb.FromFile)
                            + $" − 未归到 RimWorld 进程的占用 {Gb(memoryCheck.External)} 只剩 "
                            + $"{Gb(memory.Total - memoryHeadroom - memoryCheck.External)}");
                    }

                    long fullNeed = memoryNeed + spill.Mine;
                    bool memoryFits = memoryCheck == null || fullNeed <= memoryCheck.Decision.Available;
                    string closing = memoryCheck == null ? ""
                        : $"：需 {NeedText(memoryNeed, memorySource, spill)}，可用 {Gb(memoryCheck.Decision.Available)}"
                          + VramClause(need, source, spill, snapshot != null);

                    string summary;
                    if (loadFits && ahead == null && memoryFits)
                    {
                        Reservation admitted = Admit(number, key, label, need, memoryKey, memoryNeed, me,
                            Closing(lastPrint, lastPrefix, watch, lastPrefix == "[内存]" ? closing : ""), log);
                        if (lastPrint == null && memoryCheck != null)
                            CliLog.Info((lastLogged == null ? "[内存] 未排队，立即准入" : $"[内存] 排队 {watch.Elapsed.TotalSeconds:0}s 后准入")
                                        + closing);
                        return admitted;
                    }

                    if (loadFits && ahead == null)
                    {
                        MemoryCheck check = memoryCheck!;
                        render = observed => MemoryWaitLine(memoryNeed, memorySource, check, observed,
                            VramClause(need, source, spill, snapshot != null));
                        residents = MemoryResidents(check.Snapshot);
                        blocker = "";
                        summary = $"内存，需 {Gb(fullNeed)}、可用 {Gb(check.Decision.Available)}";
                        evictMemory = (fullNeed, check.Decision.Available);
                        long available = check.Decision.Available;
                        activity = () => ActivityLog.Queued(fullNeed, available, check.Snapshot.Residents.Select(r =>
                            new ActivityLog.QueuedInstance(r.Pid,
                                GameProcessProbe.ClaimedNumber(r.CommandLine, RimRunner.RimRunnerEngineArgs.Instance), r.PrivateBytes)));
                    }
                    else
                    {
                        string line = LoadingLine(loadFits ? [] : loading, ahead);
                        render = _ => line;
                        blocker = loadFits ? ahead!.Label : Labels(loading);
                        summary = loadFits ? $"排在 {ahead!.Label} 之后" : LoadingSummary(loading);
                    }

                    ledger.Enqueue(new AdmissionLedger.QueueEntry
                    {
                        Id = me, Number = number, EnqueuedAt = enqueuedAt, Need = fullNeed, Label = label,
                        Waiting = summary,
                    });
                }

                string bare = render(null);
                string prefix = bare[..bare.IndexOf(']')] + "]";
                string logged = prefix + blocker;
                bool quiet = watch.Elapsed < grace;
                bool emit = quiet
                    ? logged != lastLogged
                    : blocker != lastBlocker || lastPrint == null || watch.Elapsed - lastPrint >= Reprint;
                IdleEviction.Snapshot? observed = eviction != null && residents.Count > 0
                                                  && (emit || evictMemory != null)
                    ? eviction.Observe(residents, number)
                    : null;
                waiting = observed == null ? bare : render(observed);
                if (quiet)
                {
                    if (emit) CliLog.Info(waiting);
                    lastLogged = logged;
                }
                else if (emit)
                {
                    log?.Invoke(waiting);
                    activity?.Invoke();
                    lastPrint = watch.Elapsed;
                    lastBlocker = blocker;
                    lastPrefix = prefix;
                }

                if (evictMemory is { } memoryPlan && observed != null)
                    eviction!.TryMemory(number, memoryPlan.Need, memoryPlan.Available, observed, log);

                if (timeoutMinutes > 0 && watch.Elapsed >= TimeSpan.FromMinutes(timeoutMinutes))
                    throw new QueueTimeoutException($"排队 {timeoutMinutes} 分钟仍未准入，放弃启动实例。最后一次排队状态：{waiting}");
                await Task.Delay(Poll, token);
            }
        }
        finally
        {
            try
            {
                using (ledger.Lock(LockTimeout)) ledger.Dequeue(me);
            }
            catch (StoreException)
            {
            }
        }
    }

    internal static string NeedText(long memoryNeed, string memorySource, Spill spill) =>
        spill.Mine > 0
            ? $"{Gb(memoryNeed + spill.Mine)}（内存 {Gb(memoryNeed)}，{memorySource}；显存溢出 {Gb(spill.Mine)}）"
            : $"{Gb(memoryNeed)}（{memorySource}）";

    private static string VramClause(long need, string source, Spill spill, bool readable) =>
        !readable ? "；无法读取显存，按不溢出处理"
        : need <= 0 ? ""
        : $"；显存需 {Gb(need)}（{source}），整卡减预留 {Gb(spill.Cap)}";

    private static string? Closing(TimeSpan? lastPrint, string prefix, Stopwatch watch, string detail) =>
        lastPrint == null ? null : $"{prefix} 排队 {watch.Elapsed.TotalSeconds:0}s 后准入{(detail.StartsWith('；') ? "：" + detail[1..] : detail)}";

    private Reservation Admit(int number, string key, string label, long need, string? memoryKey, long memoryNeed,
        string me, string? closing, Action<string>? log)
    {
        ledger.Dequeue(me);
        var ticket = new AdmissionLedger.Ticket
        {
            Id = Guid.NewGuid().ToString("N"), Number = number, Key = key, Label = label, Expected = need, CreatedAt = DateTimeOffset.Now,
            MemoryKey = memoryKey, MemoryExpected = memoryNeed,
        };
        ledger.Open(ticket);
        if (closing != null) log?.Invoke(closing);
        return new Reservation(ledger, ticket);
    }

    private static List<IdleEviction.Resident> MemoryResidents(MemoryAccount.Snapshot snapshot) =>
        [.. snapshot.Residents.Select(r => new IdleEviction.Resident(r.Pid, r.PrivateBytes, r.CommandLine))];

    internal static string MemoryWaitLine(long need, string source, MemoryCheck check,
        IdleEviction.Snapshot? observed = null, string vramClause = "")
    {
        var line = new StringBuilder();
        long available = check.Decision.Available;
        long full = need + check.Spill.Mine;
        line.Append($"[内存] 排队：需 {NeedText(need, source, check.Spill)}，可用 {Gb(available)}");
        if (full > available) line.Append($"，差 {Gb(full - available)}");
        line.Append($"（物理内存 {Gb(check.Snapshot.Total)}，未归到 RimWorld 进程的占用 {Gb(check.External)}，"
                    + $"预留 {Gb(check.Headroom)}{(check.Spill.Pending > 0 ? $"，在途实例的显存溢出 {Gb(check.Spill.Pending)}" : "")}）");
        line.Append(vramClause);
        foreach (MemoryAccount.Resident resident in check.Snapshot.Residents.OrderByDescending(r => r.PrivateBytes))
        {
            AdmissionLedger.Ticket? ticket = check.Tickets.FirstOrDefault(t => t.GamePid == resident.Pid);
            string name = ticket?.Label ?? InstanceLabel(resident.CommandLine);
            line.Append("\n        ").Append($"{name} · PID {resident.Pid} · 当前 {Gb(resident.PrivateBytes)}");
            if (ticket != null && ticket.MemoryExpected - resident.PrivateBytes >= MinPrintedRemaining)
                line.Append($"，预期峰值 {Gb(ticket.MemoryExpected)}");
            if (observed?.StatusOf(resident.Pid) is { } status)
                line.Append(" · ").Append(status);
        }

        return line.ToString();
    }

    private static string Labels(IEnumerable<AdmissionLedger.Ticket> tickets) => string.Join("、", tickets.Select(t => t.Label));

    private static string LoadingSummary(IReadOnlyList<AdmissionLedger.Ticket> loading) => $"加载闸，等 {Labels(loading)} 加载完";

    internal static string LoadingLine(IReadOnlyList<AdmissionLedger.Ticket> loading, AdmissionLedger.QueueEntry? ahead)
    {
        if (loading.Count == 0)
            return $"[准入] 排队：排在 {ahead!.Label} 之后";
        string line = $"[加载闸] 排队：等 {Labels(loading)} 加载完";
        return ahead == null ? line : line + $"；排在 {ahead.Label} 之后";
    }

    public void ObserveQuietly()
    {
        try
        {
            (GpuMemory.Snapshot? snapshot, _) = read();
            if (snapshot == null) return;
            using (ledger.Lock(TimeSpan.FromSeconds(5)))
                ObserveLocked(ledger.LiveTickets(), snapshot);
        }
        catch (Exception exception) when (exception is StoreException or IOException or UnauthorizedAccessException)
        {
        }
    }

    private void ObserveLocked(IEnumerable<AdmissionLedger.Ticket> tickets, GpuMemory.Snapshot snapshot)
    {
        AdmissionLedger.History history = ledger.ReadHistory();
        HashSet<int> driven = InstanceProbe.DrivenNumbers();
        if (AdmissionLedger.Observe(history, tickets.Where(ticket => !driven.Contains(ticket.Number)),
                snapshot.Demands(), DateTimeOffset.Now))
            ledger.WriteHistory(history);
    }

    private static (GpuMemory.Snapshot?, string?) ReadWithEngine()
    {
        GpuMemory.Snapshot? snapshot = GpuMemory.TryRead(out string? reason);
        return (snapshot == null ? null : snapshot with { EngineDemand = EngineDemands() }, reason);
    }

    internal static Dictionary<int, long> EngineDemands()
    {
        var result = new Dictionary<int, long>();
        foreach (MachineSnapshot.Instance instance in MachineSnapshot.Read()?.Instances ?? [])
            if (instance is { Pid: int pid, GpuDemand: long demand })
                result[pid] = demand;
        foreach (InstanceLink link in InstanceLinks.All)
            if (link is { Pid: int pid, GpuDemand: long demand })
                result[pid] = demand;
        return result;
    }

    internal sealed record RimWorldInstance(int Pid, long Bytes, AdmissionLedger.Ticket? Ticket, string? CommandLine,
        long Shared = 0)
    {
        public long Remaining => Ticket == null ? 0 : Math.Max(0, Ticket.Expected - Bytes);
    }

    internal static List<RimWorldInstance> RimWorldInstances(GpuMemory.Snapshot snapshot,
        IReadOnlyList<AdmissionLedger.Ticket> tickets, out string? fault)
    {
        fault = null;
        List<GameProcessProbe.GameProcess> processes;
        try
        {
            processes = GameProcessProbe.Enumerate();
        }
        catch (StoreException exception)
        {
            fault = exception.Message;
            return [];
        }

        return
        [
            .. processes.Select(p => new RimWorldInstance(p.Pid, snapshot.ProcessUsed.GetValueOrDefault(p.Pid),
                tickets.FirstOrDefault(t => t.GamePid == p.Pid), p.CommandLine, snapshot.Shared(p.Pid)))
        ];
    }

    internal const long SharedNoteBytes = 1_000_000_000;

    internal static string SharedNote(RimWorldInstance instance) =>
        instance.Shared >= SharedNoteBytes ? $"（另有 {Gb(instance.Shared)} 在共享内存）" : "";

    private const long MinPrintedRemaining = 50_000_000;

    internal static string InstanceLabel(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return "RimWorld（命令行无法读取）";
        int? number = GameProcessProbe.ClaimedNumber(commandLine, RimRunner.RimRunnerEngineArgs.Instance);
        if (number == null) return "RimWorld（非 RimRunner 实例）";
        string? modList = GameProcessProbe.ArgValue(commandLine, RimRunner.RimRunnerEngineArgs.ModList);
        string? name = new ClaimStore().NameOf(number.Value);
        return (name == null ? "未登记的游戏进程" : $"实例 {name}") + $" · {modList ?? "(无名单)"}";
    }

    internal static string Gb(long bytes) => $"{Math.Round(bytes / 1e9, 1) + 0.0:0.0} GB";

    private static string IdsHash(IReadOnlyList<string> ids) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join("\n", ids.Select(id => id.ToLowerInvariant()).Prepend($"model r{VramModel.Revision}")))))[..16];
}
