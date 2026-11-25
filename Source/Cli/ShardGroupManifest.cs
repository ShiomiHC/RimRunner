using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimRunnerCli;

public sealed class ShardGroupManifest
{
    public const int CurrentSchema = 2;

    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [JsonPropertyName("schema")] public int? Schema { get; set; }
    [JsonPropertyName("group")] public string Group { get; set; } = "";
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; set; }
    [JsonPropertyName("caseId")] public string CaseId { get; set; } = "";
    [JsonPropertyName("modList")] public string? ModList { get; set; }
    [JsonPropertyName("fingerprint")] public string Fingerprint { get; set; } = "";
    [JsonPropertyName("minShardWeight")] public double MinShardWeight { get; set; }
    [JsonPropertyName("totalWeight")] public double TotalWeight { get; set; }
    [JsonPropertyName("queueCells")] public int QueueCells { get; set; }
    [JsonPropertyName("planned")] public int? Planned { get; set; }
    [JsonPropertyName("seconds")] public double? Seconds { get; set; }
    [JsonPropertyName("unclaimed")] public int? Unclaimed { get; set; }
    [JsonPropertyName("drainedAt")] public DateTimeOffset? DrainedAt { get; set; }
    [JsonPropertyName("claims")] public List<ClaimEntry>? Claims { get; set; }
    [JsonPropertyName("prepares")] public List<PrepareEntry> Prepares { get; set; } = [];
    [JsonPropertyName("consistency")] public ConsistencyEntry Consistency { get; set; } = new();
    [JsonPropertyName("shards")] public List<ShardEntry> Shards { get; set; } = [];
    [JsonPropertyName("undispatched")] public List<UndispatchedEntry> Undispatched { get; set; } = [];
    [JsonPropertyName("ledgerFaults")] public List<string>? LedgerFaults { get; set; }

    public sealed class ConsistencyEntry
    {
        [JsonPropertyName("assemblyMvids")] public Dictionary<string, string> AssemblyMvids { get; set; } = [];
        [JsonPropertyName("contentFingerprint")] public string? ContentFingerprint { get; set; }
    }

    public sealed class ShardEntry
    {
        [JsonPropertyName("index")] public int Index { get; set; }
        [JsonPropertyName("number")] public int Number { get; set; }
        [JsonPropertyName("tag")] public string? Tag { get; set; }
        [JsonPropertyName("cells")] public List<string> Cells { get; set; } = [];
        [JsonPropertyName("weight")] public double Weight { get; set; }
        [JsonPropertyName("runId")] public string? RunId { get; set; }
        [JsonPropertyName("report")] public string? Report { get; set; }
        [JsonPropertyName("exitCode")] public int? ExitCode { get; set; }
        [JsonPropertyName("seconds")] public double? Seconds { get; set; }
    }

    public sealed class ClaimEntry
    {
        [JsonPropertyName("shard")] public int Shard { get; set; }
        [JsonPropertyName("claimedAt")] public DateTimeOffset ClaimedAt { get; set; }
        [JsonPropertyName("doneAt")] public DateTimeOffset? DoneAt { get; set; }
    }

    public sealed class PrepareEntry
    {
        [JsonPropertyName("number")] public int Number { get; set; }
        [JsonPropertyName("decidedAt")] public DateTimeOffset? DecidedAt { get; set; }
        [JsonPropertyName("reused")] public bool? Reused { get; set; }
        [JsonPropertyName("forecast")] public ShardForecast.Record? Forecast { get; set; }
        [JsonPropertyName("readySeconds")] public double? ReadySeconds { get; set; }
        [JsonPropertyName("seconds")] public double Seconds { get; set; }
    }

    public sealed class UndispatchedEntry
    {
        [JsonPropertyName("number")] public int Number { get; set; }
        [JsonPropertyName("tag")] public string? Tag { get; set; }
        [JsonPropertyName("reason")] public string Reason { get; set; } = "";
        [JsonPropertyName("failed")] public bool Failed { get; set; }
        [JsonPropertyName("unplanned")] public bool? Unplanned { get; set; }
        [JsonPropertyName("exitCode")] public int? ExitCode { get; set; }
    }

    [JsonIgnore] public bool Red => Shards.Any(shard => shard.ExitCode != 0) || Undispatched.Any(entry => entry.Failed)
                                    || Unclaimed is > 0 || Claims?.Any(claim => claim.DoneAt == null) == true;

    public static ShardGroupManifest Load(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<ShardGroupManifest>(File.ReadAllText(path), Options)
                   ?? throw new StoreException($"分片组清单为 null：{path}");
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            throw new StoreException($"无法读取分片组清单：{path}（{exception.Message}）");
        }
    }

    public void Save(string path)
    {
        Schema ??= CurrentSchema;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        RimTooling.IO.AtomicFile.Write(path, JsonSerializer.Serialize(this, Options));
    }
}
