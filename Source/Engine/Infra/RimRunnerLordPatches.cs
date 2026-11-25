using Verse.AI.Group;

namespace RimRunner;

internal static class RimRunnerGatheringFaction
{
    private static string? trackedCase;
    private static readonly Dictionary<int, HashSet<string>> mismatchedStarters = [];

    internal static bool InRange(LordJob? job) => job is LordJob_Joinable_Gathering and not LordJob_Ritual;

    internal static void Track(string caseId, Lord lord, IEnumerable<Pawn> startingPawns)
    {
        if (trackedCase != caseId)
        {
            mismatchedStarters.Clear();
            trackedCase = caseId;
        }

        HashSet<string> ids = [.. startingPawns.Where(pawn => pawn != null && pawn.Faction != lord.faction)
            .Select(pawn => pawn.ThingID)];
        if (ids.Count > 0) mismatchedStarters[lord.loadID] = ids;
    }

    internal static bool TakeMismatchedStarter(string caseId, Lord lord, Pawn pawn)
    {
        return trackedCase == caseId
               && mismatchedStarters.TryGetValue(lord.loadID, out HashSet<string>? ids)
               && ids.Remove(pawn.ThingID);
    }
}

[HarmonyPatch(typeof(LordMaker), nameof(LordMaker.MakeNewLord))]
internal static class Patch_LordMaker_MakeNewLord_RimRunnerGatheringFaction
{
    private static void Postfix(Lord __result, LordJob lordJob, IEnumerable<Pawn> startingPawns)
    {
        string? caseId = RimRunnerObservationBuffer.ActiveCaseId;
        if (caseId == null || __result == null || startingPawns == null || !RimRunnerGatheringFaction.InRange(lordJob)) return;
        RimRunnerGatheringFaction.Track(caseId, __result, startingPawns);
    }
}

[HarmonyPatch(typeof(Lord), nameof(Lord.Notify_PawnLost))]
internal static class Patch_Lord_NotifyPawnLost_RimRunnerGatheringFaction
{
    private static bool Prepare()
    {
        return GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable);
    }

    private static void Postfix(Lord __instance, Pawn pawn, PawnLostCondition cond)
    {
        if (cond != PawnLostCondition.LeftVoluntarily || pawn == null) return;
        string? caseId = RimRunnerObservationBuffer.ActiveCaseId;
        if (caseId == null || !RimRunnerGatheringFaction.InRange(__instance.LordJob)
            || pawn.Faction == __instance.faction
            || !RimRunnerGatheringFaction.TakeMismatchedStarter(caseId, __instance, pawn))
        {
            return;
        }

        RimRunnerSentinel.Warn(RimRunnerWarningKey.LordFaction,
            caseId + "：自愿加入编队的起始成员因派系与 lord 派系不匹配而离队",
            __instance.LordJob.GetType().Name + " 派系 " + (__instance.faction?.Name ?? "null") + " ↔ 成员 " + pawn.LabelShort
            + "（" + (pawn.Faction?.Name ?? "null") + "）于 tick " + Find.TickManager.TicksGame);
    }
}
