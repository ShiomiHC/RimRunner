using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimRunnerCli;

public sealed class ClaimRecord
{
    [JsonPropertyName("number")] public int Number { get; set; }
    [JsonPropertyName("treeName")] public string TreeName { get; set; } = "";
    [JsonPropertyName("shardOrdinal")] public int? ShardOrdinal { get; set; }
    [JsonPropertyName("product")] public string Product { get; set; } = "";
    [JsonPropertyName("productRoot")] public string ProductRoot { get; set; } = "";
    [JsonPropertyName("mainProductRoot")] public string? MainProductRoot { get; set; }
    [JsonPropertyName("state")] public string State { get; set; } = "";
    [JsonPropertyName("modsFolder")] public string ModsFolder { get; set; } = "";
    [JsonPropertyName("saveData")] public string SaveData { get; set; } = "";
    [JsonPropertyName("packageIds")] public List<string> PackageIds { get; set; } = [];
    [JsonPropertyName("loadDirs")] public List<string>? LoadDirs { get; set; }
    [JsonPropertyName("wrappers")] public List<string>? Wrappers { get; set; }
    [JsonPropertyName("testMod")] public string? TestMod { get; set; }
    [JsonPropertyName("purpose")] public string? Purpose { get; set; }
    [JsonPropertyName("stoppedAt")] public DateTimeOffset? StoppedAt { get; set; }
    [JsonPropertyName("stopReason")] public string? StopReason { get; set; }

    public const string StatePreparing = "preparing";
    public const string StateReady = "ready";
    public const string PurposeBatch = RimRunner.RimRunnerInstanceMode.Batch;
    public const string PurposeShowcase = RimRunner.RimRunnerInstanceMode.Showcase;
    public const string PurposeFilm = RimRunner.RimRunnerInstanceMode.Film;
    public const string PurposeShard = "shard";
    public const string PurposeSpare = "spare";
    public const string PurposeNoCompat = "nocompat";

    [JsonIgnore] public InstancePurpose PurposeKind => PurposePolicies.ParseEffective(Purpose);

    [JsonIgnore] public PurposePolicy Policy => PurposePolicies.For(PurposeKind);

    [JsonIgnore] public string EffectivePurpose => Policy.WireName;

    [JsonIgnore] public bool OnWorktree => MainProductRoot != null;

    [JsonIgnore] public bool ProcessScoped => Policy.ProcessScoped;

    [JsonIgnore] public bool MainTreeResident => !OnWorktree && !Policy.ProcessScoped;

    [JsonIgnore] public string PurposeSegment => Policy.AllowsMultiplePerTree
        ? $"{Policy.WireName}{ShardOrdinal}"
        : Policy.WireName;

    [JsonIgnore] public string DisplayName => $"{TreeName}/{PurposeSegment}";

    [JsonIgnore] public string Noun => Policy.Noun;

    public void Validate(string path)
    {
        var missing = new List<string>();
        foreach ((string value, string field) in new[]
                 {
                     (ProductRoot, "productRoot"), (TreeName, "treeName"), (State, "state"),
                     (ModsFolder, "modsFolder"), (SaveData, "saveData"), (Product, "product"),
                 })
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                missing.Add(field);
            }
        }

        if (PurposePolicies.TryForWire(Purpose, out PurposePolicy? parsed) && parsed.AllowsMultiplePerTree && ShardOrdinal == null)
        {
            missing.Add("shardOrdinal");
        }

        if (missing.Count > 0)
        {
            throw new StoreException($"登记字段缺失：{string.Join(", ", missing)}；文件 {path}");
        }

        if (Number < 1)
        {
            throw new StoreException($"登记 number 非法：{Number}；文件 {path}");
        }

        if (ShardOrdinal is < 1)
        {
            throw new StoreException($"登记 shardOrdinal 非法：{ShardOrdinal}；文件 {path}");
        }

        if (State is not StatePreparing and not StateReady)
        {
            throw new StoreException($"登记 state 非法：{State}；文件 {path}");
        }

        if (Purpose != null && !PurposePolicies.TryParse(Purpose, out _))
        {
            throw new StoreException($"登记 purpose 非法：{Purpose}；文件 {path}");
        }
    }
}

public sealed class RefusalException(string message) : StoreException(message)
{
    public static int ExitCodeOf(Exception exception) => ExitPolicy.FromException(exception);
}

