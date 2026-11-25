namespace RimRunner;

[HarmonyPatch(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.AddHediff),
    typeof(Hediff), typeof(BodyPartRecord), typeof(DamageInfo?), typeof(DamageWorker.DamageResult))]
internal static class Patch_HealthTracker_AddHediff_RimRunnerAddedPartStack
{
    private static void Prefix(Pawn_HealthTracker __instance, Hediff hediff, BodyPartRecord part)
    {
        string? caseId = RimRunnerObservationBuffer.ActiveCaseId;
        if (caseId == null || hediff is not Hediff_AddedPart) return;
        Pawn? pawn = hediff.pawn ?? __instance.hediffSet.pawn;
        if (part == null)
        {
            RimRunnerSentinel.Raise(caseId + "：无部位挂植入体",
                pawn?.LabelShort + " 挂 " + hediff.def.defName);
            return;
        }
        Hediff? existing = __instance.hediffSet.hediffs.FirstOrDefault(h => h is Hediff_AddedPart && h.Part == part && h != hediff);
        if (existing == null) return;
        RimRunnerSentinel.Warn(RimRunnerWarningKey.AddedPartStack,
            "同槽叠挂植入体，旧件由 vanilla RestorePart 清除",
            pawn?.LabelShort + " 的 " + part.Label + " 已有 " + existing.def.defName + "，又挂 " + hediff.def.defName);
    }
}
