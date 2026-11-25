using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimRunnerCli;

public sealed record EngineReport
{
    public static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        RespectNullableAnnotations = true,
    };

    [JsonPropertyName(RimRunner.RimRunnerWireKeys.Report.Schema)]
    public required int Schema { get; init; } = RimRunner.RimRunnerWireKeys.Report.SchemaVersion;

    [JsonPropertyName("command")]
    public required string Command { get; init; }

    [JsonPropertyName("runId")]
    public required string RunId { get; init; }

    [JsonPropertyName("modList")]
    public required string? ModList { get; init; }

    [JsonPropertyName("textureTier")]
    public required string TextureTier { get; init; }

    [JsonPropertyName("ledgerFile")]
    public required string? LedgerFile { get; init; }

    [JsonPropertyName("seenLogsFile")]
    public required string? SeenLogsFile { get; init; }

    [JsonPropertyName(RimRunner.RimRunnerWireKeys.Report.Borrowed)]
    public required Dictionary<string, List<string>> Borrowed { get; init; } = [];

    [JsonPropertyName(RimRunner.RimRunnerWireKeys.Report.Inputs)]
    public required List<EngineReportInput> Inputs { get; init; } = [];

    [JsonPropertyName(RimRunner.RimRunnerWireKeys.Report.InputsUnread)]
    public required List<string> InputsUnread { get; init; } = [];

    [JsonPropertyName(RimRunner.RimRunnerWireKeys.Report.AssemblyMvids)]
    public required Dictionary<string, string> AssemblyMvids { get; init; }

    [JsonPropertyName(RimRunner.RimRunnerWireKeys.Report.ProductCapabilities)]
    public required List<string>? ProductCapabilities { get; init; }

    [JsonPropertyName(RimRunner.RimRunnerWireKeys.Report.ContentFingerprint)]
    public required string? ContentFingerprint { get; init; }

    [JsonPropertyName("regressions")]
    public List<EngineReportCase>? Regressions { get; init; }

    [JsonPropertyName("readouts")]
    public List<EngineReportCase>? Readouts { get; init; }

    [JsonPropertyName(RimRunner.RimRunnerWireKeys.Report.AmbientWindows)]
    public required List<string> AmbientWindows { get; init; }

    [JsonPropertyName(RimRunner.RimRunnerWireKeys.Report.AmbientWindowsFresh)]
    public required List<string> AmbientWindowsFresh { get; init; }

    [JsonPropertyName(RimRunner.RimRunnerWireKeys.Report.CustomSave)]
    public required string? CustomSave { get; init; }

    [JsonPropertyName(RimRunner.RimRunnerWireKeys.Report.CustomSaveNotes)]
    public required List<string> CustomSaveNotes { get; init; }

    [JsonPropertyName(RimRunner.RimRunnerWireKeys.Report.MemoryStart)]
    public required Dictionary<string, long> MemoryStart { get; init; }

    [JsonPropertyName(RimRunner.RimRunnerWireKeys.Report.MemoryEnd)]
    public required Dictionary<string, long> MemoryEnd { get; init; }

    [JsonPropertyName("faultReloads")]
    public required int FaultReloads { get; init; }

    [JsonPropertyName("mapReloads")]
    public required int MapReloads { get; init; }

    [JsonPropertyName("checkpointRetries")]
    public required List<string> CheckpointRetries { get; init; }

    [JsonPropertyName("breakerObservations")]
    public required int BreakerObservations { get; init; }

    [JsonPropertyName("breakerTrips")]
    public required int BreakerTrips { get; init; }

    [JsonPropertyName("breakerPumpThreshold")]
    public required int BreakerPumpThreshold { get; init; }

    [JsonPropertyName("breakerErrorThreshold")]
    public required int BreakerErrorThreshold { get; init; }

    [JsonPropertyName("excludedCases")]
    public required List<string> ExcludedCases { get; init; }

    [JsonPropertyName(RimRunner.RimRunnerWireKeys.Report.EnvChecksClean)]
    public required bool? EnvChecksClean { get; init; }

    [JsonPropertyName(RimRunner.RimRunnerWireKeys.Report.InapplicableCases)]
    public required Dictionary<string, string> InapplicableCases { get; init; }

    [JsonPropertyName(RimRunner.RimRunnerWireKeys.Report.GroupSkippedCases)]
    public required List<string> GroupSkippedCases { get; init; } = [];

    [JsonPropertyName(RimRunner.RimRunnerWireKeys.Report.AbsentCases)]
    public required Dictionary<string, string> AbsentCases { get; init; }

    [JsonPropertyName("foreignStartupErrors")]
    public required List<string> ForeignStartupErrors { get; init; }

    [JsonPropertyName("claimedStartupErrors")]
    public required List<string> ClaimedStartupErrors { get; init; }

    [JsonPropertyName(RimRunner.RimRunnerWireKeys.Report.UnattributedStartupErrors)]
    public required List<string> UnattributedStartupErrors { get; init; }

    [JsonPropertyName(RimRunner.RimRunnerWireKeys.Report.Rejections)]
    public required List<EngineReportRejection> Rejections { get; init; }

    [JsonPropertyName(RimRunner.RimRunnerWireKeys.Report.CaseRejections)]
    public required List<EngineReportRejection> CaseRejections { get; init; } = [];

    [JsonPropertyName(RimRunner.RimRunnerWireKeys.Report.OutOfBatchFaults)]
    public required List<EngineReportRejection> OutOfBatchFaults { get; init; } = [];

    [JsonPropertyName(RimRunner.RimRunnerWireKeys.Report.Warnings)]
    public required List<EngineReportFinding> Warnings { get; init; } = [];

    [JsonPropertyName(RimRunner.RimRunnerWireKeys.Report.WarningsAcked)]
    public required List<EngineReportFinding> WarningsAcked { get; init; } = [];

    [JsonPropertyName("acks")]
    public required List<EngineReportAck> Acks { get; init; }

    [JsonPropertyName(RimRunner.RimRunnerWireKeys.Report.LedgerFaults)]
    public required List<string> LedgerFaults { get; init; }

    [JsonPropertyName("engineFaults")]
    public required List<EngineReportEngineFault> EngineFaults { get; init; }

    [JsonPropertyName("batchSentinels")]
    public required List<EngineReportFinding> BatchSentinels { get; init; }

    [JsonPropertyName("outputFaults")]
    public required List<EngineReportFinding> OutputFaults { get; init; }

    [JsonPropertyName("reloadErrors")]
    public required List<string> ReloadErrors { get; init; }

    [JsonPropertyName("gapErrors")]
    public required List<EngineReportFinding> GapErrors { get; init; }

    [JsonPropertyName("gapErrorCount")]
    public required int GapErrorCount { get; init; }

    [JsonPropertyName("logCapHits")]
    public required List<EngineReportFinding> LogCapHits { get; init; }

    [JsonPropertyName("experiments")]
    public required string Experiments { get; init; }

    [JsonPropertyName("compatOff")]
    public required string CompatOff { get; init; }

    [JsonPropertyName("mainMap")]
    public required List<string> MainMap { get; init; }

    [JsonPropertyName("forcedMainMap")]
    public required List<string> ForcedMainMap { get; init; }

    [JsonPropertyName(RimRunner.RimRunnerWireKeys.Report.ComponentFirstFills)]
    public required Dictionary<string, string>? ComponentFirstFills { get; init; }

    [JsonPropertyName("contractScanMs")]
    public required long ContractScanMs { get; init; }

    [JsonPropertyName("durationMs")]
    public required long DurationMs { get; init; }

    [JsonPropertyName("reloadMs")]
    public required long ReloadMs { get; init; }

    [JsonPropertyName("reloadCount")]
    public required int ReloadCount { get; init; }

    public bool HasAnySection =>
        Regressions is not null || Readouts is not null;

    public IReadOnlyList<EngineReportCase> AdjudicatingCases => Regressions ?? [];

    public IReadOnlyList<EngineReportCase> DiagnosticCases => Readouts ?? [];

    public IReadOnlyList<EngineReportCase> AllCases => [.. AdjudicatingCases, .. DiagnosticCases];
}

