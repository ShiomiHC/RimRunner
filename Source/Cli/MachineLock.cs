using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimRunnerCli;

public sealed class MachineLock(string root, Func<int>? cap = null,
    Func<double>? reserve = null, Func<Func<double?>?>? probe = null, string title = MachineLock.MachineTitle,
    TimeSpan? grace = null, Func<bool>? capConfigured = null)
{
    public const string Shared = "shared";
    public const string Exclusive = "exclusive";
    public const string AwaitShared = "await-shared";
    public const string AwaitExclusive = "await-exclusive";

    public sealed class Ticket
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("mode")] public string Mode { get; set; } = AwaitShared;
        [JsonPropertyName("label")] public string Label { get; set; } = "";
        [JsonPropertyName("productRoot")] public string? ProductRoot { get; set; }
        [JsonPropertyName("cases")] public List<string> Cases { get; set; } = [];
        [JsonPropertyName("since")] public DateTimeOffset Since { get; set; }
        [JsonPropertyName("queuedAt")] public DateTimeOffset QueuedAt { get; set; }
        [JsonPropertyName("admittedAt")] public DateTimeOffset AdmittedAt { get; set; }
        [JsonPropertyName("group")] public string? Group { get; set; }
        [JsonPropertyName("cap")] public int Cap { get; set; }
        [JsonPropertyName("headroomWait")] public bool HeadroomWait { get; set; }
    }

    public sealed class WaitNote
    {
        public string? Gate { get; set; }
    }

    internal const string HeadroomGateName = "P 核余量";
    internal const string CapGateName = "同时执行的实例上限";
    internal const string ExclusiveGateName = "独占的批";

    internal static string GateOf(IReadOnlyList<Ticket> blockers, HeadroomShort? shortfall) =>
        shortfall != null ? HeadroomGateName
        : blockers.Any(t => t.Mode is Exclusive or AwaitExclusive) ? ExclusiveGateName
        : CapGateName;

    internal static string WhileQueued(string gate) =>
        "排" + (char.IsAsciiLetter(gate[0]) ? " " : "") + gate + "期间";

    public sealed record HeadroomShort(double? Idle, double Need, int Fresh, IReadOnlyList<Ticket> Busy, int Ahead);

    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Reprint = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(60);

    internal static readonly TimeSpan Ramp = TimeSpan.FromSeconds(20);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Func<int> capOf = cap ?? BusyCap.Resolve;
    private readonly Func<bool> capConfiguredOf = capConfigured ?? (() => RimRunnerConfig.Machine().BusyCap.FromFile);
    private readonly Func<double> reserveOf = reserve ?? BusyReserve.Resolve;
    private readonly Func<Func<double?>?> probeOf = probe ?? CpuHeadroom.NewProbe;
    private readonly Func<string?> probeFailureOf = probe == null ? CpuHeadroom.ProbeFailure : () => null;
    private readonly TimeSpan grace = grace ?? QueuePolicy.Grace;
    private readonly string title = title;

    public string Root { get; } = root;

    internal const string TablePrefix = "machine-lock:";

    internal string Table => TablePrefix + PathIdentity.Normalize(Root).ToUpperInvariant();

    internal const string MachineTitle = "机器锁";
    internal const string OutputLockTitle = "产出根锁";

    public static MachineLock ForClaimRoot(string claimRoot) =>
        new(Path.Combine(Path.GetDirectoryName(CommandContext.FullPath(claimRoot)) ?? claimRoot, "machine"));

    public static MachineLock ForProductRoot(string claimRoot, string productRoot) =>
        new(Path.Combine(Path.GetDirectoryName(CommandContext.FullPath(claimRoot)) ?? claimRoot, "product-root-locks",
                ProductRootKey(productRoot)),
            cap: () => 0, reserve: () => 0, probe: () => null, title: OutputLockTitle);

    internal static string ProductRootKey(string productRoot)
    {
        string normalized = PathIdentity.Normalize(productRoot).ToUpperInvariant();
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }

    public static List<Ticket> Blockers(Ticket me, IEnumerable<Ticket> others, int cap = 0, bool skipsHeadroom = false)
    {
        List<Ticket> rest = [.. others.Where(t => t.Id != me.Id)];
        return me.Mode switch
        {
            AwaitShared => SharedBlockers(me, rest, cap, skipsHeadroom),
            AwaitExclusive =>
            [
                .. rest.Where(t => t.Mode is Shared or Exclusive
                                   || (t.Mode == AwaitExclusive && Ahead(t, me))),
            ],
            _ => [],
        };
    }

    public static IReadOnlyList<string> MissedQuiet(EngineReport? report)
    {
        if (report == null || report.Rejections.Count == 0
            || report.Rejections.Any(r => r.Gate != RimRunner.RimRunnerRejectionGate.Quiet))
            return [];
        return [.. report.Rejections.Select(r => r.Subject).OfType<string>().Distinct(StringComparer.Ordinal)];
    }

    public static IReadOnlyList<string> QuietCases(StructuredRunRequest request, CaseManifest manifest)
    {
        return [.. manifest.SelectedBy(request).Where(entry => entry.QuietMachine).Select(entry => entry.Id)];
    }

    public int FreeSlots() => CapUnlimited ? int.MaxValue
        : Math.Max(0, capOf() - LiveTickets().Count(ticket => ticket.Mode == Shared));

    public bool CapUnlimited => capOf() <= 0;

    private static List<Ticket> SharedBlockers(Ticket me, List<Ticket> rest, int cap, bool skipsHeadroom)
    {
        bool joins = Joins(me, rest);
        List<Ticket> writers = [.. rest.Where(t => t.Mode == Exclusive || (t.Mode == AwaitExclusive && !joins))];
        if (writers.Count > 0 || cap <= 0) return writers;
        List<Ticket> busy = [.. rest.Where(t => t.Mode == Shared)];
        List<Ticket> ahead = [.. rest.Where(t => t.Mode == AwaitShared && QueuedAhead(t, me) && !HeldByWriter(t, rest)
                                                 && !(skipsHeadroom && t.HeadroomWait))];
        return busy.Count + ahead.Count >= cap ? [.. busy, .. ahead] : [];
    }

    internal static bool Joins(Ticket me, IEnumerable<Ticket> others) =>
        me.Group != null && others.Any(t => t.Id != me.Id && t.Group == me.Group && t.Mode == Shared);

    private static bool HeldByWriter(Ticket waiter, List<Ticket> rest) =>
        rest.Any(t => t.Mode is Exclusive or AwaitExclusive) && !Joins(waiter, rest);

    public static HeadroomShort? HeadroomGate(Ticket me, IEnumerable<Ticket> others, double reserve, double? idle,
        DateTimeOffset now, double need = BusyReserve.InstanceCores)
    {
        if (reserve <= 0) return null;
        List<Ticket> rest = [.. others.Where(t => t.Id != me.Id)];
        List<Ticket> busy = [.. rest.Where(t => t.Mode == Shared).OrderBy(t => t.QueuedAt)];
        int ahead = rest.Count(t => t.Mode == AwaitShared && QueuedAhead(t, me) && !HeldByWriter(t, rest));
        if (busy.Count == 0 && ahead == 0) return null;
        int fresh = busy.Count(t => now - t.AdmittedAt < Ramp);
        double required = reserve + need + fresh * need;
        if (ahead == 0 && idle is double free && free >= required) return null;
        return new HeadroomShort(idle, required, fresh, busy, ahead);
    }

    private static bool Ahead(Ticket a, Ticket b) =>
        (a.Since, a.Id).CompareTo((b.Since, b.Id)) < 0;

    private static bool QueuedAhead(Ticket a, Ticket b) =>
        (a.QueuedAt, a.Id).CompareTo((b.QueuedAt, b.Id)) < 0;

    public async Task<Hold> AcquireSharedAsync(string label, int timeoutMinutes, Action<string>? log,
        CancellationToken token, bool queueForCap = true, string? group = null, bool cpuAdmission = true,
        string? productRoot = null, WaitNote? note = null)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        var ticket = new Ticket
        {
            Id = Guid.NewGuid().ToString("N"),
            Mode = AwaitShared,
            Label = label,
            ProductRoot = productRoot,
            Since = now,
            QueuedAt = now,
            Group = group,
        };
        var hold = new Hold(this, ticket, queueForCap ? capOf() : 0, queueForCap && cpuAdmission ? reserveOf() : 0, note);
        try
        {
            await hold.WaitAsync(Shared, timeoutMinutes, log, token);
            return hold;
        }
        catch
        {
            hold.Dispose();
            throw;
        }
    }

    public async Task<Hold> AcquireExclusiveAsync(string label, string reason, int timeoutMinutes,
        Action<string>? log, CancellationToken token)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        var ticket = new Ticket
        {
            Id = Guid.NewGuid().ToString("N"),
            Mode = AwaitExclusive,
            Label = label,
            Cases = [reason],
            Since = now,
            QueuedAt = now,
        };
        var hold = new Hold(this, ticket, 0, 0);
        try
        {
            await hold.WaitAsync(Exclusive, timeoutMinutes, log, token);
            return hold;
        }
        catch
        {
            hold.Dispose();
            throw;
        }
    }

    public sealed class Hold : IDisposable
    {
        private readonly MachineLock owner;
        private readonly Ticket ticket;
        private readonly int cap;
        private readonly double reserve;
        private readonly WaitNote? note;
        private bool released;

        internal Hold(MachineLock owner, Ticket ticket, int cap, double reserve, WaitNote? note = null)
        {
            this.owner = owner;
            this.ticket = ticket;
            this.cap = cap;
            this.reserve = reserve;
            this.note = note;
            ticket.Cap = cap;
        }

        public bool IsExclusive => !released && ticket.Mode == Exclusive;

        public void Suspend()
        {
            using (owner.Lock())
                owner.Delete(ticket.Id);
            released = true;
        }

        public async Task ResumeSharedAsync(int timeoutMinutes, Action<string>? log, CancellationToken token)
        {
            released = false;
            ticket.Mode = AwaitShared;
            ticket.Since = DateTimeOffset.Now;
            await WaitAsync(Shared, timeoutMinutes, log, token, cpuPassed: true);
        }

        public async Task ToExclusiveAsync(IReadOnlyList<string> cases, int timeoutMinutes, Action<string>? log,
            CancellationToken token)
        {
            if (IsExclusive) return;
            log?.Invoke($"[{owner.title}] 本批含需安静的用例 {string.Join("、", cases)}，独占机器");
            ticket.Mode = AwaitExclusive;
            ticket.Cases = [.. cases];
            ticket.Since = DateTimeOffset.Now;
            released = false;
            await WaitAsync(Exclusive, timeoutMinutes, log, token);
        }

        internal async Task WaitAsync(string target, int timeoutMinutes, Action<string>? log, CancellationToken token,
            bool cpuPassed = false)
        {
            Stopwatch watch = Stopwatch.StartNew();
            TimeSpan? lastPrint = null;
            string? lastBlockers = null;
            string? lastLogged = null;
            string waiting = "";
            bool wantsCpu = target == Shared && reserve > 0 && !cpuPassed;
            Func<double?>? probe = wantsCpu ? owner.probeOf() : null;
            if (wantsCpu && probe == null)
            {
                string failure = owner.probeFailureOf() ?? "探针未建成";
                if (cap <= 0)
                    throw new StoreException($"P 核余量无法测量（{failure}）");
                log?.Invoke($"[{owner.title}] 无法读取 P 核余量（{failure}），按名额上限 {cap} 准入");
            }
            while (true)
            {
                Action? activity = null;
                double? idle = probe?.Invoke();
                using (owner.Lock())
                {
                    DateTimeOffset now = DateTimeOffset.Now;
                    List<Ticket> live = owner.LiveTickets();
                    List<Ticket> blockers = Blockers(ticket, live, cap, skipsHeadroom: !wantsCpu);
                    HeadroomShort? shortfall = blockers.Count == 0 && probe != null
                        ? HeadroomGate(ticket, live, reserve, idle, now)
                        : null;
                    ticket.HeadroomWait = shortfall != null;
                    if (blockers.Count == 0 && shortfall == null)
                    {
                        ticket.Mode = target;
                        ticket.AdmittedAt = now;
                        owner.Write(ticket);
                        if (lastPrint != null)
                            log?.Invoke($"[{owner.title}] 排队 {watch.Elapsed.TotalSeconds:0}s 后取得{ModeName(target)}");
                        else if (lastLogged != null)
                            CliLog.Info($"[{owner.title}] 排队 {watch.Elapsed.TotalSeconds:0}s 后取得{ModeName(target)}");
                        else
                            CliLog.Info($"[{owner.title}] 未排队，立即取得{ModeName(target)}");
                        return;
                    }

                    owner.Write(ticket);
                    if (note != null) note.Gate = GateOf(blockers, shortfall);
                    string budget = Budget(watch.Elapsed, timeoutMinutes);
                    waiting = shortfall != null
                        ? HeadroomLine(shortfall, now, owner.title, budget)
                        : WaitLine(target, blockers, cap, now, owner.title, budget, owner.capConfiguredOf());
                    string names = shortfall != null
                        ? "headroom:" + string.Join(",", shortfall.Busy.Select(t => t.Id).Order(StringComparer.Ordinal))
                          + ":" + shortfall.Ahead + ":" + (shortfall.Idle != null)
                        : string.Join(",", blockers.Select(t => t.Id).Order(StringComparer.Ordinal));
                    if (watch.Elapsed < owner.grace)
                    {
                        if (names != lastLogged) CliLog.Info(waiting);
                        lastLogged = names;
                    }
                    else if (names != lastBlockers || lastPrint == null || watch.Elapsed - lastPrint >= Reprint)
                    {
                        log?.Invoke(names == lastBlockers ? ContinueLine(watch.Elapsed, shortfall, timeoutMinutes) : waiting);
                        if (shortfall != null)
                            activity = () => ActivityLog.MachineQueued("headroom", cap, shortfall.Idle, shortfall.Need,
                                shortfall.Busy.Count, shortfall.Ahead);
                        else if (target == Shared && !blockers.Any(t => t.Mode is Exclusive or AwaitExclusive))
                        {
                            int busy = blockers.Count(t => t.Mode == Shared);
                            int ahead = blockers.Count - busy;
                            activity = () => ActivityLog.MachineQueued("cap", cap, null, null, busy, ahead);
                        }

                        lastPrint = watch.Elapsed;
                        lastBlockers = names;
                    }
                }

                activity?.Invoke();
                if (timeoutMinutes > 0 && watch.Elapsed >= TimeSpan.FromMinutes(timeoutMinutes))
                    throw new QueueTimeoutException($"{owner.title}排队 {timeoutMinutes} 分钟仍未取得{ModeName(target)}。最后一次排队状态：{waiting}");
                await Task.Delay(Poll, token);
            }
        }

        public void Dispose()
        {
            if (released) return;
            released = true;
            try
            {
                using (owner.Lock())
                    owner.Delete(ticket.Id);
            }
            catch (StoreException)
            {
            }
        }
    }

    internal static string WaitLine(string target, IReadOnlyList<Ticket> blockers, int cap, DateTimeOffset now,
        string title = MachineTitle, string? budget = null, bool capConfigured = false)
    {
        string spent = budget == null ? "" : $"（{budget}）";
        if (target != Shared || blockers.Any(t => t.Mode is Exclusive or AwaitExclusive))
            return $"[{title}] 等{ModeName(target)}{spent}：{string.Join("；", DescribeAll(blockers, now))}";
        List<Ticket> busy = [.. blockers.Where(t => t.Mode == Shared).OrderBy(t => t.QueuedAt)];
        int ahead = blockers.Count - busy.Count;
        return $"[{title}] 同时执行的实例已满（{(capConfigured ? "已配置" : "缺省")}上限 {cap}，现有 {busy.Count} 个"
               + (ahead > 0 ? $"，前面还有 {ahead} 个在等" : "") + $"），排队中{spent}"
               + string.Concat(DescribeAll(busy, now).Select(line => "\n        " + line));
    }

    internal static string HeadroomLine(HeadroomShort shortfall, DateTimeOffset now, string title = MachineTitle,
        string? budget = null)
    {
        string idle = shortfall.Idle is double free ? $"空闲 {free:0.0} 核，" : "";
        return $"[{title}] 等 P 核余量{(budget == null ? "" : $"（{budget}）")}：{idle}需 {shortfall.Need:0.#} 核"
               + (shortfall.Ahead > 0 ? $"，前面还有 {shortfall.Ahead} 个在等" : "")
               + string.Concat(DescribeAll(shortfall.Busy, now, t => now - t.AdmittedAt < Ramp)
                   .Select(line => "\n        " + line));
    }

    internal static string ContinueLine(TimeSpan waited, HeadroomShort? shortfall, int timeoutMinutes) =>
        "\t" + Budget(waited, timeoutMinutes)
        + (shortfall?.Idle is double free ? $"，空闲 {free:0.0} 核" : "");

    internal static string Budget(TimeSpan waited, int timeoutMinutes) =>
        $"已等 {waited.TotalSeconds:0}s，" + (timeoutMinutes > 0 ? $"排队上限 {timeoutMinutes} 分钟" : "排队不设上限");

    public List<string> HolderLines(DateTimeOffset now)
    {
        List<Ticket> tickets = LiveTickets();
        return
        [
            .. DescribeAll(tickets.OrderBy(t => t.Mode switch { Exclusive => 0, Shared => 1, AwaitExclusive => 2, _ => 3 })
                .ThenBy(t => t.Since), now),
        ];
    }

    public List<Ticket> LiveHolderTickets() => LiveTickets();

    private static string State(string mode) => mode is AwaitShared or AwaitExclusive ? "排队中" : "正在执行";

    internal static string ModeName(string mode) => mode switch
    {
        Shared => "共享",
        Exclusive => "独占",
        AwaitShared => "等共享",
        AwaitExclusive => "等独占",
        _ => mode,
    };

    internal static IEnumerable<string> DescribeAll(IEnumerable<Ticket> tickets, DateTimeOffset now,
        Func<Ticket, bool>? ramping = null)
    {
        List<Ticket> all = [.. tickets];
        var folded = new HashSet<(string, string, string)>();
        foreach (Ticket ticket in all)
        {
            if (ticket.Group == null)
            {
                yield return Describe(ticket, now, ramping?.Invoke(ticket) == true ? "起步中预扣 1 核" : null);
                continue;
            }

            if (!folded.Add((ticket.Group, ticket.Mode, ticket.Label))) continue;
            List<Ticket> members =
                [.. all.Where(t => t.Group == ticket.Group && t.Mode == ticket.Mode && t.Label == ticket.Label)];
            DateTimeOffset since = members.Min(t => t.Since);
            int ramp = ramping == null ? 0 : members.Count(ramping);
            yield return $"{ModeName(ticket.Mode)} {ticket.Label} · 分片组 {GroupTail(ticket.Group)} · {members.Count} 片"
                         + $"（{State(ticket.Mode)}，{since.ToLocalTime():HH:mm:ss} 起，{(int)Math.Max(0, (now - since).TotalSeconds)}s"
                         + (ramp > 0 ? $"，起步中预扣 {ramp} 核" : "") + "）";
        }
    }

    internal static string GroupTail(string group) =>
        group.Length > 9 && group[8] == '-' && group.Take(8).All(char.IsAsciiDigit) ? group[9..] : group;

    internal static string Describe(Ticket ticket, DateTimeOffset now, string? note = null) =>
        $"{ModeName(ticket.Mode)} {ticket.Label}"
        + (ticket.Cases.Count > 0 ? $" · {string.Join("、", ticket.Cases)}" : "")
        + $"（{State(ticket.Mode)}，{ticket.Since.ToLocalTime():HH:mm:ss} 起，{(int)Math.Max(0, (now - ticket.Since).TotalSeconds)}s"
        + (note != null ? $"，{note}" : "") + "）";

    internal static bool LateConflicts(IReadOnlyList<Coordinator.Row> rows, Coordinator.Row late)
    {
        Ticket? me = Parse(late.Payload);
        if (me == null || me.Mode is not (Shared or Exclusive)) return false;
        List<Ticket> held =
        [
            .. rows.Where(row => row.Id != late.Id).Select(row => Parse(row.Payload)).OfType<Ticket>()
                .Where(t => t.Mode is Shared or Exclusive),
        ];
        if (me.Mode == Exclusive) return held.Count > 0;
        return held.Any(t => t.Mode == Exclusive) || (me.Cap > 0 && held.Count >= me.Cap);
    }

    private static Ticket? Parse(string payload) => JsonSerializer.Deserialize<Ticket>(payload, Options);

    private IDisposable Lock() => Leases.Lock(Table, LockTimeout, $"{title}临界区");

    private List<Ticket> LiveTickets() =>
        [.. Leases.List(Table).Select(row => Parse(row.Payload)).OfType<Ticket>()];

    private void Write(Ticket ticket) => Leases.Put(Table, ticket.Id, JsonSerializer.Serialize(ticket, Options));

    private void Delete(string id) => Leases.Delete(Table, id);
}
