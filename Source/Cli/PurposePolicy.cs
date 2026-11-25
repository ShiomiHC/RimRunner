using System.Diagnostics.CodeAnalysis;

namespace RimRunnerCli;

public enum InstancePurpose
{
    Batch,
    Spare,
    Showcase,
    Film,
    Shard,
    NoCompat,
}

public enum ReapWaitMode
{
    None,
    StopRecorded,
    Settled,
}

public enum OutputLockKind
{
    None,
    Shared,
    Exclusive,
}

public sealed record PurposePolicy
{
    public required InstancePurpose Purpose { get; init; }
    public required string WireName { get; init; }
    public required string Noun { get; init; }
    public required int SortRank { get; init; }
    public required string EngineMode { get; init; }
    public required bool AllowsMultiplePerTree { get; init; }
    public required bool SkipsMountPreflight { get; init; }
    public required bool ProcessScoped { get; init; }
    public required ReapWaitMode ReapWait { get; init; }
    public required bool ReapRespectsShardHold { get; init; }
    public required string? ReapLockLabel { get; init; }
    public required string? ReapTag { get; init; }
    public required string? ReapRecordNoun { get; init; }
    public required bool BorrowableForShard { get; init; }
    public required bool ArchivesDiagnosticsOnUnmount { get; init; }
    public required OutputLockKind BatchOutputLock { get; init; }
    public required Func<RimRunnerConfig.MachineConfig, int> IdleMinutes { get; init; }
    public required bool SurvivesDrain { get; init; }
    public required SaveDataInitializer.PrefsProfile PrefsProfile { get; init; }
    public required bool ForcesNativeTextures { get; init; }
    public required bool UsesPlayerConfigBase { get; init; }
    public required string ReadyRunState { get; init; }
    public required bool ShowsWindow { get; init; }
    public required bool LocksHostWindowSize { get; init; }
    public required bool TakesLiveGrace { get; init; }
    public required bool ChecksFilmClockOnReuse { get; init; }
    public required bool ChecksCompatMirrorOnReuse { get; init; }
}

public static class PurposePolicies
{
    public static readonly PurposePolicy Batch = new()
    {
        Purpose = InstancePurpose.Batch,
        WireName = ClaimRecord.PurposeBatch,
        Noun = "自动实例",
        SortRank = 0,
        EngineMode = RimRunner.RimRunnerInstanceMode.Batch,
        AllowsMultiplePerTree = false,
        SkipsMountPreflight = false,
        ProcessScoped = false,
        ReapWait = ReapWaitMode.None,
        ReapRespectsShardHold = false,
        ReapLockLabel = null,
        ReapTag = null,
        ReapRecordNoun = null,
        BorrowableForShard = true,
        ArchivesDiagnosticsOnUnmount = false,
        BatchOutputLock = OutputLockKind.Shared,
        IdleMinutes = static machine => machine.BatchIdleMinutes.Value,
        SurvivesDrain = false,
        PrefsProfile = SaveDataInitializer.PrefsProfile.Batch,
        ForcesNativeTextures = false,
        UsesPlayerConfigBase = false,
        ReadyRunState = "idle",
        ShowsWindow = false,
        LocksHostWindowSize = true,
        TakesLiveGrace = true,
        ChecksFilmClockOnReuse = false,
        ChecksCompatMirrorOnReuse = false,
    };