public sealed class EngineReportError
{
    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("stack")]
    public required string? Stack { get; init; }
}

public sealed class EngineReportFinding
{
    [JsonPropertyName("subject")]
    public required string? Subject { get; init; }

    [JsonPropertyName("key")]
    public required string? Key { get; init; }

    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("fresh")]
    public bool? Fresh { get; init; }
}

public sealed class EngineReportAck
{
    [JsonPropertyName("subject")]
    public required string? Subject { get; init; }

    public const string StartupKind = "startup";
    public const string KnownFailingKind = "knownFailing";

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("target")]
    public required string Target { get; init; }

    [JsonPropertyName("origin")]
    public required string? Origin { get; init; }

    [JsonPropertyName("anchor")]
    public required string? Anchor { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    [JsonPropertyName("hits")]
    public required int Hits { get; init; }

    [JsonPropertyName("tolerance")]
    public required int Tolerance { get; init; }

    [JsonPropertyName("misses")]
    public required int? Misses { get; init; }

    [JsonPropertyName("unreachable")]
    public required string? Unreachable { get; init; }
}

public sealed class EngineReportEngineFault
{
    [JsonPropertyName("at")]
    public required string At { get; init; }

    [JsonPropertyName("caseId")]
    public required string? CaseId { get; init; }