public class StoreException(string message, Exception? innerException = null) : Exception(message, innerException)
{
    internal static string Detailed(string head, string tag, IEnumerable<EngineReportFinding> findings, string unit = "处")
    {
        List<string> screen = [];
        List<string> logOnly = [];
        FindingPresentation.Facts(findings, "", tag, screen, logOnly, indent: "  ", unit: unit);
        logOnly.ForEach(CliLog.Info);
        return head + string.Concat(screen.Select(line => "\n" + line));
    }

    internal static EngineReportFinding Fact(string text, string? subject = null) =>
        new() { Key = null, Subject = subject, Text = text };
}

public sealed class ProductRootClaimedException(ClaimRecord existing, string productRoot) : StoreException($"本树已有 {existing.EffectivePurpose} 登记，实例 {existing.DisplayName}：{productRoot}")
{
    public ClaimRecord Existing { get; } = existing;
}

public sealed class ClaimStore(string? root = null, TimeSpan? registerLockTimeout = null,
    Func<IEnumerable<int>>? liveNumbers = null)
{
    public static string DefaultRoot
    {
        get
        {
            return Path.Combine(MachineRoot.Resolve(), "instances");
        }
    }

    private readonly Func<IEnumerable<int>> liveNumbers = liveNumbers ?? GameProcessProbe.ClaimedNumbers;

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly TimeSpan registerLockTimeout = registerLockTimeout ?? TimeSpan.FromSeconds(60);

    public string Root { get; } = root ?? DefaultRoot;

    private string PathFor(int number) => Path.Combine(Root, $"{number}.json");

    public string InstanceDir(int number) => Path.Combine(Root, number.ToString());

    public List<ClaimRecord> All()
    {
        if (!Directory.Exists(Root))
        {
            return [];
        }

        var records = new List<ClaimRecord>();
        foreach (string file in Directory.EnumerateFiles(Root, "*.json"))
        {
            records.Add(ReadFile(file));
        }

        return records;
    }

    public const string GoneName = "清单里已没有的实例";

    public static string Named(int number) => new ClaimStore().Name(number);

    public string Name(int number) => NameOf(number) ?? GoneName;

    public string? NameOf(int number)
    {
        try
        {
            return Read(number)?.DisplayName;
        }
        catch (StoreException)
        {
            return null;
        }
    }

    public bool Has(int number) =>
        RimTooling.IO.PathPresence.Of(PathFor(number)) != RimTooling.IO.PathPresenceKind.Absent;

    public DateTime? WrittenAtUtc(int number)
    {
        string path = PathFor(number);
        return RimTooling.IO.PathPresence.Of(path) == RimTooling.IO.PathPresenceKind.Present
            ? File.GetLastWriteTimeUtc(path)
            : null;
    }

    public ClaimRecord? Read(int number)
    {
        string path = PathFor(number);
        return RimTooling.IO.PathPresence.Of(path) == RimTooling.IO.PathPresenceKind.Absent ? null : ReadFile(path);
    }

    private static ClaimRecord ReadFile(string path)
    {
        ClaimRecord record;
        try
        {
            record = JsonSerializer.Deserialize<ClaimRecord>(
                ReadAllTextWithSharingRetry(path), ReadOptions)
                ?? throw new StoreException($"登记为 null：{path}");
        }
        catch (JsonException exception)
        {
            throw new StoreException($"登记不是合法 JSON：{path}\n  {exception.Message}");
        }

        record.Validate(path);
        return record;
    }

    private static string ReadAllTextWithSharingRetry(string path)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                last = exception;
                Thread.Sleep(25);
            }
        }

        throw new StoreException(
            $"登记读取失败：{path}\n  {last!.Message}");
    }

    public bool TryClaim(ClaimRecord record)
    {
        Directory.CreateDirectory(Root);
        string path = PathFor(record.Number);
        try
        {
            using var stream = new FileStream(
                path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
            writer.Write(JsonSerializer.Serialize(record, WriteOptions));
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public void Write(ClaimRecord record)
    {
        Directory.CreateDirectory(Root);
        string path = PathFor(record.Number);
        record.Validate(path);
        RimTooling.IO.AtomicFile.Write(path, JsonSerializer.Serialize(record, WriteOptions));
    }

    private static readonly TimeSpan SweepLockTimeout = TimeSpan.FromSeconds(60);

    public IDisposable SweepLock() =>
        Leases.Lock(SweepLockSection, SweepLockTimeout, "Mods/ 清扫锁");

    public IDisposable? TrySweepLock()
    {
        try
        {
            return Leases.Lock(SweepLockSection, TimeSpan.Zero, "Mods/ 清扫锁");
        }
        catch (StoreException)
        {
            return null;
        }
    }

    private string SweepLockSection => "mods-sweep:" + LockScope;

    public IDisposable MirrorLock() =>
        Leases.Lock("compat-mirror:" + LockScope, TimeSpan.FromMinutes(5), "兼容关闭镜像锁");

    public IDisposable RegisterLock() =>
        Leases.Lock("register:" + LockScope, registerLockTimeout, "登记锁");

    private string LockScope => PathIdentity.Normalize(Root).ToUpperInvariant();

    internal string SweepSection(int number) => "orphan-sweep:" + LockScope + ":" + number;

    internal IDisposable TreeUnmountLock(string productRoot) =>
        Leases.Lock("unmount-tree:" + LockScope + ":" + PathIdentity.Normalize(productRoot).ToUpperInvariant(),
            TimeSpan.FromMinutes(10), "同树卸载锁");

    public List<ClaimRecord> FindSettled(string productRoot, string purpose)
    {
        List<ClaimRecord> records = FindByProductRoot(productRoot, purpose);
        if (records.All(record => record.State != ClaimRecord.StatePreparing)) return records;
        using (RegisterLock())
        {
            return FindByProductRoot(productRoot, purpose);
        }
    }

    public (ClaimRecord Record, bool Created) ClaimForProductRoot(
        string productRoot, string purpose, Func<int, ClaimRecord> recordForNumber)
    {
        using (RegisterLock())
        {
            List<ClaimRecord> existing = FindByProductRoot(productRoot, purpose);
            if (existing.Count > 1)
                throw new StoreException($"本树存在 {existing.Count} 条 {purpose} 登记，拒绝猜测");
            if (existing.Count == 1) return (existing[0], false);
            int number = NextFreeNumber();
            ClaimRecord record = recordForNumber(number);
            AssignNames(record);
            if (!TryClaim(record))
                throw new StoreException($"实例号 {number} 在分配之后被另一条登记占用");
            return (record, true);
        }
    }

    public int NextFreeNumber()
    {
        var used = new HashSet<int>(All().Select(record => record.Number));
        used.UnionWith(liveNumbers());
        int candidate = 1;
        while (used.Contains(candidate)) candidate++;
        return candidate;
    }

    public void AssignNames(ClaimRecord record)
    {
        List<ClaimRecord> all = All();
        List<ClaimRecord> sameRoot = [.. all.Where(other => PathIdentity.Same(other.ProductRoot, record.ProductRoot))];
        if (record.Policy.AllowsMultiplePerTree)
        {
            var ordinals = new HashSet<int>(sameRoot
                .Where(other => other.PurposeKind == record.PurposeKind && other.ShardOrdinal != null)
                .Select(other => other.ShardOrdinal!.Value));
            int ordinal = 1;
            while (ordinals.Contains(ordinal)) ordinal++;
            record.ShardOrdinal = ordinal;
        }

        if (sameRoot.FirstOrDefault()?.TreeName is { Length: > 0 } kept)
        {
            record.TreeName = kept;
            return;
        }

        var taken = new HashSet<string>(
            all.Where(other => !PathIdentity.Same(other.ProductRoot, record.ProductRoot)).Select(other => other.TreeName),
            StringComparer.OrdinalIgnoreCase);
        string name = TreeScope.TreeName(record.ProductRoot);
        if (taken.Contains(name)) name = record.Product + "/" + name;
        string candidate = name;
        for (int suffix = 2; taken.Contains(candidate); suffix++) candidate = $"{name}-{suffix}";
        record.TreeName = candidate;
    }

    public void Remove(int number)
    {
        string path = PathFor(number);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public List<ClaimRecord> FindByProductRoot(string productRoot)
    {
        return
        [
            .. All()
                .Where(record => !string.IsNullOrWhiteSpace(record.ProductRoot) && PathIdentity.Same(record.ProductRoot, productRoot))
        ];
    }

    public List<ClaimRecord> FindByProductRoot(string productRoot, string purpose) =>
        [.. FindByProductRoot(productRoot).Where(record => record.EffectivePurpose == purpose)];
}