    public static readonly PurposePolicy Spare = new()
    {
        Purpose = InstancePurpose.Spare,
        WireName = ClaimRecord.PurposeSpare,
        Noun = "临时自动实例",
        SortRank = 1,
        EngineMode = RimRunner.RimRunnerInstanceMode.Batch,
        AllowsMultiplePerTree = true,
        SkipsMountPreflight = true,
        ProcessScoped = true,
        ReapWait = ReapWaitMode.StopRecorded,
        ReapRespectsShardHold = false,
        ReapLockLabel = "临时实例收割",
        ReapTag = "[临时实例]",
        ReapRecordNoun = "临时自动实例的登记",
        BorrowableForShard = false,
        ArchivesDiagnosticsOnUnmount = false,
        BatchOutputLock = OutputLockKind.Shared,
        IdleMinutes = static machine => machine.BatchIdleMinutes.Value,
        SurvivesDrain = false,
        PrefsProfile = SaveDataInitializer.PrefsProfile.Batch,
        ForcesNativeTextures = false,
        UsesPlayerConfigBase = false,
        ReadyRunState = "idle",
        ShowsWindow = false,
        LocksHostWindowSize = true,
        TakesLiveGrace = true,
        ChecksFilmClockOnReuse = false,
        ChecksCompatMirrorOnReuse = false,
    };

    public static readonly PurposePolicy Showcase = new()
    {
        Purpose = InstancePurpose.Showcase,
        WireName = ClaimRecord.PurposeShowcase,
        Noun = "展台实例",
        SortRank = 2,
        EngineMode = RimRunner.RimRunnerInstanceMode.Showcase,
        AllowsMultiplePerTree = false,
        SkipsMountPreflight = false,
        ProcessScoped = false,
        ReapWait = ReapWaitMode.None,
        ReapRespectsShardHold = false,
        ReapLockLabel = null,
        ReapTag = null,
        ReapRecordNoun = null,
        BorrowableForShard = false,
        ArchivesDiagnosticsOnUnmount = false,
        BatchOutputLock = OutputLockKind.None,
        IdleMinutes = static machine => machine.ShowcaseIdleMinutes.Value,
        SurvivesDrain = true,
        PrefsProfile = SaveDataInitializer.PrefsProfile.Showcase,
        ForcesNativeTextures = true,
        UsesPlayerConfigBase = true,
        ReadyRunState = "idle",
        ShowsWindow = true,
        LocksHostWindowSize = false,
        TakesLiveGrace = false,
        ChecksFilmClockOnReuse = false,
        ChecksCompatMirrorOnReuse = false,
    };

    public static readonly PurposePolicy Film = new()
    {
        Purpose = InstancePurpose.Film,
        WireName = ClaimRecord.PurposeFilm,
        Noun = "拍摄实例",
        SortRank = 3,
        EngineMode = RimRunner.RimRunnerInstanceMode.Film,
        AllowsMultiplePerTree = false,
        SkipsMountPreflight = false,
        ProcessScoped = false,
        ReapWait = ReapWaitMode.None,
        ReapRespectsShardHold = false,
        ReapLockLabel = null,
        ReapTag = null,
        ReapRecordNoun = null,
        BorrowableForShard = false,
        ArchivesDiagnosticsOnUnmount = false,
        BatchOutputLock = OutputLockKind.None,
        IdleMinutes = static machine => machine.FilmIdleMinutes.Value,
        SurvivesDrain = false,
        PrefsProfile = SaveDataInitializer.PrefsProfile.Film,
        ForcesNativeTextures = true,
        UsesPlayerConfigBase = false,
        ReadyRunState = "running",
        ShowsWindow = false,
        LocksHostWindowSize = false,
        TakesLiveGrace = false,
        ChecksFilmClockOnReuse = true,
        ChecksCompatMirrorOnReuse = false,
    };

    public static readonly PurposePolicy Shard = new()
    {
        Purpose = InstancePurpose.Shard,
        WireName = ClaimRecord.PurposeShard,
        Noun = "分片实例",
        SortRank = 4,
        EngineMode = RimRunner.RimRunnerInstanceMode.Batch,
        AllowsMultiplePerTree = true,
        SkipsMountPreflight = true,
        ProcessScoped = true,
        ReapWait = ReapWaitMode.StopRecorded,
        ReapRespectsShardHold = true,
        ReapLockLabel = "分片收割",
        ReapTag = "[分片]",
        ReapRecordNoun = "分片登记",
        BorrowableForShard = false,
        ArchivesDiagnosticsOnUnmount = false,
        BatchOutputLock = OutputLockKind.None,
        IdleMinutes = static machine => machine.ShardIdleMinutes.Value,
        SurvivesDrain = false,
        PrefsProfile = SaveDataInitializer.PrefsProfile.Batch,
        ForcesNativeTextures = false,
        UsesPlayerConfigBase = false,
        ReadyRunState = "idle",
        ShowsWindow = false,
        LocksHostWindowSize = true,
        TakesLiveGrace = true,
        ChecksFilmClockOnReuse = false,
        ChecksCompatMirrorOnReuse = false,
    };