    [JsonPropertyName("where")]
    public required string Where { get; init; }

    [JsonPropertyName("text")]
    public required string Text { get; init; }
}

public sealed class EngineReportInput
{
    [JsonPropertyName(RimRunner.RimRunnerWireKeys.Report.InputPath)]
    public required string Path { get; init; }

    [JsonPropertyName(RimRunner.RimRunnerWireKeys.Report.InputSource)]
    public required string Source { get; init; }

    [JsonPropertyName(RimRunner.RimRunnerWireKeys.Report.InputHash)]
    public required string Hash { get; init; }

    [JsonPropertyName(RimRunner.RimRunnerWireKeys.Report.InputCases)]
    public required List<string> Cases { get; init; } = [];
}

public sealed class EngineReportRejection
{
    [JsonPropertyName("gate")]
    public required string Gate { get; init; }

    [JsonPropertyName("subject")]
    public required string? Subject { get; init; }

    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    [JsonPropertyName("text")]
    public required string Text { get; init; }
}

public sealed record EngineReportCase
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("errorSummary")]
    public required string? ErrorSummary { get; init; }

    [JsonPropertyName("passRate10")]
    public required string? PassRate10 { get; init; }

    [JsonPropertyName("outcomeTail30")]
    public required string? OutcomeTail30 { get; init; }

    [JsonPropertyName("saveChangedSincePass")]
    public required bool SaveChangedSincePass { get; init; }

    [JsonPropertyName("onMainMap")]
    public required bool OnMainMap { get; init; }

    [JsonPropertyName("onParallelSite")]
    public required bool OnParallelSite { get; init; }

    [JsonPropertyName("durationMs")]
    public required long DurationMs { get; init; }

    [JsonPropertyName("checkpointMs")]
    public required long CheckpointMs { get; init; }

    [JsonPropertyName("otherBusyCores")]
    public required double? OtherBusyCores { get; init; }

    [JsonPropertyName("otherBusyWindowMs")]
    public required long OtherBusyWindowMs { get; init; }

    [JsonPropertyName("asserts")]
    public required List<EngineReportAssert> Asserts { get; init; }

    [JsonPropertyName("diagnostics")]
    public required List<EngineReportDiagnostic> Diagnostics { get; init; }

    [JsonPropertyName("failureFingerprints")]
    public required List<EngineReportFinding> FailureFingerprints { get; init; }

    [JsonPropertyName("artifacts")]
    public required List<EngineReportArtifact> Artifacts { get; init; }

    [JsonPropertyName("screenshots")]
    public required List<EngineReportScreenshot> Screenshots { get; init; }

    [JsonPropertyName("readoutTexts")]
    public required List<EngineReportReadoutText> ReadoutTexts { get; init; }

    [JsonPropertyName("expectedLogs")]
    public required List<string> ExpectedLogs { get; init; }

    [JsonPropertyName("expectedLogsFresh")]
    public required List<string> ExpectedLogsFresh { get; init; }

    [JsonPropertyName("logAcks")]
    public required List<EngineReportLogAck> LogAcks { get; init; }

    [JsonPropertyName("newErrors")]
    public required List<EngineReportError> NewErrors { get; init; }

    [JsonPropertyName("sentinelFaults")]
    public required List<EngineReportFinding> SentinelFaults { get; init; }

    [JsonPropertyName("engineFaults")]
    public required List<string> EngineFaults { get; init; }

    [JsonPropertyName(RimRunner.RimRunnerWireKeys.Report.Warnings)]
    public required List<EngineReportFinding> Warnings { get; init; } = [];

    [JsonPropertyName(RimRunner.RimRunnerWireKeys.Report.WarningsAcked)]
    public required List<EngineReportFinding> WarningsAcked { get; init; } = [];

    [JsonPropertyName("newWarnings")]
    public required List<string> NewWarnings { get; init; }

    [JsonPropertyName("foreignErrors")]
    public required List<string> ForeignErrors { get; init; }

    [JsonPropertyName("degradedNotes")]
    public required List<string> DegradedNotes { get; init; }

    [JsonPropertyName("degradedNotesFresh")]
    public required List<string> DegradedNotesFresh { get; init; }
}

