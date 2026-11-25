using System.Text.Json;

namespace RimRunnerCli;

public sealed class ShardChildSpec
{
    public string ProductRoot { get; set; } = "";
    public int Number { get; set; }
    public bool Borrowed { get; set; }
    public string? ModList { get; set; }
    public string? CustomSave { get; set; }
    public bool ForceBusy { get; set; }
    public int? TimeoutMinutes { get; set; }
    public int QueueMinutes { get; set; } = QueuePolicy.DefaultMinutes;
    public string? Group { get; set; }
    public int Index { get; set; }
    public int Count { get; set; }
    public string? QueuePath { get; set; }
    internal ShardGroupQueue? Queue { get; set; }
    public double? ColdStartSeconds { get; set; }
    public string? Fingerprint { get; set; }
    internal ShardGroupSink? Sink { get; set; }
    public Dictionary<string, string>? AssemblyMvids { get; set; }
    public string? ContentFingerprint { get; set; }
    public string? LockGroup { get; set; }
    public List<CliBatchWarnings.Entry> BatchWarnings { get; set; } = [];

    public List<string> BatchChecks { get; set; } = [];

    public StructuredRunRequest Batch { get; set; } = StructuredRunRequest.Batch();

    internal CliLog? GroupLog { get; set; }

    internal static string Tag(int number)
    {
        try
        {
            if (new ClaimStore().Read(number) is { ShardOrdinal: int ordinal }) return ClaimRecord.PurposeShard + ordinal;
        }
        catch (StoreException)
        {
        }

        return ClaimStore.Named(number);
    }

    public ShardChildResult Result { get; } = new();

    public bool Dispatch => Group != null;

    public bool QueueDrained() => Queue is { Drained: true };

    public ShardForecast.Record Forecast(DateTime now) =>
        Queue == null
            ? new ShardForecast.Record { ColdStartSeconds = ColdStartSeconds, NoForecast = "规格里没有组队列" }
            : ShardForecast.Judge(Queue.Claims(), Queue.Weights.Sum(), now, ColdStartSeconds);

    public static ShardChildSpec From(string productRoot, StructuredRunRequest request, int number, string? modList,
        bool forceBusy, int? timeoutMinutes, int queueMinutes, bool borrowed = false, string? customSave = null) => new()
    {
        ProductRoot = productRoot,
        Number = number,
        Borrowed = borrowed,
        ModList = modList,
        CustomSave = customSave,
        ForceBusy = forceBusy,
        TimeoutMinutes = timeoutMinutes,
        QueueMinutes = queueMinutes,
        Batch = request.WithoutEnvChecks(),
        BatchWarnings = [.. CliBatchWarnings.Snapshot()],
        BatchChecks = [.. CliBatchWarnings.CheckedSnapshot()],
    };

    public void WriteParams(Utf8JsonWriter writer)
    {
        writer.WriteString(RimRunner.RimRunnerWireKeys.ShardRun.Group, Group);
        writer.WriteNumber(RimRunner.RimRunnerWireKeys.ShardRun.Index, Index);
        writer.WriteNumber(RimRunner.RimRunnerWireKeys.ShardRun.Count, Count);
        writer.WriteString(RimRunner.RimRunnerWireKeys.ShardRun.Queue, QueuePath);
        writer.WriteString(RimRunner.RimRunnerWireKeys.ShardRun.Fingerprint, Fingerprint);
    }
}

public sealed class ShardChildResult
{
    public int ExitCode { get; set; } = ExitCodes.InternalFault;
    public bool? Reused { get; set; }
    public bool QueueDrained { get; set; }
    public string? DrainedGate { get; set; }
    public bool QueueTimedOut { get; set; }
    public string? BorrowRefused { get; set; }
    public ShardForecast.Record? Forecast { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public double? ReadySeconds { get; set; }
    public long MemoryNeed { get; set; }
    public long VramNeed { get; set; }
    public string? ReuseRefused { get; set; }
    public int? Pid { get; set; }
    public string? RunId { get; set; }
    public string? ReportPath { get; set; }
    public ReportValidationResult? Validation { get; set; }
}
