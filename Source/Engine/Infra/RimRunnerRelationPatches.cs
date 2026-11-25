namespace RimRunner;

[HarmonyPatch(typeof(Pawn_RelationsTracker), nameof(Pawn_RelationsTracker.AddDirectRelation), typeof(PawnRelationDef), typeof(Pawn))]
internal static class Patch_Pawn_RelationsTracker_AddDirectRelation_RimRunnerSentinel
{
    private static void Postfix(Pawn ___pawn, PawnRelationDef def, Pawn otherPawn)
    {
        string? caseId = RimRunnerObservationBuffer.ActiveCaseId;
        if (caseId == null || def == null || otherPawn == null || ___pawn == null || otherPawn == ___pawn)
        {
            return;
        }

        if (InRelationAfterWrite(def, ___pawn, otherPawn))
        {
            return;
        }

        RimRunnerSentinel.Warn(RimRunnerWarningKey.DerivedRelation, caseId + "：加直接关系后按关系查询无法枚举出这条关系",
            ___pawn.LabelShort + " " + def.defName + " → " + otherPawn.LabelShort);
    }

    private static bool InRelationAfterWrite(PawnRelationDef def, Pawn pawn, Pawn other)
        => (!def.implied || def.Worker.GetType().GetMethod(nameof(PawnRelationWorker.InRelation))!.DeclaringType != typeof(PawnRelationWorker))
           && def.Worker.InRelation(pawn, other);
}

[HarmonyPatch(typeof(PawnRelationUtility), nameof(PawnRelationUtility.Notify_PawnsSeenByPlayer),
    [typeof(IEnumerable<Pawn>), typeof(string), typeof(bool), typeof(bool)],
    [ArgumentType.Normal, ArgumentType.Out, ArgumentType.Normal, ArgumentType.Normal])]
internal static class Patch_PawnRelationUtility_Notify_PawnsSeenByPlayer_RimRunnerSentinel
{
    private static void Postfix(IEnumerable<Pawn> seenPawns, bool informEvenIfSeenBefore)
    {
        string? caseId = RimRunnerObservationBuffer.ActiveCaseId;
        if (caseId == null || seenPawns == null)
        {
            return;
        }

        foreach (Pawn seenPawn in seenPawns)
        {
            if (seenPawn == null || !seenPawn.RaceProps.IsFlesh || seenPawn.relations == null)
            {
                continue;
            }

            if (!informEvenIfSeenBefore && seenPawn.relations.everSeenByPlayer)
            {
                continue;
            }

            if (!(seenPawn.Spawned && seenPawn.Fogged()))
            {
                continue;
            }

            if (PawnRelationUtility.GetMostImportantColonyRelative(seenPawn) == null)
            {
                continue;
            }

            RimRunnerSentinel.Warn(RimRunnerWarningKey.FoggedKin, caseId + "：pawn 有殖民地亲属但在迷雾里，"
                                    + "vanilla 的「见到亲属」通知按 Fogged 跳过该 pawn",
                seenPawn.LabelShort);
        }
    }
}