public sealed class EngineReportLogAck
{
    [JsonPropertyName("level")]
    public required string Level { get; init; }

    [JsonPropertyName("pattern")]
    public required string Pattern { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    [JsonPropertyName("stackAnchors")]
    public required List<string> StackAnchors { get; init; }

    [JsonPropertyName("hits")]
    public required int Hits { get; init; }

    [JsonPropertyName("lines")]
    public required List<string> Lines { get; init; }

    [JsonPropertyName("linesFresh")]
    public required List<string> LinesFresh { get; init; }

    [JsonPropertyName("tolerance")]
    public required int Tolerance { get; init; }

    [JsonPropertyName("misses")]
    public required int? Misses { get; init; }

    [JsonPropertyName("unreachable")]
    public required string? Unreachable { get; init; }
}

public sealed class EngineReportAssert
{
    [JsonPropertyName("ordinal")]
    public required int Ordinal { get; init; }

    [JsonPropertyName("label")]
    public required string Label { get; init; }

    [JsonPropertyName("expected")]
    public required string Expected { get; init; }

    [JsonPropertyName("actual")]
    public required string Actual { get; init; }

    [JsonPropertyName("pass")]
    public required bool Pass { get; init; }

    [JsonPropertyName("source")]
    public required string? Source { get; init; }

    [JsonPropertyName("explanation")]
    public required string? Explanation { get; init; }

    [JsonPropertyName("offending")]
    public required List<string> Offending { get; init; }
}

public sealed class EngineReportDiagnostic
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("label")]
    public required string Label { get; init; }

    [JsonPropertyName("value")]
    public required string Value { get; init; }

    [JsonPropertyName("gameTick")]
    public required int GameTick { get; init; }
}

public sealed class EngineReportArtifact
{
    [JsonPropertyName("artifactId")]
    public required string? ArtifactId { get; init; }

    [JsonPropertyName("caseId")]
    public required string CaseId { get; init; }

    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("path")]
    public required string? Path { get; init; }

    [JsonPropertyName("error")]
    public required string? Error { get; init; }

    [JsonPropertyName("runId")]
    public required string RunId { get; init; }

    [JsonPropertyName("command")]
    public required string Command { get; init; }

    [JsonPropertyName("frame")]
    public required int Frame { get; init; }

    [JsonPropertyName("completedFrame")]
    public required int CompletedFrame { get; init; }

    [JsonPropertyName("cleanupFrame")]
    public required int CleanupFrame { get; init; }
}

public sealed class EngineReportScreenshot
{
    [JsonPropertyName("caption")]
    public required string Caption { get; init; }

    [JsonPropertyName("path")]
    public required string? Path { get; init; }

    [JsonPropertyName("artifactId")]
    public required string? ArtifactId { get; init; }

    [JsonPropertyName("error")]
    public required string? Error { get; init; }

    [JsonPropertyName("note")]
    public required string? Note { get; init; }
}

public sealed class EngineReportReadoutText
{
    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("lines")]
    public required int Lines { get; init; }

    [JsonPropertyName("bytes")]
    public required long Bytes { get; init; }
}
