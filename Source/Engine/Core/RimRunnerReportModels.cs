namespace RimRunner;

public class RimRunnerAssertEntry
{
    public required string label;
    public required string expected;
    public required string actual;
    public bool pass;
    public required string source;
    public string? explanation;
    public int ordinal;
    public List<string> offending = [];
    internal string sceneKind = RimRunnerLiveContract.SceneFinding.Assert;

    internal string ActualInFull => offending.Count == 0 ? actual : actual + "：" + string.Join("；", offending);
}

public class RimRunnerDiagnosticEntry
{
    public required string kind;
    public required string label;
    public required string value;
    public int gameTick;
    internal bool engineAuthored;
}

public class RimRunnerScreenshotEntry
{
    public required string caption;
    public string? path;
    public string? artifactId;
    public string? error;
    public string? note;
}

public class RimRunnerReadoutTextEntry
{
    public required string title;
    public required string path;
    public int lines;
    public long bytes;
}

public class RimRunnerArtifactEntry
{
    public required string kind;
    public string? artifactId;
    public required string state;
    public required string? label;
    public string? fileName;
    public string? path;
    public string? error;
    public required string? runId;
    public required string? command;
    public required string? caseId;
    public string? targetId;
    public int gameTick;
    public int frame;
    public int completedFrame;
    public int cleanupFrame;
    public int width;
    public int height;
    public int screenWidth;
    public int screenHeight;
    public float uiScale;
    public int mapId;
    public string? cameraPosition;
    public string? cameraZoom;
}

public class RimRunnerResultEntry
{
    public required string id;
    public string? status;
    public long durationMs;
    public long checkpointMs;
    public string? frameSpeed;
    public int ticksAdvanced;
    public double? otherBusyCores;
    public long otherBusyWindowMs;
    public string? passRate10;
    public string? outcomeTail30;
    public bool saveChangedSincePass;
    public bool onMainMap;
    public bool onParallelSite;
    public string? errorSummary;
    public IReadOnlyList<RimRunnerAssertEntry> asserts => assertList;
    private readonly List<RimRunnerAssertEntry> assertList = [];
    private int assertsRecorded;

    internal void AddAssert(RimRunnerAssertEntry assertEntry)
    {
        assertEntry.ordinal = ++assertsRecorded;
        assertList.Add(assertEntry);
    }

    internal void RemoveAssert(RimRunnerAssertEntry assertEntry) => assertList.Remove(assertEntry);
    public List<RimRunnerDiagnosticEntry> diagnostics = [];
    public List<RimRunnerArtifactEntry> artifacts = [];
    public List<RimRunnerScreenshotEntry> screenshots = [];
    public List<RimRunnerReadoutTextEntry> readoutTexts = [];
    public string? exception;
    public List<RimRunnerErrorEntry> newErrors = [];
    public List<RimRunnerFindingEntry> sentinelFaults = [];
    public List<string> engineFaults = [];
    public List<RimRunnerWarningEntry> warnings = [];
    public List<RimRunnerWarningEntry> warningsAcked = [];
    public List<string> expectedLogs = [];
    public List<string> expectedLogsFresh = [];
    public List<RimRunnerLogAckEntry> logAcks = [];
    public List<string> newWarnings = [];
    public List<string> foreignErrors = [];
    public List<string> cleanupErrors = [];
    public List<string> degradedNotes = [];
    public List<string> degradedNotesFresh = [];
    public List<RimRunnerFindingEntry> failureFingerprints = [];

    internal List<RimRunnerSampleRecord> samples = [];

    internal List<RimRunnerFindingEntry> pendingFingerprints = [];
    internal List<string> pendingResidue = [];

    internal double otherBusyCoreMs;
    internal double otherBusyWallMs;

    internal bool endedByClaimedFault;

    internal readonly Dictionary<string, string> unreachableAcks = new(StringComparer.Ordinal);

    internal bool redBeforeBarrier;

    internal bool trippedCheckpointGate;

    internal List<RimRunnerShardLease> shardLeases = [];

    internal List<string> untakenWholeCells = [];

    internal bool wholeCellsEnumerated;

    internal int demotedAsserts;
}

public class RimRunnerLogAckEntry
{
    internal const string ErrorLevel = "error";
    internal const string WarningLevel = "warning";

    public required string level;
    public required string pattern;
    public required string reason;
    public List<string> stackAnchors = [];
    public int hits;
    public List<string> lines = [];
    public List<string> linesFresh = [];
    public int tolerance;
    public int? misses;
    public string? unreachable;
}

public class RimRunnerRejectionEntry
{
    public required string gate;
    public string? subject;
    public required string summary;
    public required string text;
}

public class RimRunnerEngineFaultEntry
{
    public required string at;
    public string? caseId;
    public required string where;
    public required string text;
}

public class RimRunnerFindingEntry
{
    public string? key;
    public string? subject;
    public required string text;

