using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimRunnerCli;

public sealed class AdmissionLedger(string root, Func<int, DateTimeOffset?, bool>? gameAlive = null)
{
    public sealed class Ticket
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("number")] public int Number { get; set; }
        [JsonPropertyName("gamePid")] public int? GamePid { get; set; }
        [JsonPropertyName("gameStartedAt")] public DateTimeOffset? GameStartedAt { get; set; }
        [JsonPropertyName("key")] public string Key { get; set; } = "";
        [JsonPropertyName("label")] public string Label { get; set; } = "";
        [JsonPropertyName("expected")] public long Expected { get; set; }
        [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; set; }
        [JsonPropertyName("startupLoadedAt")] public DateTimeOffset? StartupLoadedAt { get; set; }
        [JsonPropertyName("loadedAt")] public DateTimeOffset? LoadedAt { get; set; }
        [JsonPropertyName("skipHistory")] public bool SkipHistory { get; set; }
        [JsonPropertyName("memoryKey")] public string? MemoryKey { get; set; }
        [JsonPropertyName("memoryExpected")] public long MemoryExpected { get; set; }
    }

    public sealed class QueueEntry
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("number")] public int Number { get; set; }
        [JsonPropertyName("enqueuedAt")] public DateTimeOffset EnqueuedAt { get; set; }
        [JsonPropertyName("need")] public long Need { get; set; }
        [JsonPropertyName("label")] public string Label { get; set; } = "";
        [JsonPropertyName("waiting")] public string? Waiting { get; set; }
    }

    public sealed class Peak
    {
        [JsonPropertyName("gamePid")] public int GamePid { get; set; }
        [JsonPropertyName("startedAt")] public DateTimeOffset StartedAt { get; set; }
        [JsonPropertyName("bytes")] public long Bytes { get; set; }
    }

    public sealed class ModelEstimate
    {
        [JsonPropertyName("ids")] public string Ids { get; set; } = "";
        [JsonPropertyName("bytes")] public long Bytes { get; set; }
    }

    public sealed class History
    {
        [JsonPropertyName("peaks")] public Dictionary<string, List<Peak>> Peaks { get; set; } = [];
        [JsonPropertyName("models")] public Dictionary<string, ModelEstimate> Models { get; set; } = [];
        [JsonPropertyName("memoryPeaks")] public Dictionary<string, List<Peak>> MemoryPeaks { get; set; } = [];
        [JsonPropertyName("sources")] public Dictionary<string, List<string>> Sources { get; set; } = [];

        [JsonIgnore] public string? Unreadable { get; init; }
    }

    public const int PeaksKept = 5;

    public static readonly TimeSpan SettleTime = TimeSpan.FromMinutes(3);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Func<int, DateTimeOffset?, bool> gameAlive = gameAlive ?? ProcessIdentity.GameAlive;

    public string Root { get; } = root;

    private string Scope => "admission:" + PathIdentity.Normalize(Root).ToUpperInvariant();
    private string TicketTable => Scope + ":inflight";
    private string QueueTable => Scope + ":queue";
    private string LoaderTable => Scope + ":loading";

    public static AdmissionLedger ForClaimRoot(string claimRoot) =>
        new(Path.Combine(Path.GetDirectoryName(CommandContext.FullPath(claimRoot)) ?? claimRoot, "vram"));

    private string HistoryPath => Path.Combine(Root, "history.json");

    public IDisposable Lock(TimeSpan timeout) => Leases.Lock(Scope, timeout, "准入账锁");

    public List<Ticket> LiveTickets() => LiveTickets(out _);

    public List<Ticket> LiveTickets(out int unreadable)
    {
        var live = new List<Ticket>();
        unreadable = 0;
        foreach (Coordinator.Row row in Leases.List(TicketTable))
        {
            if (Parse<Ticket>(row.Payload) is not { } ticket)
            {
                unreadable++;
                continue;
            }

            if (ticket.GamePid is int pid && !gameAlive(pid, ticket.GameStartedAt))
            {
                Leases.Delete(TicketTable, row.Id);
                continue;
            }

            live.Add(ticket);
        }

        return
        [
            .. live.GroupBy(ticket => ticket.Number)
                .Select(group => group.OrderByDescending(ticket => ticket.CreatedAt).ThenBy(ticket => ticket.Id, StringComparer.Ordinal).First()),
        ];
    }

    public bool Loading(Ticket ticket) =>
        ticket.StartupLoadedAt == null && ticket.LoadedAt == null
        && Leases.List(LoaderTable).Any(row => row.Id == ticket.Id);

    public bool LoadedByMe(Ticket ticket) =>
        Leases.List(LoaderTable).Any(row => row.Id == ticket.Id && Leases.IsMine(row.Owner));

    public void Open(Ticket ticket)
    {
        Leases.Put(TicketTable, ticket.Id, Serialize(ticket));
        Leases.Put(LoaderTable, ticket.Id, "");
    }

    public void WriteTicket(Ticket ticket) => Leases.Put(TicketTable, ticket.Id, Serialize(ticket));

    public void Adopt(Ticket ticket)
    {
        Leases.Delete(TicketTable, ticket.Id);
        Leases.PutForExecutor(TicketTable, ticket.Id, Serialize(ticket));
    }

    public void EndLoading(Ticket ticket) => Leases.Delete(LoaderTable, ticket.Id);

    public void DeleteTicket(Ticket ticket)
    {
        Leases.Delete(TicketTable, ticket.Id);
        Leases.Delete(LoaderTable, ticket.Id);
    }

    public List<QueueEntry> LiveQueue() => LiveQueue(out _);

    public List<QueueEntry> LiveQueue(out int unreadable)
    {
        List<QueueEntry?> parsed = [.. Leases.List(QueueTable).Select(row => Parse<QueueEntry>(row.Payload))];
        unreadable = parsed.Count(entry => entry == null);
        return
        [
            .. parsed.OfType<QueueEntry>()
                .OrderBy(entry => entry.EnqueuedAt).ThenBy(entry => entry.Id, StringComparer.Ordinal),
        ];
    }

    public void Enqueue(QueueEntry entry) => Leases.Put(QueueTable, entry.Id, Serialize(entry));

    public void Dequeue(string id) => Leases.Delete(QueueTable, id);

    public History ReadHistory()
    {
        switch (RimTooling.IO.PathPresence.Of(HistoryPath))
        {
            case RimTooling.IO.PathPresenceKind.Absent:
                return new History();
            case RimTooling.IO.PathPresenceKind.Unknown:
                return new History { Unreadable = "查询文件失败" };
        }

        try
        {
            return JsonSerializer.Deserialize<History>(File.ReadAllText(HistoryPath), Options)
                   ?? new History { Unreadable = "内容为 null" };
        }
        catch (JsonException exception)
        {
            return new History { Unreadable = "不是合法 JSON：" + exception.Message };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new History { Unreadable = exception.Message };
        }
    }

    public void WriteHistory(History history)
    {
        if (history.Unreadable != null)
        {
            string aside = $"{HistoryPath}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}";
            try
            {
                File.Move(HistoryPath, aside);
                CliLog.Warn($"[准入] 准入历史无法读取（{history.Unreadable}），原件已改名为 {aside}，从空历史重记");
            }
            catch (FileNotFoundException)
            {
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                CliLog.Warn($"[准入] 准入历史无法读取（{history.Unreadable}），也无法改名留证（{exception.Message}），本次不写");
                return;
            }
        }

        Prune(history, path => RimTooling.IO.PathPresence.Of(path) != RimTooling.IO.PathPresenceKind.Absent);
        PruneRetiredTiers(history);
        WriteJson(HistoryPath, history);
    }

    public static string SourceKey(string key)
    {
        int first = key.IndexOf('|');
        int second = first < 0 ? -1 : key.IndexOf('|', first + 1);
        return second < 0 ? key : key[..second];
    }

    public static bool RecordSource(History history, string sourceKey, IEnumerable<string> paths)
    {
        if (!history.Sources.TryGetValue(sourceKey, out List<string>? known))
            history.Sources[sourceKey] = known = [];
        bool changed = false;
        foreach (string path in paths.Select(CommandContext.FullPath))
        {
            if (known.Any(existing => PathIdentity.Same(existing, path))) continue;
            known.Add(path);
            changed = true;
        }

        return changed;
    }

    public static void Prune(History history, Func<string, bool> exists)
    {
        foreach ((string sourceKey, List<string> paths) in history.Sources.ToList())
        {
            paths.RemoveAll(path => !exists(path));
            if (paths.Count > 0) continue;
            history.Sources.Remove(sourceKey);
            foreach (Dictionary<string, List<Peak>> table in new[] { history.Peaks, history.MemoryPeaks })
                foreach (string key in table.Keys.Where(key => SourceKey(key) == sourceKey).ToList())
                    table.Remove(key);
            foreach (string key in history.Models.Keys.Where(key => SourceKey(key) == sourceKey).ToList())
                history.Models.Remove(key);
        }
    }

    public static void PruneRetiredTiers(History history)
    {
        static bool Retired(string key) => key.Split('|') is { Length: >= 4 } parts && TextureTierPolicy.Rank(parts[3]) < 0;
        foreach (Dictionary<string, List<Peak>> table in new[] { history.Peaks, history.MemoryPeaks })
            foreach (string key in table.Keys.Where(Retired).ToList())
                table.Remove(key);
        foreach (string key in history.Models.Keys.Where(Retired).ToList())
            history.Models.Remove(key);
    }

    public static (long Bytes, int Count)? FromHistory(History history, string key) => Max(history.Peaks, key);

    public static (long Bytes, int Count)? FromMemoryHistory(History history, string key) => Max(history.MemoryPeaks, key);

    public static (long Bytes, int Count)? FromOtherGamesMemory(History history, string key)
    {
        string lineage = Lineage(key);
        List<Peak> peaks =
        [
            .. history.MemoryPeaks.Where(pair => pair.Key != key && Lineage(pair.Key) == lineage)
                .SelectMany(pair => pair.Value),
        ];
        return peaks.Count > 0 ? (peaks.Max(peak => peak.Bytes), peaks.Count) : null;
    }

    private static (long Bytes, int Count)? Max(Dictionary<string, List<Peak>> table, string key) =>
        table.TryGetValue(key, out List<Peak>? peaks) && peaks.Count > 0
            ? (peaks.Max(peak => peak.Bytes), peaks.Count)
            : null;

    public const double MemoryHighRatio = 1.5;
    public const long MemoryHighMarginBytes = 2_000_000_000;

    public static (long Peak, int Instances)? MemoryHigh(History history, Ticket ticket, long bytes)
    {
        if (ticket.MemoryKey is not { Length: > 0 } key || !history.MemoryPeaks.TryGetValue(key, out List<Peak>? peaks))
            return null;
        List<Peak> others = [.. peaks.Where(p => !(p.GamePid == ticket.GamePid && p.StartedAt == ticket.CreatedAt))];
        if (others.Count == 0) return null;
        long peak = others.Max(p => p.Bytes);
        return bytes > peak * MemoryHighRatio && bytes - peak > MemoryHighMarginBytes ? (peak, others.Count) : null;
    }

    public static bool ObserveMemory(History history, Ticket ticket, long bytes) =>
        ticket.MemoryKey is { Length: > 0 } key && ticket.GamePid is int pid && bytes > 0
        && Record(history.MemoryPeaks, key, pid, ticket.CreatedAt, bytes);

    public static bool Observe(History history, IEnumerable<Ticket> tickets,
        IReadOnlyDictionary<int, long> processUsed, DateTimeOffset now)
    {
        bool changed = false;
        foreach (Ticket ticket in tickets)
        {
            if (ticket.GamePid is not int pid || ticket.SkipHistory
                || (ticket.LoadedAt == null && now - ticket.CreatedAt < SettleTime)
                || !processUsed.TryGetValue(pid, out long bytes) || bytes <= 0)
                continue;
            changed |= Record(history.Peaks, ticket.Key, pid, ticket.CreatedAt, bytes);
        }

        return changed;
    }

    public static string Lineage(string key)
    {
        string[] parts = key.Split('|');
        return parts.Length <= 4 ? key : string.Join('|', parts.Take(4));
    }

    public static void DropOtherGames<T>(Dictionary<string, T> table, string key)
    {
        if (table.ContainsKey(key)) return;
        string lineage = Lineage(key);
        foreach (string other in table.Keys.Where(other => Lineage(other) == lineage).ToList())
            table.Remove(other);
    }

    private static bool Record(Dictionary<string, List<Peak>> table, string key, int pid, DateTimeOffset startedAt,
        long bytes)
    {
        DropOtherGames(table, key);
        if (!table.TryGetValue(key, out List<Peak>? peaks))
            table[key] = peaks = [];
        Peak? peak = peaks.FirstOrDefault(p => p.GamePid == pid && p.StartedAt == startedAt);
        if (peak == null)
        {
            peaks.Add(new Peak { GamePid = pid, StartedAt = startedAt, Bytes = bytes });
            peaks.Sort((a, b) => a.StartedAt.CompareTo(b.StartedAt));
            if (peaks.Count > PeaksKept) peaks.RemoveRange(0, peaks.Count - PeaksKept);
            return true;
        }

        if (bytes <= peak.Bytes) return false;
        peak.Bytes = bytes;
        return true;
    }

    public sealed record Pending(Ticket Ticket, long Current)
    {
        public long Remaining => Math.Max(0, Ticket.Expected - Current);
    }

    public sealed record Decision(bool Admit, bool Impossible, long Available, long PendingTotal);

    public static Decision Decide(long need, long total, long adapterUsed, long rimWorldUsed,
        IReadOnlyList<Pending> pending, long headroom, bool aheadInQueue) =>
        Decide(need, total, adapterUsed, rimWorldUsed, pending.Sum(p => p.Remaining), headroom, aheadInQueue);

    public static Decision Decide(long need, long total, long used, long rimWorldUsed,
        long pendingTotal, long headroom, bool aheadInQueue)
    {
        long available = total - used - pendingTotal - headroom;
        long external = Math.Max(0, used - rimWorldUsed);
        bool impossible = need > total - headroom - external;
        return new Decision(!impossible && !aheadInQueue && need <= available, impossible, available, pendingTotal);
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    private static T? Parse<T>(string payload) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(payload, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        RimTooling.IO.AtomicFile.Write(path, JsonSerializer.Serialize(value, Options));
    }
}
