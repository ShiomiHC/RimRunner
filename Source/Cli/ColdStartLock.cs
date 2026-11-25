using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimRunnerCli;

public sealed class ColdStartLock : IDisposable
{
    public sealed class Holder
    {
        [JsonPropertyName("productRoot")] public string ProductRoot { get; set; } = "";
        [JsonPropertyName("startedAt")] public DateTimeOffset StartedAt { get; set; }
        [JsonPropertyName("label")] public string? Label { get; set; }

        [JsonIgnore] internal Coordinator.Owner Owner { get; set; }
    }

    public sealed record Result(ColdStartLock? Lock, Holder? HeldBy);

    private const string TablePrefix = "cold-start:";

    private static readonly TimeSpan ReleaseWait = TimeSpan.FromSeconds(15);

    private readonly string table;
    private readonly string id;

    private ColdStartLock(string table, string id)
    {
        this.table = table;
        this.id = id;
    }

    internal static string TableFor(string claimRoot) => TablePrefix + PathIdentity.Normalize(claimRoot).ToUpperInvariant();

    public static Result TryAcquire(string claimRoot, int number, string productRoot, DateTimeOffset now, string? label = null) =>
        TryAcquireIn(TableFor(claimRoot), number, productRoot, now, label);

    internal static Result TryAcquireIn(string table, int number, string productRoot, DateTimeOffset now, string? label)
    {
        string id = number.ToString();
        string payload = JsonSerializer.Serialize(new Holder { ProductRoot = productRoot, StartedAt = now, Label = label });
        return Leases.Claim(table, id, payload) is { } held
            ? new Result(null, Read(held))
            : new Result(new ColdStartLock(table, id), null);
    }

    public static Holder? AliveHolder(string claimRoot, int number) => HolderIn(TableFor(claimRoot), number);

    internal static Holder? HolderIn(string table, int number)
    {
        string id = number.ToString();
        return Leases.List(table).FirstOrDefault(row => row.Id == id) is { } row ? Read(row) : null;
    }

    public sealed record Owners(Holder? Starting, Holder? Group)
    {
        public bool Any => Starting != null || Group != null;
    }

    public static Owners OwnersOf(string claimRoot, int number)
    {
        Holder? Other(string table) => HolderIn(table, number) is { } holder && !Leases.IsMine(holder.Owner) ? holder : null;
        return new Owners(Other(TableFor(claimRoot)), Other(ShardHold.TableFor(claimRoot)));
    }

    public static bool Held(string claimRoot, int number) =>
        HolderIn(TableFor(claimRoot), number) != null || HolderIn(ShardHold.TableFor(claimRoot), number) != null;

    public static string RefusalLine(string claimRoot, int number, Holder holder, bool bypass = true)
    {
        List<AdmissionLedger.QueueEntry> queue;
        try
        {
            queue = AdmissionLedger.ForClaimRoot(claimRoot).LiveQueue();
        }
        catch (Exception exception) when (exception is StoreException or IOException or UnauthorizedAccessException)
        {
            queue = [];
        }

        return RefusalLine(new ClaimStore(claimRoot).Name(number), number, holder, queue, DateTimeOffset.Now, bypass);
    }

    internal static string RefusalLine(string name, int number, Holder holder, IEnumerable<AdmissionLedger.QueueEntry> queue,
        DateTimeOffset now, bool bypass) =>
        $"[拒绝/冷启动锁] 实例 {name} 正由另一条命令执行前置闸或冷启动："
        + Describe(holder, now, InstanceLiveness.QueuePhase(number, queue, now))
        + (bypass ? "；--force 撤销持锁的命令、停止该实例并冷启动" : "");

    public static string GroupRefusalLine(string name, Holder holder, DateTimeOffset now) =>
        $"[拒绝/分片持有] 实例 {name} 正由分片组持有：" + Describe(holder, now);

    public static string GroupHoldNote(Holder holder, DateTimeOffset now) => "分片组持有：" + Describe(holder, now);

    internal static string Describe(Holder holder, DateTimeOffset now, string? note = null) =>
        $"{holder.Label ?? holder.ProductRoot}（{holder.StartedAt.ToLocalTime():HH:mm:ss} 起，"
        + $"{(int)Math.Max(0, (now - holder.StartedAt).TotalSeconds)}s"
        + (note != null ? $"，{note}" : "") + "）";

    public static bool TakeOver(string claimRoot, int number, Holder holder, Action<string>? log)
    {
        string name = new ClaimStore(claimRoot).Name(number);
        log?.Invoke($"[冷启动锁] 撤销实例 {name} 的持锁命令：{holder.Label ?? holder.ProductRoot}（--force）");
        return RevokeAndWait(TableFor(claimRoot), name, number, holder, revoke: true, "持锁命令", "锁", log);
    }

    internal static bool RevokeAndWait(string table, string name, int number, Holder holder, bool revoke,
        string holderNoun, string leaseNoun, Action<string>? log)
    {
        if (revoke && !CommandRevocation.Revoke(holder.Owner, $"实例 {name} 被另一条带 --force 的命令接管"))
        {
            log?.Invoke($"[FAIL] 实例 {name} 的{holderNoun}无法撤销：{holderNoun}所属的编排进程无应答");
            return false;
        }

        DateTime deadline = DateTime.UtcNow + ReleaseWait;
        while (HolderIn(table, number) is { } current && current.Owner == holder.Owner)
        {
            if (DateTime.UtcNow > deadline)
            {
                log?.Invoke($"[FAIL] 实例 {name} 的{holderNoun}已撤销，{(int)ReleaseWait.TotalSeconds} 秒内{leaseNoun}仍未释放");
                return false;
            }

            Thread.Sleep(50);
        }

        return true;
    }

    private static Holder Read(Coordinator.Row row)
    {
        Holder holder;
        try
        {
            holder = JsonSerializer.Deserialize<Holder>(row.Payload) ?? new Holder();
        }
        catch (JsonException)
        {
            holder = new Holder { ProductRoot = "（锁的记录无法读取）" };
        }

        holder.Owner = row.Owner;
        return holder;
    }

    public void Dispose() => Leases.Delete(table, id);
}
