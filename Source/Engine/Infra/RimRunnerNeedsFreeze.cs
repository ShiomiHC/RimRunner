namespace RimRunner;

/// <summary>需求冻结：冻结登记 pawn 的全部需求自然推进。</summary>
/// <remarks>冻结全部需求的自然推进、营养不良增减、心情收敛与 thought 更新；需求增删与显式写 CurLevel 照常生效，崩溃照按冻结的心情掷骰。离图的世界 pawn 同样冻结。</remarks>
public static class RimRunnerNeedsFreeze
{
    private static readonly RimRunnerPawnFixtureSet Frozen = new(
        "RimRunnerNeedsFreeze.Add");

    private static readonly Dictionary<string, List<Pawn>> FrozenCases = new(StringComparer.Ordinal);
    private static string? segmentCaseId;

    private static readonly Dictionary<string, HashSet<string>> BlockedCases = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, HashSet<string>> ReadAfterBlockCases = new(StringComparer.Ordinal);
    private static HashSet<string>? blockedThisSegment;

    internal static bool FrozenThisCase => segmentCaseId != null && FrozenCases.ContainsKey(segmentCaseId);

    internal static IReadOnlyCollection<string> ReadAfterBlockThisCase
        => segmentCaseId != null && ReadAfterBlockCases.TryGetValue(segmentCaseId, out HashSet<string>? ids) ? ids : [];

    internal static bool BlockedThisCase(Pawn pawn) => blockedThisSegment?.Contains(pawn.ThingID) ?? false;

    internal static void NoteBlocked(Pawn pawn)
    {
        if (segmentCaseId == null) return;
        if (!BlockedCases.TryGetValue(segmentCaseId, out HashSet<string>? ids)) BlockedCases[segmentCaseId] = ids = new(StringComparer.Ordinal);
        ids.Add(pawn.ThingID);
        blockedThisSegment = ids;
    }

    internal static void NoteRead(Pawn? pawn)
    {
        HashSet<string>? blocked = blockedThisSegment;
        if (blocked == null || pawn == null || RimRunnerTimeDriver.Advancing
            || RimRunnerContext.AmbientPhase != RimRunnerPhase.Case || !blocked.Contains(pawn.ThingID))
        {
            return;
        }

        if (!ReadAfterBlockCases.TryGetValue(segmentCaseId!, out HashSet<string>? ids)) ReadAfterBlockCases[segmentCaseId!] = ids = new(StringComparer.Ordinal);
        ids.Add(pawn.ThingID);
    }

    internal static IReadOnlyList<Pawn> FrozenPawnsThisCase
        => segmentCaseId != null && FrozenCases.TryGetValue(segmentCaseId, out List<Pawn>? pawns) ? pawns : [];

    internal static void BeginSegment(string caseId, bool continuesPreviousPhase)
    {
        segmentCaseId = caseId;
        if (!continuesPreviousPhase)
        {
            FrozenCases.Remove(caseId);
            BlockedCases.Remove(caseId);
            ReadAfterBlockCases.Remove(caseId);
        }

        blockedThisSegment = BlockedCases.TryGetValue(caseId, out HashSet<string>? ids) ? ids : null;
    }

    /// <summary>登记冻结；撤销走 Remove，ctx.SuspendNeeds 登记同一冻结并随用例撤下。</summary>
    /// <remarks>登记保留到 Remove 或该 pawn 经 RimRunnerPawns.Cleanup 回收；用例末（多阶段为 phase 末）仍在、不随交棒走的由引擎撤下并记 FIXTURE-RESIDUE。</remarks>
    public static void Add(Pawn pawn)
    {
        if (pawn == null) throw new ArgumentNullException(nameof(pawn));
        Frozen.Add(pawn);
        if (segmentCaseId == null) return;
        if (!FrozenCases.TryGetValue(segmentCaseId, out List<Pawn>? pawns)) FrozenCases[segmentCaseId] = pawns = [];
        if (!pawns.Contains(pawn)) pawns.Add(pawn);
    }

    /// <summary>冻结撤销：撤下该 pawn 的冻结；未冻结时无操作。</summary>
    public static void Remove(Pawn? pawn)
    {
        if (pawn != null) Frozen.Remove(pawn);
    }

    internal static void ClearForNewWorld()
    {
        Frozen.Clear();
        FrozenCases.Clear();
        BlockedCases.Clear();
        ReadAfterBlockCases.Clear();
        blockedThisSegment = null;
        segmentCaseId = null;
    }

    /// <summary>冻结查询：该 pawn 的需求是否被冻结。</summary>
    public static bool IsFrozen(Pawn? pawn)
    {
        return pawn != null && Frozen.Contains(pawn);
    }
}

[HarmonyPatch(typeof(Pawn_NeedsTracker), nameof(Pawn_NeedsTracker.NeedsTrackerTickInterval))]
internal static class Patch_Pawn_NeedsTracker_NeedsTrackerTickInterval_RimRunnerFreeze
{
    private static bool Prepare()
    {
        return GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable);
    }

    private static bool Prefix(Pawn ___pawn)
    {
        if (!RimRunnerNeedsFreeze.IsFrozen(___pawn)) return true;
        RimRunnerNeedsFreeze.NoteBlocked(___pawn);
        return false;
    }
}

[HarmonyPatch(typeof(Need), nameof(Need.CurLevel), MethodType.Getter)]
internal static class Patch_Need_CurLevel_RimRunnerFrozenRead
{
    private static bool Prepare()
    {
        return GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable);
    }

    private static void Prefix(Pawn ___pawn)
    {
        RimRunnerNeedsFreeze.NoteRead(___pawn);
    }
}