    public static readonly PurposePolicy NoCompat = new()
    {
        Purpose = InstancePurpose.NoCompat,
        WireName = ClaimRecord.PurposeNoCompat,
        Noun = "兼容关闭实例",
        SortRank = 5,
        EngineMode = RimRunner.RimRunnerInstanceMode.Batch,
        AllowsMultiplePerTree = false,
        SkipsMountPreflight = false,
        ProcessScoped = true,
        ReapWait = ReapWaitMode.Settled,
        ReapRespectsShardHold = false,
        ReapLockLabel = "随进程收割",
        ReapTag = "[兼容关闭]",
        ReapRecordNoun = "兼容关闭实例的登记",
        BorrowableForShard = false,
        ArchivesDiagnosticsOnUnmount = true,
        BatchOutputLock = OutputLockKind.Exclusive,
        IdleMinutes = static machine => machine.BatchIdleMinutes.Value,
        SurvivesDrain = false,
        PrefsProfile = SaveDataInitializer.PrefsProfile.Batch,
        ForcesNativeTextures = false,
        UsesPlayerConfigBase = false,
        ReadyRunState = "idle",
        ShowsWindow = false,
        LocksHostWindowSize = true,
        TakesLiveGrace = true,
        ChecksFilmClockOnReuse = false,
        ChecksCompatMirrorOnReuse = true,
    };

#pragma warning disable CS8524
    public static PurposePolicy For(InstancePurpose purpose) => purpose switch
    {
        InstancePurpose.Batch => Batch,
        InstancePurpose.Spare => Spare,
        InstancePurpose.Showcase => Showcase,
        InstancePurpose.Film => Film,
        InstancePurpose.Shard => Shard,
        InstancePurpose.NoCompat => NoCompat,
    };
#pragma warning restore CS8524

    public static bool IsAuto(InstancePurpose purpose) =>
        purpose is InstancePurpose.Batch or InstancePurpose.Spare;

    public static readonly IReadOnlyList<PurposePolicy> All =
        [.. Enum.GetValues<InstancePurpose>().Select(For)];

    private static readonly Dictionary<string, PurposePolicy> ByWire =
        All.ToDictionary(static policy => policy.WireName, StringComparer.Ordinal);

    public static bool TryParse(string? wire, out InstancePurpose purpose)
    {
        if (wire != null && ByWire.TryGetValue(wire, out PurposePolicy? policy))
        {
            purpose = policy.Purpose;
            return true;
        }

        purpose = default;
        return false;
    }

    public static bool TryForWire(string? wire, [NotNullWhen(true)] out PurposePolicy? policy)
    {
        if (wire != null && ByWire.TryGetValue(wire, out policy))
            return true;
        policy = null;
        return false;
    }

    public static InstancePurpose ParseEffective(string? wire)
    {
        if (wire == null)
            return InstancePurpose.Batch;
        if (ByWire.TryGetValue(wire, out PurposePolicy? policy))
            return policy.Purpose;
        throw new ArgumentOutOfRangeException(nameof(wire), wire, "未知的实例用途");
    }

    public static PurposePolicy ForEffective(string? wire) => For(ParseEffective(wire));

    public static PurposePolicy ForWire(string wire) =>
        ByWire.TryGetValue(wire, out PurposePolicy? policy)
            ? policy
            : throw new ArgumentOutOfRangeException(nameof(wire), wire, "未知的实例用途");
}