    internal string Line => subject == null ? text : subject + "：" + text;

    internal static RimRunnerFindingEntry Of(string? key, string? subject, string text) =>
        new() { key = key, subject = subject, text = text };
}

public class RimRunnerErrorEntry
{
    public required string text;
    public string? stack;
}

public class RimRunnerWarningEntry
{
    public string? subject;
    public required string key;
    public required string text;
    public string? reason;
    public bool? fresh;
    internal string? scope;
    internal string? origin;
    internal string? anchor;
}

public class RimRunnerReport
{
    internal HashSet<string> warningScopes = new(StringComparer.Ordinal);
    public string? timestamp;
    public required string? command;
    public string? modList;
    public string textureTier = RimRunnerEngineArgs.TextureTierNative;
    public string? ledgerFile;
    public string? seenLogsFile;
    public List<KeyValuePair<string, List<string>>> borrowed = [];
    internal List<RimRunnerInputRead> inputs = [];
    internal List<string> inputsUnread = [];
    public required string runId;
    public SortedDictionary<string, string> assemblyMvids = new(StringComparer.Ordinal);
    public List<string>? productCapabilities;
    public string? contentFingerprint;
    public RimRunnerShardInfo? shard;
    public int pass;
    public int noAssertions;
    public int fail;
    public int unclaimedFault;
    public int sentinelFault;
    public int engineFault;
    public int done;
    public int incomplete;
    public long durationMs;
    public long reloadMs;
    public int reloadCount;
    public int faultReloads;
    public int mapReloads;
    public int breakerObservations;
    public int breakerTrips;
    public int breakerPumpThreshold;
    public int breakerErrorThreshold;
    public List<string> ambientWindows = [];
    public string? customSave;
    public List<string> customSaveNotes = [];
    public List<string> ambientWindowsFresh = [];
    public int danglingProbeArms;
    public List<string> excludedCases = [];
    public List<string> groupSkippedCases = [];
    public SortedDictionary<string, string> inapplicableCases = new(StringComparer.Ordinal);
    public SortedDictionary<string, string> absentCases = new(StringComparer.Ordinal);
    public bool? envChecksClean;
    public List<string> checkpointBatches = [];
    public List<string> checkpointRetries = [];
    public List<string> footprints = [];
    public List<string> mainMap = [];
    public List<string> forcedMainMap = [];
    public SortedDictionary<string, string>? componentFirstFills;
    public string experiments = "";
    public string compatOff = "";
    public List<string> foreignStartupErrors = [];
    public List<string> claimedStartupErrors = [];
    public List<string> unattributedStartupErrors = [];
    public List<RimRunnerRejectionEntry> rejections = [];
    public List<RimRunnerRejectionEntry> caseRejections = [];
    public List<RimRunnerRejectionEntry> outOfBatchFaults = [];
    public List<RimRunnerWarningEntry> warnings = [];
    public List<RimRunnerWarningEntry> warningsAcked = [];
    public List<string> ledgerFaults = [];
    public List<RimRunnerEngineFaultEntry> engineFaults = [];
    public List<RimRunnerFindingEntry> batchSentinels = [];
    public List<RimRunnerFindingEntry> outputFaults = [];
    public List<string> reloadErrors = [];
    public List<RimRunnerFindingEntry> gapErrors = [];
    public int gapErrorCount;
    public List<RimRunnerFindingEntry> logCapHits = [];
    public int logCountPeak;
    public List<RimRunnerAckEntry> acks = [];
    public bool contractGatesReady;
    public long contractScanMs;
    public SortedDictionary<string, long>? memoryStart;
    public SortedDictionary<string, long>? memoryEnd;
    public List<RimRunnerResultEntry> regressions = [];
    public List<RimRunnerResultEntry> readouts = [];

    public int EntryCount
    {
        get
        {
            return regressions.Count + readouts.Count;
        }
    }

    public bool ContainsEntry(RimRunnerResultEntry entry)
    {
        return regressions.Contains(entry) || readouts.Contains(entry);
    }

    public void AddEntry(RimRunnerResultEntry entry)
    {
        if (RimRunnerRegistry.CountsAsRegression(entry.id))
        {
            regressions.Add(entry);
        }
        else
        {
            readouts.Add(entry);
        }
    }
}

public class RimRunnerShardInfo
{
    public required string group;
    public int index;
    public int count;
    public required string caseId;
    public int cells;
    public double weight;
    public required string fingerprint;
}

public sealed class RimRunnerAckEntry
{
    internal const string KeyKind = "key";
    internal const string StartupKind = "startup";
    internal const string KnownFailingKind = "knownFailing";

    public string? subject;
    public required string kind;
    public required string target;
    public string? origin;
    public string? anchor;
    public required string reason;
    public int hits;
    public int tolerance;
    public int? misses;
    public string? unreachable;
}
