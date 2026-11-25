using Verse.AI;
using Verse.AI.Group;

namespace RimRunner;

internal static class RimRunnerThreatDisable
{
    internal static readonly DutyDef Def = new()
    {
        defName = "RimRunner_ThreatDisabled",
        threatDisabled = true,
        thinkNode = new ThinkNode_Priority()
    };

    internal static readonly RimRunnerPawnFixtureSet Registered = new("ctx.DisableThreat", pawn =>
    {
        if (pawn.mindState?.duty?.def == Def) pawn.mindState.duty = null;
    });

    internal static readonly HashSet<Pawn> Kept = [];

    internal static bool Keeps(Pawn pawn) => Kept.Contains(pawn) && Registered.Contains(pawn);

    internal static void Forget(Pawn pawn)
    {
        Registered.Remove(pawn);
        Kept.Remove(pawn);
    }

    internal static void Clear()
    {
        Registered.Clear();
        Kept.Clear();
    }

    internal static void ReapplyAfterCheckpointLoad()
    {
        if (Registered.Count == 0 && Kept.Count == 0) return;
        HashSet<Pawn> refreshedKept = [];
        foreach (Map map in Find.Maps)
        {
            foreach (Pawn pawn in map.mapPawns.AllPawns)
            {
                if (Kept.Contains(pawn)) refreshedKept.Add(pawn);
                if (pawn.mindState != null && pawn.mindState.duty == null && Registered.Contains(pawn))
                    pawn.mindState.duty = new PawnDuty(Def);
            }
        }

        foreach (Pawn pawn in Find.WorldPawns.AllPawnsAliveOrDead)
        {
            if (Kept.Contains(pawn)) refreshedKept.Add(pawn);
        }

        Kept.Clear();
        Kept.UnionWith(refreshedKept);
    }
}

[HarmonyPatch(typeof(Pawn_MindState), nameof(Pawn_MindState.ExposeData))]
internal static class Patch_PawnMindState_ExposeData_RimRunnerThreatDisable
{
    private static bool Prepare()
    {
        return GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable);
    }

    private static void Prefix(Pawn_MindState __instance, out PawnDuty? __state)
    {
        __state = null;
        if (Scribe.mode != LoadSaveMode.Saving || __instance.duty?.def != RimRunnerThreatDisable.Def) return;
        __state = __instance.duty;
        __instance.duty = null;
    }

    private static Exception? Finalizer(Exception? __exception, Pawn_MindState __instance, PawnDuty? __state)
    {
        if (__state != null) __instance.duty = __state;
        return __exception;
    }
}

[HarmonyPatch(typeof(Pawn_MindState), nameof(Pawn_MindState.Reset), typeof(bool), typeof(bool), typeof(bool))]
internal static class Patch_PawnMindState_Reset_RimRunnerThreatDisable
{
    private static bool Prepare()
    {
        return GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable);
    }

    private static void Postfix(Pawn_MindState __instance)
    {
        Pawn pawn = __instance.pawn;
        if (pawn == null || !RimRunnerThreatDisable.Keeps(pawn)) return;
        if (pawn.Dead || pawn.Destroyed || pawn.GetLord() != null || __instance.duty != null) return;
        __instance.duty = new PawnDuty(RimRunnerThreatDisable.Def);
    }
}
